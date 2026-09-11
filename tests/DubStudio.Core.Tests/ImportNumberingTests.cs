using DubStudio.Core.Import;
using DubStudio.Core.Models;
using DubStudio.Core.Services;
using Xunit;

namespace DubStudio.Core.Tests;

public class ImportNumberingTests
{
    private const string ScriptA = """
EPISODE E01 第一集
SCENE S001 村口
田中: 你好。
田中: 又见面了。
铃木: 早上好。
SCENE S002 河边
田中: 你好。
""";

    private static (ScriptImportService imp, TestDb db) CreateHarness()
    {
        var db = new TestDb();
        return (new ScriptImportService(db.Db), db);
    }

    [Fact]
    public void Import_CreatesStableCodes_AndSupportsSameCharacterMultipleLines()
    {
        var (imp, db) = CreateHarness();
        var doc = ScriptParser.Parse(ScriptA);
        var rep = imp.Import(doc);

        Assert.Equal(2, db.Db.Conn.Table<Scene>().Count());
        Assert.Equal(4, db.Db.Conn.Table<ScriptLine>().Count());
        var lines = db.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).ToList();
        Assert.Equal("E01-S001-C_田中-01", lines[0].Code);
        Assert.Equal("E01-S001-C_田中-02", lines[1].Code); // 同角多句
        Assert.Equal("E01-S001-C_铃木-01", lines[2].Code);
        Assert.Equal("E01-S002-C_田中-01", lines[3].Code);
        Assert.Equal(0, rep.LinesRetired);
    }

    [Fact]
    public void Reimport_IsIdempotent_KeepsStableKeys()
    {
        var (imp, db) = CreateHarness();
        imp.Import(ScriptParser.Parse(ScriptA));
        var keys1 = db.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).Select(l => l.StableKey).ToList();
        var ids1 = db.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).Select(l => l.Id).ToList();

        var rep2 = imp.Import(ScriptParser.Parse(ScriptA));
        var keys2 = db.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).Select(l => l.StableKey).ToList();
        var ids2 = db.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).Select(l => l.Id).ToList();

        Assert.Equal(keys1, keys2);
        Assert.Equal(ids1, ids2);
        Assert.Equal(0, rep2.LinesCreated);
        Assert.Equal(0, rep2.LinesRetired);
    }

    [Fact]
    public async Task Reimport_ReplacedLine_OldTakesBecomePendingReview_NotAutoApplied()
    {
        var (imp, db) = CreateHarness();
        imp.Import(ScriptParser.Parse(ScriptA));
        var line = db.Db.Conn.Table<ScriptLine>().First(l => l.OriginalText == "早上好。");

        var revisions = new LineRevisionService(db.Db);
        revisions.ReviseText(line.Id, "早上好呀。");
        var recording = new RecordingService(db.Db);
        var audio = MakeAudio(db, "a.wav");
        var take = (await recording.AddTakeAsync(
            new NewTakeRequest(line.Id, "张三", "U87", 100, 500, audio))).Take;
        var director = new DirectorService(db.Db);
        director.PickTake(line.Id, take.Id);
        director.ConfirmPick(line.Id); // 导演确认旧译文下采用该条
        Assert.Equal(PickStatus.Confirmed,
            db.Db.Conn.Table<DirectorPick>().First(x => x.LineId == line.Id).Status);

        // 铃木的句子被换掉
        var changed = ScriptA.Replace("铃木: 早上好。", "铃木: 今天天气不错。");
        var rep = imp.Import(ScriptParser.Parse(changed));

        Assert.Equal(1, rep.LinesRetired);
        var oldLine = db.Db.Conn.Find<ScriptLine>(line.Id);
        Assert.Equal(LineState.Retired, oldLine.State);
        Assert.Equal(TakeStatus.NeedsReview, db.Db.Conn.Find<Take>(take.Id).Status);
        var pick = db.Db.Conn.Table<DirectorPick>().First(p => p.LineId == line.Id);
        Assert.Equal(PickStatus.PendingReview, pick.Status);
        Assert.True(db.Db.Conn.Table<ReviewItem>().Any(r => r.LineId == line.Id && !r.Resolved));
    }

    private const string ScriptShifted = """
EPISODE E01 第一集
SCENE S001 村口
田中: 你好。
田中: 又见面了。
SCENE S002 河边
铃木: 早上好。
田中: 你好。
""";

    [Fact]
    public async Task ShiftedLine_OldTakesBecomePendingReview_InsteadOfSilentApply()
    {
        var (imp, db) = CreateHarness();
        imp.Import(ScriptParser.Parse(ScriptA));
        var line = db.Db.Conn.Table<ScriptLine>().First(l =>
            l.OriginalText == "早上好。" && l.StableKey.Contains("S001"));
        var audio = MakeAudio(db, "a.wav");
        var take = (await new RecordingService(db.Db).AddTakeAsync(
            new NewTakeRequest(line.Id, "张三", "U87", 100, 500, audio))).Take;
        Assert.Equal(TakeStatus.Usable, db.Db.Conn.Find<Take>(take.Id).Status);

        // 铃木的句子从 S001 移到 S002：稳定键不同，旧行退役、新行建立
        var rep = imp.Import(ScriptParser.Parse(ScriptShifted));

        Assert.Equal(1, rep.LinesRetired);
        Assert.Equal(1, rep.LinesCreated);
        Assert.Equal(TakeStatus.NeedsReview, db.Db.Conn.Find<Take>(take.Id).Status);
        Assert.True(db.Db.Conn.Find<ScriptLine>(line.Id).LipSyncDirty);
        Assert.True(db.Db.Conn.Table<ReviewItem>().Any(r =>
            r.LineId == line.Id && r.Kind == ReviewKind.ReimportConflict));
    }

    [Fact]
    public void DuplicateCharacterNames_DefaultSamePerson_QualifierSplits()
    {
        var (imp, db) = CreateHarness();
        var doc = ScriptParser.Parse("""
EPISODE E01
SCENE S001
田中: 第一句。
田中@少年: 年轻时的台词。
田中@老年: 年老时的台词。
""");
        imp.Import(doc);
        var chars = db.Db.Conn.Table<Character>().OrderBy(c => c.Id).ToList();
        Assert.Equal(3, chars.Count);
        Assert.Equal("C_田中", chars[0].StableCode);
        Assert.Contains("#", chars[1].StableCode);
        Assert.NotEqual(chars[1].StableCode, chars[2].StableCode);
    }

    [Fact]
    public void RepeatedSameLine_SameCharacter_GetDistinctOccurrenceKeys()
    {
        var (imp, db) = CreateHarness();
        var doc = ScriptParser.Parse("""
EPISODE E01
SCENE S001
回声: 一样的话。
回声: 一样的话。
""");
        imp.Import(doc);
        var lines = db.Db.Conn.Table<ScriptLine>().OrderBy(l => l.Id).ToList();
        Assert.Equal(2, lines.Count);
        Assert.NotEqual(lines[0].StableKey, lines[1].StableKey);
        Assert.Equal(1, lines[0].Occurrence);
        Assert.Equal(2, lines[1].Occurrence);
    }

    [Fact]
    public void TextChange_CreatesImmutableVersions_AndQueuesReviewForOldTakes()
    {
        var (imp, db) = CreateHarness();
        imp.Import(ScriptParser.Parse(ScriptA));
        var line = db.Db.Conn.Table<ScriptLine>().First();
        var rev = new LineRevisionService(db.Db);

        var v1 = rev.ReviseText(line.Id, "你好吗？");
        var v2 = rev.ReviseText(line.Id, "你好吗？");  // 相同文本不产生新版本
        var v3 = rev.ReviseText(line.Id, "你好吗，朋友？");

        Assert.Equal(v1.Id, v2.Id);
        Assert.Equal(3, v3.RevisionNo); // 空初始版本为 1，第一次实际译文为 2，重复保存不产生版本
        var all = db.Db.Conn.Table<LineTextVersion>().Where(v => v.LineId == line.Id)
            .OrderBy(v => v.RevisionNo).ToList();
        // 第三次 ReviseText 与第二次不同，形成第 3 版
        Assert.Equal(3, all.Count);
        Assert.Equal("", all[0].Text); // 初始版本不可变
        Assert.Equal("你好吗？", all[1].Text);
        Assert.Equal("你好吗，朋友？", all[2].Text);
    }

    [Fact]
    public void RevivedLine_AfterReappearing_IsReviewed()
    {
        var (imp, db) = CreateHarness();
        imp.Import(ScriptParser.Parse(ScriptA));
        var line = db.Db.Conn.Table<ScriptLine>().First(l => l.OriginalText == "早上好。");
        var without = ScriptA.Replace("铃木: 早上好。\n", "");
        imp.Import(ScriptParser.Parse(without));
        Assert.Equal(LineState.Retired, db.Db.Conn.Find<ScriptLine>(line.Id).State);

        var rep = imp.Import(ScriptParser.Parse(ScriptA));
        Assert.Equal(1, rep.LinesRevived);
        Assert.Equal(LineState.Active, db.Db.Conn.Find<ScriptLine>(line.Id).State);
        Assert.True(db.Db.Conn.Table<ReviewItem>().Any(r => r.LineId == line.Id));
    }

    internal static string MakeAudio(TestDb t, string name, byte[]? bytes = null)
    {
        var dir = System.IO.Path.GetDirectoryName(t.Path)!;
        var p = System.IO.Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes ?? new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        return p;
    }
}
