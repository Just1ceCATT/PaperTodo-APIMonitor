using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.ApiBalanceMonitor.LoadTests;

/// <summary>
/// PaperBodyContext 的最小化 mock：只实现插件启动路径真正触碰到的成员。
///
/// 从 HookConcurrencyTests 抽出为独立文件，供多个测试类共用。
/// </summary>
internal static class FakePaperBodyContext
{
    public static PaperBodyContext Create() => new()
    {
        ProviderId = "ApiBalanceMonitor",
        ApiVersion = "1.0",
        StateJson = "{}",
        StateVersion = 1,
        TargetStateVersion = 1,
        SettingsJson = """{"provider":"deepseek","pollSeconds":60,"hooksPort":0}""",
        SaveStateJson = _ => { },
        Paper = new PaperBodyPaperContext
        {
            PaperId = "test",
            SetTitle = _ => { },
            SetHeaderText = _ => { },
            SetCapsulePresentation = _ => { }
        },
        Body = new PaperBodySurfaceContext
        {
            Theme = new PaperBodyTheme(
                IsDark: false,
                PaperColor: "#FFFFFF",
                TextColor: "#000000",
                WeakTextColor: "#888888",
                AccentColor: "#0078D4",
                BorderColor: "#CCCCCC",
                FontFamily: "Segoe UI",
                FontScale: 1.0),
            Controls = new FakeControls(),
            SetInputClaims = _ => { },
            MarkDirty = () => { },
            OpenExternal = _ => { },
            RequestReload = () => { }
        },
        Workspace = new FakeHostApi(),
        // 宿主 PaperBodyContext 新增了 required Runtime 成员（Protocol 2.1），
        // mock 不实现它编译不过；插件当前不用 runtime 命令通道，固定返回"不可用"。
        Runtime = new FakeRuntimeClient()
    };
}

internal sealed class FakeControls : IPaperBodyControls
{
    public void ApplySelectStyle(System.Windows.Controls.ComboBox comboBox, double fontSize) { }
}

internal sealed class FakeHostApi : IPaperTodoHostApi
{
    public IReadOnlySet<string> GrantedPermissions => new HashSet<string>();
    public System.Collections.Generic.IReadOnlyList<PaperSnapshot> ListPapers(string? type = null) => [];
    public PaperSnapshot? GetPaper(string paperId) => null;
    public System.Collections.Generic.IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false) => [];
    public NoteSnapshot? GetNote(string paperId) => null;
    public PaperMutationResult CreatePaper(CreatePaperRequest request) => default!;
    public AppendTodosResult AppendTodos(AppendTodosRequest request) => default!;
    public TodoMutationResult UpdateTodo(UpdateTodoRequest request) => default!;
    public TodoMutationResult SetTodoReminder(SetTodoReminderRequest request) => default!;
    public NoteMutationResult WriteNote(WriteNoteRequest request) => default!;
    public DeleteMutationResult DeleteTodo(DeleteTodoRequest request) => default!;
    public DeleteMutationResult DeletePaper(string paperId) => default!;
    public IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler) => new NoopDisposable();
}

/// <summary>最小 Runtime mock：插件不依赖 runtime 命令通道，固定"不可用"。</summary>
internal sealed class FakeRuntimeClient : IPaperPluginRuntimeClient
{
    public bool IsAvailable => false;
    public bool Post(JsonElement message) => false;
}

internal sealed class NoopDisposable : IDisposable
{
    public void Dispose() { }
}
