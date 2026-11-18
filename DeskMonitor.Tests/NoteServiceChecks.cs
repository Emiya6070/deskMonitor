using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DeskMonitor.Core;

internal static class NoteServiceChecks
{
    public static async Task RunAsync(string serviceDll, Action<bool, string> check)
    {
        serviceDll = Path.GetFullPath(serviceDll);
        var folder = Path.Combine(Path.GetTempPath(), "deskmonitor-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var address = $"http://127.0.0.1:{port}";
        Process Start()
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(serviceDll); info.ArgumentList.Add("--urls"); info.ArgumentList.Add(address);
            info.Environment["NOTES_API_TOKEN"] = token; info.Environment["NOTES_DATA_FILE"] = Path.Combine(folder, "notes.json");
            info.Environment["NOTES_PUBLIC_URL"] = address;
            info.Environment["NOTES_OAUTH_CLIENT_SECRET"] = token + "oauth";
            var process = Process.Start(info)!;
            process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
        }
        async Task WaitReady(Process process, HttpClient http)
        {
            for (var i = 0; i < 50; i++)
            {
                if (process.HasExited) throw new Exception("Notes service exited before becoming ready.");
                try { using var result = await http.GetAsync("notes"); if (result.StatusCode == HttpStatusCode.Unauthorized) return; }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            throw new TimeoutException("Notes service did not become ready.");
        }
        Process? process = null;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(address + "/"), Timeout = TimeSpan.FromSeconds(5) };
            process = Start(); await WaitReady(process, http);
            using (var response = await http.GetAsync("notes")) check(response.StatusCode == HttpStatusCode.Unauthorized, "service: list requires authentication");
            var input = new NoteInput("网页聊天分析", "结论和下一步", "完整分析正文", "https://chatgpt.com/c/example", 0);
            using (var response = await http.PutAsJsonAsync("notes/test", input)) check(response.StatusCode == HttpStatusCode.Unauthorized, "service: writes require authentication");
            using var client = new NoteServiceClient(address, token);
            var note = await client.PutAsync("test", input);
            check((await client.ListAsync()).Single() == note, "service: desktop client reads chat-created note");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using (var response = await http.PutAsJsonAsync("notes/test", input)) check(response.StatusCode == HttpStatusCode.Conflict, "service: repeated create cannot overwrite");
            using (var response = await http.PutAsJsonAsync("notes/bad", input with { SourceUrl = "javascript:alert(1)" })) check(response.StatusCode == HttpStatusCode.BadRequest, "service: unsafe link rejected");
            using (var response = await http.PutAsJsonAsync("notes/bad", input with { Summary = null! })) check(response.StatusCode == HttpStatusCode.BadRequest, "service: null required field rejected");
            using (var response = await http.PutAsync("notes/bad", new StringContent("{", System.Text.Encoding.UTF8, "application/json"))) check(response.StatusCode == HttpStatusCode.BadRequest, "service: malformed JSON rejected");
            note = await client.PutAsync("test", input with { Summary = "继续后的结论", ExpectedRevision = 1 });
            check(note.Revision == 2 && (await client.ListAsync()).Length == 1, "service: update retains identity and increments revision");
            using (var response = await http.DeleteAsync("notes/test?revision=1")) check(response.StatusCode == HttpStatusCode.Conflict, "service: stale delete cannot discard newer work");
            var oauthTokens = await NotesMcpChecks.RunAsync(address, token, token + "oauth", check);
            process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); process = null;
            http.DefaultRequestHeaders.Authorization = null;
            process = Start(); await WaitReady(process, http);
            await NotesMcpChecks.VerifyRefreshAsync(address, token + "oauth", oauthTokens, check);
            check((await client.ListAsync()).Single() == note, "service: full content survives service restart");
            await client.DeleteAsync(note);
            check((await client.ListAsync()).Length == 0, "service: delete reflected in desktop list");
        }
        finally
        {
            if (process is not null) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
            Directory.Delete(folder, true);
        }
    }
}
