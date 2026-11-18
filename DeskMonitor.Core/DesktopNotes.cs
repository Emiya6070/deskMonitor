using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskMonitor.Core;

public sealed record NoteInput(string Title, string Summary, string Content, string? SourceUrl, int ExpectedRevision)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 120 || Title.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(Summary) || Summary.Length > 2000
            || string.IsNullOrWhiteSpace(Content) || Content.Length > 100000 || ExpectedRevision < 0)
            throw new InvalidDataException("标题须为 1–120 字，重点 1–2000 字，全文 1–100000 字，版本不能为负数。");
        if (!string.IsNullOrEmpty(SourceUrl)) NoteProtocol.ValidateUrl(SourceUrl);
    }
}

public sealed record DesktopNote(string Id, string Title, string Summary, string Content, string? SourceUrl, int Revision, DateTimeOffset UpdatedAt)
{
    public void Validate()
    {
        NoteProtocol.ValidateId(Id);
        new NoteInput(Title, Summary, Content, SourceUrl, Revision).Validate();
        if (Revision < 1 || UpdatedAt == default) throw new InvalidDataException("便签版本或更新时间缺失。");
    }
}

public static class NoteProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void ValidateId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,80}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("便签 ID 只能包含 1–80 个英文字母、数字、下划线或连字符。");
    }
    public static Uri ValidateUrl(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("链接必须为不含用户名和密码的 HTTP/HTTPS 地址。");
        return uri;
    }
    public static void ValidateList(DesktopNote[]? notes)
    {
        if (notes is null || notes.Length > 100 || notes.Any(n => n is null)
            || notes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != notes.Length)
            throw new InvalidDataException("便签列表无效、ID 重复或超过 100 条。");
        foreach (var note in notes) note.Validate();
    }
}

public sealed class NoteConflictException() : Exception("便签已被其他操作更新，请刷新后再保存。");

// One store instance owns one file. Commit to disk before exposing a new snapshot.
public sealed class NoteStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private DesktopNote[] _notes;
    public NoteStore(string path)
    {
        _path = Path.GetFullPath(path);
        _notes = File.Exists(_path) ? JsonSerializer.Deserialize<DesktopNote[]>(File.ReadAllText(_path), NoteProtocol.Json)
            ?? throw new InvalidDataException("便签文件为空。") : [];
        NoteProtocol.ValidateList(_notes);
    }
    public DesktopNote[] List() { lock (_gate) return _notes.OrderByDescending(n => n.UpdatedAt).ToArray(); }
    public DesktopNote? Get(string id) { lock (_gate) return _notes.SingleOrDefault(n => n.Id == id); }
    public DesktopNote Put(string id, NoteInput input)
    {
        NoteProtocol.ValidateId(id); input.Validate();
        lock (_gate)
        {
            var old = _notes.SingleOrDefault(n => n.Id == id);
            if ((old?.Revision ?? 0) != input.ExpectedRevision) throw new NoteConflictException();
            if (old is null && _notes.Length >= 100) throw new InvalidDataException("最多保存 100 张便签，请先删除不需要的便签。");
            var note = new DesktopNote(id, input.Title.Trim(), input.Summary.Trim(), input.Content,
                string.IsNullOrEmpty(input.SourceUrl) ? null : input.SourceUrl, checked(input.ExpectedRevision + 1), DateTimeOffset.UtcNow);
            Commit(_notes.Where(n => n.Id != id).Append(note).ToArray());
            return note;
        }
    }
    public bool Delete(string id, int revision)
    {
        lock (_gate)
        {
            var old = _notes.SingleOrDefault(n => n.Id == id);
            if (old is null) return false;
            if (old.Revision != revision) throw new NoteConflictException();
            Commit(_notes.Where(n => n.Id != id).ToArray()); return true;
        }
    }
    private void Commit(DesktopNote[] notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(notes, NoteProtocol.Json));
        File.Move(_path + ".tmp", _path, true);
        _notes = notes;
    }
}

public sealed class NoteServiceClient : IDisposable
{
    private readonly HttpClient _http;
    public NoteServiceClient(string address, string token)
    {
        var uri = NoteProtocol.ValidateUrl(address);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidDataException("服务地址不能包含查询参数或锚点。");
        if (uri.Scheme != "https" && !uri.IsLoopback) throw new InvalidDataException("远程便签服务必须使用 HTTPS；本机调试可用 HTTP。");
        if (string.IsNullOrWhiteSpace(token) || !Regex.IsMatch(token, "^[A-Za-z0-9_-]{32,512}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("访问令牌须为 32–512 位英文字母、数字、下划线或连字符。");
        _http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(address.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 64 * 1024 * 1024 };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public async Task<DesktopNote[]> ListAsync(CancellationToken cancellation = default)
    {
        using var response = await _http.GetAsync("notes", cancellation);
        await EnsureSuccess(response, cancellation);
        var notes = await response.Content.ReadFromJsonAsync<DesktopNote[]>(NoteProtocol.Json, cancellation);
        NoteProtocol.ValidateList(notes); return notes!;
    }
    public async Task<DesktopNote> PutAsync(string id, NoteInput input, CancellationToken cancellation = default)
    {
        NoteProtocol.ValidateId(id); input.Validate();
        using var response = await _http.PutAsJsonAsync("notes/" + id, input, NoteProtocol.Json, cancellation);
        await EnsureSuccess(response, cancellation);
        var note = await response.Content.ReadFromJsonAsync<DesktopNote>(NoteProtocol.Json, cancellation)
            ?? throw new InvalidDataException("服务未返回保存结果。");
        note.Validate();
        if (note.Id != id || note.Revision != input.ExpectedRevision + 1) throw new InvalidDataException("服务返回的便签 ID 或版本与保存请求不符。");
        return note;
    }
    public async Task DeleteAsync(DesktopNote note, CancellationToken cancellation = default)
    {
        using var response = await _http.DeleteAsync($"notes/{note.Id}?revision={note.Revision}", cancellation);
        await EnsureSuccess(response, cancellation);
    }
    private static Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (response.StatusCode == HttpStatusCode.Conflict) throw new NoteConflictException();
        response.EnsureSuccessStatusCode(); return Task.CompletedTask;
    }
    public void Dispose() => _http.Dispose();
}
