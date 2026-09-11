using DubStudio.Core.Data;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

/// <summary>工程的创建/打开与媒体信息登记。</summary>
public static class ProjectService
{
    public static StudioDatabase CreateProject(string dbPath, string name, string? sourceMediaPath = null,
        double fpsNum = 24000, double fpsDen = 1001, bool vfr = false)
    {
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var db = StudioDatabase.Create(dbPath);
        var now = ReviewWorkflow.Now();
        var project = new Project
        {
            Id = 1,
            Name = name,
            SourceMediaPath = sourceMediaPath,
            FrameRateNum = fpsNum,
            FrameRateDen = fpsDen,
            VariableFrameRate = vfr,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Conn.Insert(project);
        return db;
    }

    public static StudioDatabase OpenProject(string dbPath) => StudioDatabase.Open(dbPath);

    public static void RegisterMedia(StudioDatabase db, MediaSource source)
    {
        source.ProbedAt = ReviewWorkflow.Now();
        var existing = db.Conn.Table<MediaSource>().FirstOrDefault(m => m.ProjectId == source.ProjectId && m.Path == source.Path);
        if (existing == null) db.Conn.Insert(source);
        else
        {
            source.Id = existing.Id;
            db.Conn.Update(source);
        }
        var p = db.RequireProject(source.ProjectId);
        p.SourceMediaPath = source.Path;
        p.FrameRateNum = source.FrameRateNum;
        p.FrameRateDen = source.FrameRateDen;
        p.VariableFrameRate = source.VariableFrameRate;
        p.UpdatedAt = ReviewWorkflow.Now();
        db.Conn.Update(p);
    }
}
