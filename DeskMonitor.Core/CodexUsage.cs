using System.Diagnostics;
using System.Text.Json;

namespace DeskMonitor.Core;

public sealed record QuotaWindow(double UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public string Label => DurationMinutes switch
    {
        10080 => "每周", 300 => "5 小时", null => "额度",
        int n when n % 1440 == 0 => $"{n / 1440} 天",
        int n when n % 60 == 0 => $"{n / 60} 小时", int n => $"{n} 分钟"
    };
    public bool AwaitingReset(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
}

public sealed record CodexUsage(QuotaWindow? Primary, QuotaWindow? Secondary, DateTimeOffset FetchedAt)
{
    public bool IsStale(DateTimeOffset now) => now - FetchedAt > TimeSpan.FromMinutes(2);
    public static CodexUsage Parse(JsonElement result, DateTimeOffset fetchedAt)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Codex 额度响应格式无效。");
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind != JsonValueKind.Null)
        {
            if (map.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Codex 额度分类格式无效。");
            if (!map.TryGetProperty("codex", out bucket)) throw new InvalidDataException("账户未返回 Codex 额度。");
        }
        else if (!result.TryGetProperty("rateLimits", out bucket)) throw new InvalidDataException("账户未返回 Codex 额度。");
        if (bucket.ValueKind != JsonValueKind.Object) throw new InvalidDataException("账户未返回 Codex 额度。");
        if (bucket.TryGetProperty("limitId", out var id) && id.ValueKind != JsonValueKind.Null && (id.ValueKind != JsonValueKind.String || id.GetString() != "codex"))
            throw new InvalidDataException("返回的额度不属于 Codex。");
        var primary = ReadWindow(bucket, "primary");
        var secondary = ReadWindow(bucket, "secondary");
        if (primary is null && secondary is null) throw new InvalidDataException("账户未提供额度窗口，请检查 Codex 的 ChatGPT 登录状态。");
        return new(primary, secondary, fetchedAt);
    }
    private static QuotaWindow? ReadWindow(JsonElement bucket, string name)
    {
        if (!bucket.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("usedPercent", out var used)
            || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0)
            throw new InvalidDataException("Codex 已用百分比无效。");
        int? duration = null;
        if (value.TryGetProperty("windowDurationMins", out var mins) && mins.ValueKind != JsonValueKind.Null)
        {
            if (mins.ValueKind != JsonValueKind.Number || !mins.TryGetInt32(out var n) || n <= 0) throw new InvalidDataException("Codex 额度周期无效。");
            duration = n;
        }
        DateTimeOffset? reset = null;
        if (value.TryGetProperty("resetsAt", out var timestamp) && timestamp.ValueKind != JsonValueKind.Null)
        {
            if (timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out var seconds) || seconds <= 0 || seconds > 253402300799)
                throw new InvalidDataException("Codex 重置时间无效。");
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return new(percent, duration, reset);
    }
}

public static class CodexUsageClient
{
    public static string ResolveExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath) || !File.Exists(configuredPath) || !configuredPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择有效的 Codex .exe 完整路径。");
            return configuredPath;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[] { Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(Path.IsPathFullyQualified).Select(p => Path.Combine(p, "codex.exe")));
        return candidates.FirstOrDefault(File.Exists) ?? throw new IOException("未找到 Codex；请在设置中选择 codex.exe，并在 Codex 中登录 ChatGPT。");
    }
    public static async Task<CodexUsage> ReadAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        try { return await ReadOnceAsync(configuredPath, cancellationToken); }
        catch (CodexRpcException ex) when (ex.Retryable && !cancellationToken.IsCancellationRequested)
        {
            // Observed account/rateLimits/read returning -32603 and succeeding on
            // the next read. Retry only once, after the failed process is cleaned up.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return await ReadOnceAsync(configuredPath, cancellationToken);
        }
    }
    private static async Task<CodexUsage> ReadOnceAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var start = new ProcessStartInfo(ResolveExecutable(configuredPath))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex 额度查询。");
        // Drain, but never retain or display server logs (which may contain private paths).
        using var drainCancellation = new CancellationTokenSource();
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, drainCancellation.Token);
        var stage = "初始化";
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"desk_monitor\",\"title\":\"DeskMonitor\",\"version\":\"0.3.3\"}}}".AsMemory(), token);
            await ReadResponseAsync(process, 1, token);
            stage = "读取额度";
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), token);
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}".AsMemory(), token);
            var response = await ReadResponseAsync(process, 2, token);
            return CodexUsage.Parse(response, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException($"Codex {stage}超时；请检查网络和登录状态。"); }
        finally
        {
            try
            {
                process.StandardInput.Close();
                using var exitWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitWait.CancelAfter(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(exitWait.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.WaitForExitAsync(killWait.Token); }
                    catch (OperationCanceledException ex) { throw new IOException("Codex 查询进程未及时退出。", ex); }
                }
            }
            finally
            {
                // A descendant can inherit stderr even after app-server exits. Never wait
                // for pipe EOF indefinitely: that would keep every future refresh disabled.
                drainCancellation.Cancel();
                try { await drain.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested) { }
                catch (TimeoutException ex) { throw new IOException("Codex 查询日志管道未及时关闭。", ex); }
            }
        }
    }
    private static async Task<JsonElement> ReadResponseAsync(Process process, int id, CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token) ?? throw new IOException("Codex 查询进程提前退出。");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number || value.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out var error))
            {
                // Keep the error code for diagnosis without displaying raw server messages,
                // which can contain private paths, URLs or account details.
                int? code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var number)
                    && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var n) ? n : null;
                var label = code is null ? "" : $"（错误码 {code}）";
                throw new CodexRpcException($"Codex {(id == 1 ? "初始化" : "读取账户额度")}失败{label}；请稍后重试。", id == 2 && code == -32603);
            }
            if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException("Codex 查询响应缺少结果。");
            return result.Clone();
        }
    }
    private sealed class CodexRpcException(string message, bool retryable) : IOException(message)
    {
        public bool Retryable { get; } = retryable;
    }
}
