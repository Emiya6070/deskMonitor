using System.Diagnostics;
using System.Text.Json;
using DeskMonitor.Core;

internal static class FakeCodexServer
{
    internal const string ModeVariable = "DESKMONITOR_TEST_CODEX_MODE";

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
        var previous = Environment.GetEnvironmentVariable(FakeCodexServer.ModeVariable);
        try
        {
            async Task<CodexUsage> Read(string mode, CancellationToken token = default)
            {
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
                check((await Read("success")).Primary is not null, $"quota can refresh after {mode}");
            }
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            elapsed.Restart();
            var cancelled = false;
            try { await Read("hang", cancel.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
            check(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(6), "cancelling a stuck quota process completes promptly");
            check((await Read("success")).Primary is not null, "quota refresh recovers after cancellation");
            elapsed.Restart();
            var timedOut = false;
            try { await Read("hang"); }
            catch (IOException ex) when (ex.Message.Contains("超时")) { timedOut = true; }
            check(timedOut && elapsed.Elapsed < TimeSpan.FromSeconds(27), "quota request timeout also bounds process cleanup");
            check((await Read("success")).Primary is not null, "quota refresh recovers after a request timeout");
        }
        finally { Environment.SetEnvironmentVariable(FakeCodexServer.ModeVariable, previous); }
    }
}
