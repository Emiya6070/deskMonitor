using DeskMonitor.Core;

internal static class DesktopNoteChecks
{
    public static void Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "deskmonitor-notes-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "notes.json");
        try
        {
            var store = new NoteStore(path);
            var input = new NoteInput("测试便签", "只显示重点", "完整分析：与重点字段独立。", "https://chatgpt.com/c/example", 0);
            var first = store.Put("analysis-1", input);
            check(first.Revision == 1 && first.Summary != first.Content, "notes: summary and full text remain independent");
            check(new NoteStore(path).Get(first.Id) == first, "notes: saved content survives restart");
            Reject<NoteConflictException>(() => store.Put(first.Id, input), "notes: duplicate create is rejected without overwriting", check);
            var next = store.Put(first.Id, input with { Summary = "继续分析后的重点", ExpectedRevision = 1 });
            check(next.Revision == 2 && store.List().Length == 1, "notes: continued analysis updates the same note");
            Reject<NoteConflictException>(() => store.Delete(first.Id, 1), "notes: stale delete is rejected", check);
            Reject<InvalidDataException>(() => store.Put("bad", input with { SourceUrl = "file:///C:/Windows/test" }), "notes: non-HTTP links rejected", check);
            Reject<InvalidDataException>(() => store.Put("bad", input with { Summary = "" }), "notes: summary required instead of deriving from full text", check);
            Reject<InvalidDataException>(() => store.Put("../escape", input), "notes: invalid identity rejected", check);
            Reject<InvalidDataException>(() => NoteProtocol.ValidateList([next, next]), "notes: duplicate remote IDs rejected", check);
            Reject<InvalidDataException>(() => new NoteServiceClient("http://example.com", new string('a', 32)), "notes: remote plaintext credentials rejected", check);
            check(store.Delete(first.Id, 2) && new NoteStore(path).List().Length == 0, "notes: delete persists");
            File.WriteAllText(path, "null");
            Reject<InvalidDataException>(() => new NoteStore(path), "notes: corrupt storage fails explicitly", check);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static void Reject<T>(Action action, string label, Action<bool, string> check) where T : Exception
    {
        try { action(); } catch (T) { check(true, label); return; }
        throw new Exception("FAIL: " + label);
    }
}
