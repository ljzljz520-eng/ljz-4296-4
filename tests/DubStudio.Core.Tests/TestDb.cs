using DubStudio.Core.Data;
using DubStudio.Core.Services;

namespace DubStudio.Core.Tests;

/// <summary>每个测试得到独立临时文件数据库，测试后删除。</summary>
public sealed class TestDb : IDisposable
{
    public string Path { get; }
    public StudioDatabase Db { get; }
    public int ProjectId => 1;

    static TestDb()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public TestDb(string name = "test")
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dubstudio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, name + ".dsproj");
        Db = ProjectService.CreateProject(Path, "测试工程");
    }

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true); } catch { /* 忽略 */ }
    }
}
