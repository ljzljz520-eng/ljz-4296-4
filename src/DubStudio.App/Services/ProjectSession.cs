using DubStudio.Core.Data;
using DubStudio.Core.Services;

namespace DubStudio.App.Services;

/// <summary>当前打开的工程会话；跨页面共享一个 SQLite 连接。</summary>
public sealed class ProjectSession
{
    public StudioDatabase? Db { get; private set; }
    public string? DbPath { get; private set; }
    public string ProjectName => Db?.RequireProject().Name ?? "（未打开工程）";
    public bool IsOpen => Db != null;

    public event Action? Changed;

    public StudioDatabase Require() =>
        Db ?? throw new InvalidOperationException("请先新建或打开工程");

    public void Open(string path)
    {
        Close();
        Db = ProjectService.OpenProject(path);
        DbPath = path;
        Changed?.Invoke();
    }

    public void Create(string path, string name, double fpsNum, double fpsDen, bool vfr)
    {
        Close();
        Db = ProjectService.CreateProject(path, name, null, fpsNum, fpsDen, vfr);
        DbPath = path;
        Changed?.Invoke();
    }

    public void Close()
    {
        Db?.Dispose();
        Db = null;
        DbPath = null;
        Changed?.Invoke();
    }

    public ScriptImportService Import() => new(Require());
    public LineRevisionService Lines() => new(Require());
    public RecordingService Recording() => new(Require());
    public DirectorService Director() => new(Require());
    public AudioScanService Scan() => new(Require());
    public ArchiveService Archive() => new(Require());
}
