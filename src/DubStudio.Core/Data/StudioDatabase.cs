using DubStudio.Core.Models;
using SQLite;

namespace DubStudio.Core.Data;

/// <summary>工程 SQLite 数据库封装。一个工程文件即一个数据库。</summary>
public sealed class StudioDatabase : IDisposable
{
    public SQLiteConnection Conn { get; }
    public string FilePath { get; }

    private StudioDatabase(SQLiteConnection conn, string path)
    {
        Conn = conn;
        FilePath = path;
    }

    public static StudioDatabase Create(string path)
    {
        var conn = new SQLiteConnection(path);
        var db = new StudioDatabase(conn, path);
        db.Initialize();
        return db;
    }

    public static StudioDatabase Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("工程文件不存在", path);
        var conn = new SQLiteConnection(path);
        var db = new StudioDatabase(conn, path);
        db.Initialize();
        return db;
    }

    public static StudioDatabase OpenReadOnly(string path)
    {
        var conn = new SQLiteConnection(new SQLiteConnectionString(path, SQLiteOpenFlags.ReadOnly, true));
        return new StudioDatabase(conn, path);
    }

    private void Initialize()
    {
        Conn.BeginTransaction();
        try
        {
            Conn.CreateTable<Project>();
            Conn.CreateTable<Episode>();
            Conn.CreateTable<Scene>();
            Conn.CreateTable<Character>();
            Conn.CreateTable<ScriptLine>();
            Conn.CreateTable<LineTextVersion>();
            Conn.CreateTable<Take>();
            Conn.CreateTable<DirectorPick>();
            Conn.CreateTable<ReviewItem>();
            Conn.CreateTable<MediaSource>();
            Conn.CreateTable<RevisionPackage>();
            Conn.CreateTable<RevisionChange>();
            Conn.CreateTable<LineGenealogy>();
            Conn.CreateTable<CharacterCast>();
            Conn.CreateTable<MixDelivery>();
            // 业务唯一约束
            ExecUnique("Episode", "ProjectId,StableKey");
            ExecUnique("Scene", "ProjectId,StableKey");
            ExecUnique("Character", "ProjectId,StableCode");
            ExecUnique("ScriptLine", "StableKey");
            Conn.Commit();
        }
        catch
        {
            Conn.Rollback();
            throw;
        }
    }

    private void ExecUnique(string table, string columns)
    {
        var idx = $"ux_{table}_{columns.Replace(",", "_").Replace(" ", "")}";
        Conn.Execute($"CREATE UNIQUE INDEX IF NOT EXISTS {idx} ON {table}({columns})");
    }

    public void InTransaction(Action action)
    {
        Conn.BeginTransaction();
        try { action(); Conn.Commit(); }
        catch { Conn.Rollback(); throw; }
    }

    public Project RequireProject(int projectId = 1)
    {
        return Conn.Find<Project>(projectId) ?? throw new InvalidOperationException("工程未初始化");
    }

    public void Dispose() => Conn.Close();
}
