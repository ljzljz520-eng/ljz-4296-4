using DubStudio.Core.Models;
using DubStudio.Core.Services;
using Xunit;

namespace DubStudio.Core.Tests;

public class DirectorTests
{
    private static async Task<(TestDb t, ScriptLine line, string audio)> SeedLineWithTakeAsync()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
"""));
        var line = t.Db.Conn.Table<ScriptLine>().First();
        var audio = ImportNumberingTests.MakeAudio(t, "take.wav");
        var take = (await new RecordingService(t.Db).AddTakeAsync(
            new NewTakeRequest(line.Id, "演员", "MIC", 0, 100, audio))).Take;
        return (t, line, audio);
    }

    [Fact]
    public async Task Pick_OnlyFromRealTakes_RejectsPhantomTakeId()
    {
        var (t, line, _) = await SeedLineWithTakeAsync();
        var director = new DirectorService(t.Db);
        Assert.Throws<ArgumentException>(() => director.PickTake(line.Id, 99999));
        Assert.False(t.Db.Conn.Table<DirectorPick>().Any());
    }

    [Fact]
    public async Task Pick_RequiresExistingAudioFile()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
"""));
        var line = t.Db.Conn.Table<ScriptLine>().First();
        var takeNoFile = (await new RecordingService(t.Db).AddTakeAsync(
            new NewTakeRequest(line.Id, "演员", "MIC", 0, 100, null))).Take;

        var ex = Assert.Throws<InvalidOperationException>(
            () => new DirectorService(t.Db).PickTake(line.Id, takeNoFile.Id));
        Assert.Contains("音频", ex.Message);
    }

    [Fact]
    public async Task Pick_TakeFromAnotherLine_Rejected()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 第一句。
乙: 第二句。
"""));
        var lines = t.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).ToList();
        var rec = new RecordingService(t.Db);
        var audio = ImportNumberingTests.MakeAudio(t, "a.wav");
        var takeOfSecond = (await rec.AddTakeAsync(
            new NewTakeRequest(lines[1].Id, "演员", "MIC", 0, 100, audio))).Take;

        Assert.Throws<InvalidOperationException>(() => new DirectorService(t.Db).PickTake(lines[0].Id, takeOfSecond.Id));
    }

    [Fact]
    public async Task Pick_IsOnePerLine_LatestWins()
    {
        var (t, line, audio) = await SeedLineWithTakeAsync();
        var rec = new RecordingService(t.Db);
        var take1 = t.Db.Conn.Table<Take>().First();
        var audio2 = ImportNumberingTests.MakeAudio(t, "b.wav", new byte[] { 9, 9, 9 });
        var take2 = (await rec.AddTakeAsync(
            new NewTakeRequest(line.Id, "演员", "MIC", 0, 100, audio2))).Take;

        var director = new DirectorService(t.Db);
        director.PickTake(line.Id, take1.Id);
        director.PickTake(line.Id, take2.Id);

        Assert.Single(t.Db.Conn.Table<DirectorPick>().ToList());
        Assert.Equal(take2.Id, t.Db.Conn.Table<DirectorPick>().First().TakeId);
    }

    [Fact]
    public async Task RejectedTake_CannotBePicked()
    {
        var (t, line, _) = await SeedLineWithTakeAsync();
        var take = t.Db.Conn.Table<Take>().First();
        new RecordingService(t.Db).RejectTake(take.Id, "杂音");
        Assert.Throws<InvalidOperationException>(() => new DirectorService(t.Db).PickTake(line.Id, take.Id));
    }
}
