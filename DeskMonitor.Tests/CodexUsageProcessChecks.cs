using System.Diagnostics;
using System.Text.Json;
using DeskMonitor.Core;

internal static class FakeCodexServer
{
    internal const string ModeVariable = "DESKMONITOR_TEST_CODEX_MODE";
    internal const string AttemptVariable = "DESKMONITOR_TEST_CODEX_ATTEMPT_FILE";

    public static async Task<bool> RunAsync(string[] args)
    {
        if (args.SequenceEqual(new[] { "--hold-stderr" }))
        {
            await Task.Delay(8000);
            return true;
        }
        if (!args.SequenceEqual(new[] { "app-server", "--stdio" })) return false;
        var mode = Environment.GetEnvironmentVariable(ModeVariable)
            ?? throw new InvalidOperationException("Missing fake server mode.");
        var attempt = 1;
        if (Environment.GetEnvironmentVariable(AttemptVariable) is { } attemptFile)
        {
            attempt = int.Parse(await File.ReadAllTextAsync(attemptFile)) + 1;
            await File.WriteAllTextAsync(attemptFile, attempt.ToString());
        }
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var message = JsonDocument.Parse(line);
            if (!message.RootElement.TryGetProperty("id", out var id)) continue;
            if (id.GetInt32() == 1)
            {
                Console.WriteLine("""{"id":1,"result":{}}""");
                continue;
            }
            if (mode == "hang") { await Task.Delay(60000); return true; }
            if (mode == "eof") return true;
            if (mode == "stderr-child")
            {
                using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    ArgumentList = { "--hold-stderr" }
                })!;
            }
            Console.WriteLine(mode switch
            {
                "internal-always" => """{"id":2,"error":{"code":-32603,"message":"test internal error"}}""",
                "internal-once" when attempt == 1 => """{"id":2,"error":{"code":-32603,"message":"test internal error"}}""",
                "rpc-error" => """{"id":2,"error":{"code":-32000,"message":"test error"}}""",
                "missing-quota" => """{"id":2,"result":{"rateLimits":{"primary":null,"secondary":null}}}""",
                _ => """{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000}}}}"""
            });
            return true;
        }
        return true;
    }
}

internal static class CodexUsageProcessChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        CheckExecutableDiscovery(check);
        var previous = Environment.GetEnvironmentVariable(FakeCodexServer.ModeVariable);
        var previousAttempt = Environment.GetEnvironmentVariable(FakeCodexServer.AttemptVariable);
        var attemptFile = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable(FakeCodexServer.AttemptVariable, attemptFile);
            async Task<CodexUsage> Read(string mode, CancellationToken token = default)
            {
                await File.WriteAllTextAsync(attemptFile, "0");
                Environment.SetEnvironmentVariable(FakeCodexServer.ModeVariable, mode);
                return await CodexUsageClient.ReadAsync(Environment.ProcessPath!, token);
            }

            check((await Read("success")).Primary!.RemainingPercent == 75, "quota stdio handshake reads a real child process");
            var elapsed = Stopwatch.StartNew();
            check((await Read("stderr-child")).Primary!.RemainingPercent == 75, "quota read survives a child retaining the stderr pipe");
            check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "stderr cleanup cannot hold the quota refresh busy");
            foreach (var mode in new[] { "rpc-error", "eof", "missing-quota" })
            {
                var rejected = false;
                try { await Read(mode); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
                check(rejected, $"quota {mode} is reported as a failure");
                check(await File.ReadAllTextAsync(attemptFile) == "1", $"quota {mode} is not retried internally");
                check((await Read("success")).Primary is not null, $"quota can refresh after {mode}");
            }
            check((await Read("internal-once")).Primary is not null && await File.ReadAllTextAsync(attemptFile) == "2", "transient -32603 is recovered by one delayed retry");
            var persistent = false;
            try { await Read("internal-always"); }
            catch (IOException ex) when (ex.Message.Contains("-32603")) { persistent = true; }
            check(persistent && await File.ReadAllTextAsync(attemptFile) == "2", "persistent -32603 stops after two attempts and retains the error code");
            using var cancelRetry = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var retryCancelled = false;
            try { await Read("internal-always", cancelRetry.Token); }
            catch (OperationCanceledException) when (cancelRetry.IsCancellationRequested) { retryCancelled = true; }
            check(retryCancelled && await File.ReadAllTextAsync(attemptFile) == "1", "cancelling retry delay prevents a second process");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            elapsed.Restart();
            var cancelled = false;
            try { await Read("hang", cancel.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
            check(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(2), "cancelling a stuck quota process skips the two-second exit grace");
            check((await Read("success")).Primary is not null, "quota refresh recovers after cancellation");
            elapsed.Restart();
            var timedOut = false;
            try { await Read("hang"); }
            catch (IOException ex) when (ex.Message.Contains("超时")) { timedOut = true; }
            check(timedOut && elapsed.Elapsed < TimeSpan.FromSeconds(27), "quota request timeout also bounds process cleanup");
            check((await Read("success")).Primary is not null, "quota refresh recovers after a request timeout");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeCodexServer.ModeVariable, previous);
            Environment.SetEnvironmentVariable(FakeCodexServer.AttemptVariable, previousAttempt);
            File.Delete(attemptFile);
        }
    }

    private static void CheckExecutableDiscovery(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DeskMonitor-codex-path-{Guid.NewGuid():N}");
        var local = Path.Combine(root, "local");
        var desktopBin = Path.Combine(local, "OpenAI", "Codex", "bin");
        var writtenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string CreateExecutable(string path, DateTime modifiedAt)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, modifiedAt);
            return path;
        }
        try
        {
            var v2 = CreateExecutable(Path.Combine(desktopBin, "v2", "codex.exe"), writtenAt);
            check(CodexUsageClient.ResolveExecutable(null, local, "") == v2,
                "Codex desktop version directory is discovered without PATH");
            var v1 = CreateExecutable(Path.Combine(desktopBin, "v1", "codex.exe"), writtenAt.AddHours(-1));
            check(CodexUsageClient.ResolveExecutable(null, local, "") == v2,
                "Codex desktop discovery selects the most recently written executable");
            File.SetLastWriteTimeUtc(v1, writtenAt);
            check(CodexUsageClient.ResolveExecutable(null, local, "") == v1,
                "equal Codex desktop executable timestamps use a stable alphabetical path order");
            var v3 = CreateExecutable(Path.Combine(desktopBin, "v3", "codex.exe"), writtenAt.AddHours(1));
            check(CodexUsageClient.ResolveExecutable(null, local, "") == v3,
                "Codex desktop installation updates are discovered on the next lookup");
            Directory.CreateDirectory(Path.Combine(desktopBin, "v4-incomplete"));
            check(CodexUsageClient.ResolveExecutable(null, local, "") == v3,
                "an incomplete Codex desktop version without an executable does not affect discovery");

            var pathExe = CreateExecutable(Path.Combine(root, "path", "codex.exe"), writtenAt.AddHours(2));
            var searchPath = Path.GetDirectoryName(pathExe)!;
            check(CodexUsageClient.ResolveExecutable(null, local, searchPath) == v3,
                "Codex desktop installation is preferred to PATH");
            var legacy = CreateExecutable(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"), writtenAt.AddHours(-2));
            check(CodexUsageClient.ResolveExecutable(null, local, searchPath) == legacy,
                "the existing standard Codex installation retains discovery priority");
            check(CodexUsageClient.ResolveExecutable(null, Path.Combine(root, "empty-local"), searchPath) == pathExe,
                "Codex PATH discovery remains available when standard installations are absent");
            check(CodexUsageClient.ResolveExecutable(pathExe, local, searchPath) == pathExe,
                "an explicitly configured Codex executable takes priority over discovery");
            var nonExecutable = CreateExecutable(Path.Combine(root, "codex.txt"), writtenAt);
            foreach (var invalid in new[] { Path.Combine(root, "missing.exe"), "codex.exe", nonExecutable })
            {
                var rejected = false;
                try { CodexUsageClient.ResolveExecutable(invalid, local, searchPath); }
                catch (IOException ex) when (ex.Message.Contains("有效的 Codex .exe")) { rejected = true; }
                check(rejected, "an invalid configured Codex path is rejected instead of silently replaced");
            }
            var missing = false;
            try { CodexUsageClient.ResolveExecutable(null, Path.Combine(root, "empty-local"), ""); }
            catch (IOException ex) when (ex.Message.Contains("未找到 Codex")) { missing = true; }
            check(missing, "missing Codex installations produce an explicit discovery error");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
