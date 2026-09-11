using System.IO.Compression;
using DubStudio.Core.Models;
using DubStudio.Core.Services;
using Xunit;

namespace DubStudio.Core.Tests;

public class ArchiveTests
{
    [Fact]
    public async Task Archive_ContainsDatabaseManifestAndAudio_RoundTrips()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
"""));
        var line = t.Db.Conn.Table<ScriptLine>().First();
        var audio = ImportNumberingTests.MakeAudio(t, "take.wav", new byte[] { 1, 2, 3 });
        var take = await new RecordingService(t.Db).AddTakeAsync(
            new NewTakeRequest(line.Id, "演员", "MIC", 0, 90, audio));
        new DirectorService(t.Db).PickTake(line.Id, take.Take.Id);

        var zip = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "deliver.zip");
        var result = await new ArchiveService(t.Db).ArchiveAsync(zip, includeAudio: true);

        Assert.Equal(0, result.MissingOrChangedFiles);
        Assert.Equal(1, result.Manifest.Takes);
        Assert.Equal(1, result.Manifest.ConfirmedPicks);
        using var z = ZipFile.OpenRead(zip);
        Assert.NotNull(z.GetEntry("project.dsproj"));
        Assert.NotNull(z.GetEntry("manifest.json"));
        Assert.Contains(z.Entries, e => e.FullName.StartsWith("audio/"));

        // 归档中的工程数据库可被重新打开
        var extractDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "unzipped");
        Directory.CreateDirectory(extractDir);
        z.ExtractToDirectory(extractDir, true);
        using var reopen = ProjectService.OpenProject(System.IO.Path.Combine(extractDir, "project.dsproj"));
        Assert.Equal("测试工程", reopen.RequireProject().Name);
        Assert.Equal(1, reopen.Conn.Table<ScriptLine>().Count());
    }

    [Fact]
    public async Task Archive_ReportsChangedAndMissingFiles()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
乙: 第二句。
"""));
        var lines = t.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).ToList();
        var a1 = ImportNumberingTests.MakeAudio(t, "a1.wav", new byte[] { 1 });
        var a2 = ImportNumberingTests.MakeAudio(t, "a2.wav", new byte[] { 2 });
        var rec = new RecordingService(t.Db);
        await rec.AddTakeAsync(new NewTakeRequest(lines[0].Id, "演员", "M", 0, 50, a1));
        await rec.AddTakeAsync(new NewTakeRequest(lines[1].Id, "演员", "M", 0, 50, a2));

        File.Delete(a1);
        await File.WriteAllBytesAsync(a2, new byte[] { 7, 8, 9 });

        var zip = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "deliver.zip");
        var result = await new ArchiveService(t.Db).ArchiveAsync(zip);
        Assert.True(result.MissingOrChangedFiles >= 2);
    }

    [Fact]
    public async Task Archive_WithoutAudio_StillContainsDbAndManifest()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
"""));
        var zip = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "db-only.zip");
        await new ArchiveService(t.Db).ArchiveAsync(zip, includeAudio: false);
        using var z = ZipFile.OpenRead(zip);
        Assert.Equal(2, z.Entries.Count);
    }

    [Fact]
    public async Task TakeExceedingMaxDuration_IsFlaggedForReview()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
"""));
        var line = t.Db.Conn.Table<ScriptLine>().First();
        new LineRevisionService(t.Db).UpdateLipSync(line.Id, 0, 80, null, 100);
        var audio = ImportNumberingTests.MakeAudio(t, "long.wav");
        var result = await new RecordingService(t.Db)
            .AddTakeAsync(new NewTakeRequest(line.Id, "演员", "M", 0, 250, audio));
        Assert.True(result.NeedsReview);
        Assert.Equal(TakeStatus.NeedsReview, result.Take.Status);
        Assert.True(t.Db.Conn.Table<ReviewItem>().Any(r => r.LineId == line.Id));
    }

    [Fact]
    public void LipSync_CloseBeforeInPoint_Throws()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
"""));
        var line = t.Db.Conn.Table<ScriptLine>().First();
        Assert.Throws<ArgumentException>(() =>
            new LineRevisionService(t.Db).UpdateLipSync(line.Id, 500, 100, null, null));
    }
}
