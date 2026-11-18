using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskMonitor.Core;
using DeskMonitor.NotesService;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var token = builder.Configuration["NOTES_API_TOKEN"];
if (string.IsNullOrWhiteSpace(token) || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Za-z0-9_-]{32,512}$"))
    throw new InvalidOperationException("NOTES_API_TOKEN 必须为 32–512 位英文字母、数字、下划线或连字符的随机令牌。");
var dataFile = builder.Configuration["NOTES_DATA_FILE"];
if (string.IsNullOrWhiteSpace(dataFile)) throw new InvalidOperationException("请通过 NOTES_DATA_FILE 指定持久化文件的绝对路径。");
if (!Path.IsPathFullyQualified(dataFile)) throw new InvalidOperationException("NOTES_DATA_FILE 必须为绝对路径。");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 768 * 1024);
var store = new NoteStore(dataFile);
var publicUrl = builder.Configuration["NOTES_PUBLIC_URL"] ?? throw new InvalidOperationException("请设置 NOTES_PUBLIC_URL。");
var oauth = new NotesOAuth(publicUrl, builder.Configuration["NOTES_OAUTH_CLIENT_SECRET"] ?? "", token, Path.GetDirectoryName(dataFile)!);
builder.Services.AddSingleton(store);
builder.Services.AddMcpServer(options => options.ServerInstructions = NoteMcpTools.Instructions)
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<NoteMcpTools>();
builder.Services.AddDataProtection().SetApplicationName("DeskMonitor.NotesService")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(dataFile)!, "oauth-keys")));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "DeskMonitor.Notes.Csrf";
    options.Cookie.SecurePolicy = publicUrl.StartsWith("https:", StringComparison.Ordinal) ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedProto);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("notes-oauth", limiter => { limiter.PermitLimit = 30; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; });
});
var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var isMcp = context.Request.Path.StartsWithSegments("/mcp");
    if (isMcp && context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin != new Uri(publicUrl).GetLeftPart(UriPartial.Authority))
    { context.Response.StatusCode = 403; return; }
    var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
    var authorized = NotesOAuth.IsPublicPath(context.Request.Path) || (isMcp
        ? oauth.AcceptsAccess(context.Request.Headers.Authorization.ToString())
        : CryptographicOperations.FixedTimeEquals(expectedHash, actualHash));
    if (!authorized)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = isMcp ? $"Bearer resource_metadata=\"{oauth.Metadata}\", scope=\"notes\"" : "Bearer";
        return;
    }
    try { await next(context); }
    catch (NoteConflictException ex) { await Error(context, 409, ex.Message); }
    catch (InvalidDataException ex) { await Error(context, 400, ex.Message); }
    catch (JsonException) { await Error(context, 400, "JSON 格式无效。"); }
    catch (BadHttpRequestException ex) { await Error(context, ex.StatusCode, "请求格式或大小无效。"); }
});
app.UseRateLimiter();
oauth.Map(app);
app.MapMcp("/mcp");
app.MapGet("/notes", () => store.List());
app.MapGet("/notes/{id}", (string id) =>
{
    NoteProtocol.ValidateId(id);
    return store.Get(id) is { } note ? Results.Ok(note) : Results.NotFound();
});
app.MapPut("/notes/{id}", (string id, NoteInput input) => Results.Ok(store.Put(id, input)));
app.MapDelete("/notes/{id}", (string id, int revision) =>
{
    NoteProtocol.ValidateId(id);
    return store.Delete(id, revision) ? Results.NoContent() : Results.NotFound();
});
app.Run();

static Task Error(HttpContext context, int status, string error)
{
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new { error });
}
