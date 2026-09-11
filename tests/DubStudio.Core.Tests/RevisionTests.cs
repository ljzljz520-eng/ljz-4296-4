using System.Text.Json;
using DubStudio.Core.Import;
using DubStudio.Core.Models;
using DubStudio.Core.Services;
using Xunit;

namespace DubStudio.Core.Tests;

/// <summary>混录后剧本修订：稳定身份影响分析、导演逐条/批量处置、拆分合并、
/// 时间轴平移、撤回、修订包非破坏式交付与换演员隔离、两包交叉测试。</summary>
public class RevisionTests
{
    private const string BaseScript = """
EPISODE E01 第一集
SCENE S001 村口
甲: 你好啊朋友
甲: 今天天气不错
乙: 早上好呀
乙: 回头见
""";

    private static TestDb SeedProject()
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(ScriptParser.Parse(BaseScript));
        return t;
    }

    private static List<ScriptLine> Lines(TestDb t) =>
        t.Db.Conn.Table<ScriptLine>().OrderBy(l => l.SequenceInScene).ToList();

    private static void SetTimingAndText(TestDb t, ScriptLine line, string translated,
        double inP, double close, double max)
    {
        var svc = new LineRevisionService(t.Db);
        svc.ReviseText(line.Id, translated);
        svc.UpdateLipSync(line.Id, inP, close, null, max);
    }

    private static async Task<Take> RecordAndPickAsync(TestDb t, ScriptLine line, string actor, string fileName, byte[]? bytes = null)
    {
        var audio = ImportNumberingTests.MakeAudio(t, fileName, bytes);
        var take = (await new RecordingService(t.Db).AddTakeAsync(
            new NewTakeRequest(line.Id, actor, "MIC", line.InPointMs ?? 0,
                (line.InPointMs ?? 0) + 100, audio))).Take;
        new DirectorService(t.Db).PickTake(line.Id, take.Id);
        new DirectorService(t.Db).ConfirmPick(line.Id);
        return take;
    }

    private const string DraftJson = """
{
  "episodeCode": "E01",
  "reason": "客户改稿",
  "submittedBy": "译配编辑",
  "scenes": [
    { "code": "S001", "lines": [
      { "character": "甲", "text": "你好啊朋友", "translatedText": "你好呀我的老朋友" },
      { "character": "甲", "text": "今天天气不错", "inPointMs": 1200, "mouthCloseMs": 2600, "maxDurationMs": 1500 },
      { "character": "乙", "text": "早上好呀" },
      { "character": "乙", "text": "改个说法行不行啊", "translatedText": "换个说法可以吗" }
    ]}
  ]
}
""";

    [Fact]
    public void Analyze_Classifies_Tweak_Duration_Deleted_Added_ByStableIdentity()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "今天天气不错", 1000, 2400, 1200);
        SetTimingAndText(t, lines[2], "早上好", 200, 800, 700);
        SetTimingAndText(t, lines[3], "回头见", 300, 700, 500);

        var svc = new RevisionPackageService(t.Db);
        var report = svc.Analyze(RevisionDraftParser.Parse(DraftJson));

        var changes = report.Changes;
        Assert.Equal(RevisionState.Draft, t.Db.Conn.Find<RevisionPackage>(report.PackageId)!.State);
        // 甲句1 文字微调
        var tweak = changes.Single(c => c.LineId == lines[0].Id);
        Assert.Equal(LineChangeKind.TextTweak, tweak.Kind);
        Assert.Equal(LineDisposition.NeedsRerecord, tweak.SuggestedDisposition);
        Assert.Contains("补录", tweak.SuggestionBasis);
        // 甲句2 时长变化
        var dur = changes.Single(c => c.LineId == lines[1].Id);
        Assert.Equal(LineChangeKind.DurationChanged, dur.Kind);
        Assert.Equal(LineDisposition.ReeditOnly, dur.SuggestedDisposition);
        // 乙句“回头见”消失 → 删句
        var deleted = changes.Single(c => c.LineId == lines[3].Id);
        Assert.Equal(LineChangeKind.Deleted, deleted.Kind);
        Assert.Equal(LineDisposition.Retire, deleted.SuggestedDisposition);
        // 新增一句
        var added = changes.Single(c => c.Kind == LineChangeKind.Added);
        Assert.Equal(LineDisposition.NewRecording, added.SuggestedDisposition);
        Assert.Equal("改个说法行不行啊", added.NewOriginalText);
        // 乙句“早上好呀”原样出现，无变更条目
        Assert.DoesNotContain(changes, c => c.LineId == lines[2].Id);

        Assert.Equal(1, report.TextTweaks);
        Assert.Equal(1, report.DurationChanges);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(1, report.Added);
        // 分析阶段不得改动在制数据
        Assert.Equal("你好朋友", t.Db.Conn.Find<ScriptLine>(lines[0].Id)!.TranslatedText);
        Assert.Equal(1000, t.Db.Conn.Find<ScriptLine>(lines[1].Id)!.InPointMs);
        Assert.Equal(LineState.Active, t.Db.Conn.Find<ScriptLine>(lines[3].Id)!.State);
    }

    [Fact]
    public void FuzzyOriginalRewrite_IsMatchedAsTweak_NotDeletePlusAdd()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        var draft = """
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊我的老朋友","translatedText":"你好呀"},
  {"character":"甲","text":"今天天气不错"},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
""";
        var report = new RevisionPackageService(t.Db).Analyze(RevisionDraftParser.Parse(draft));
        // 原文由“你好啊朋友”改写为“你好啊我的老朋友”，高相似度 → 文字微调，而非删+增
        var matched = report.Changes.SingleOrDefault(c => c.LineId == lines[0].Id);
        Assert.NotNull(matched);
        Assert.Equal(LineChangeKind.TextTweak, matched!.Kind);
        Assert.DoesNotContain(report.Changes, c => c.Kind == LineChangeKind.Added);
        Assert.DoesNotContain(report.Changes, c => c.Kind == LineChangeKind.Deleted);
    }

    [Fact]
    public async Task Analyze_DoesNotMutateTakesOrPicks_BeforeDirectorConfirm()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        var take = await RecordAndPickAsync(t, lines[0], "张三", "a.wav");

        var svc = new RevisionPackageService(t.Db);
        svc.Analyze(RevisionDraftParser.Parse(DraftJson));

        Assert.Equal(TakeStatus.Usable, t.Db.Conn.Find<Take>(take.Id)!.Status);
        Assert.Equal(PickStatus.Confirmed, t.Db.Conn.Table<DirectorPick>().ToList().First(p => p.LineId == lines[0].Id).Status);
    }

    [Fact]
    public async Task Confirm_TextTweak_Rerecords_PendingTakesAndPick()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "今天天气不错", 1000, 2400, 1200);
        SetTimingAndText(t, lines[2], "早上好", 200, 800, 700);
        SetTimingAndText(t, lines[3], "回头见", 300, 700, 500);
        var take1 = await RecordAndPickAsync(t, lines[0], "张三", "a.wav");
        var take2 = await RecordAndPickAsync(t, lines[1], "张三", "b.wav");
        var take3 = await RecordAndPickAsync(t, lines[3], "李四", "c.wav");

        var svc = new RevisionPackageService(t.Db);
        var report = svc.Analyze(RevisionDraftParser.Parse(DraftJson));
        svc.ApplySuggestedDispositions(report.PackageId, "客户改稿，按建议执行");
        svc.ConfirmPackage(report.PackageId);

        // 文字微调 → 新译文版本落库，旧条次需补录
        var line0 = t.Db.Conn.Find<ScriptLine>(lines[0].Id)!;
        Assert.Equal("你好呀我的老朋友", line0.TranslatedText);
        Assert.Equal(TakeStatus.NeedsReview, t.Db.Conn.Find<Take>(take1.Id)!.Status);
        Assert.Equal(PickStatus.PendingReview,
            t.Db.Conn.Table<DirectorPick>().ToList().First(p => p.LineId == lines[0].Id).Status);
        // 时长变化 → 仅重剪：条次仍可用，有时序复核项
        Assert.Equal(TakeStatus.Usable, t.Db.Conn.Find<Take>(take2.Id)!.Status);
        Assert.Equal(2600, t.Db.Conn.Find<ScriptLine>(lines[1].Id)!.MouthCloseMs);
        // 删句 → 退役
        Assert.Equal(LineState.Retired, t.Db.Conn.Find<ScriptLine>(lines[3].Id)!.State);
        Assert.Equal(TakeStatus.NeedsReview, t.Db.Conn.Find<Take>(take3.Id)!.Status);
        // 新增句已创建且无历史录音
        var added = t.Db.Conn.Table<RevisionChange>()
            .ToList().Single(c => c.PackageId == report.PackageId && c.Kind == LineChangeKind.Added);
        var addedLineId = added.TargetLineId!.Value;
        var newLine = t.Db.Conn.Find<ScriptLine>(addedLineId)!;
        Assert.Equal(LineState.Active, newLine.State);
        Assert.DoesNotContain(t.Db.Conn.Table<Take>().ToList(), x => x.LineId == addedLineId);
    }

    [Fact]
    public void SetDisposition_OverrideRequiresBasis_AndPersistsBatchBasis()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "今天天气不错", 1000, 2400, 1200);
        SetTimingAndText(t, lines[2], "早上好", 200, 800, 700);
        SetTimingAndText(t, lines[3], "回头见", 300, 700, 500);

        var svc = new RevisionPackageService(t.Db);
        var report = svc.Analyze(RevisionDraftParser.Parse(DraftJson));
        var dur = report.Changes.Single(c => c.LineId == lines[1].Id);
        // 建议重剪，导演改判保留 → 必须给依据
        Assert.Throws<ArgumentException>(() => svc.SetDisposition(dur.Id, LineDisposition.Keep, null));
        svc.SetDisposition(dur.Id, LineDisposition.Keep, "剪辑师确认新时长在余量内，原剪接可沿用");
        Assert.Equal("剪辑师确认新时长在余量内，原剪接可沿用",
            t.Db.Conn.Find<RevisionChange>(dur.Id)!.DecisionBasis);

        // 批量把其余未决定项按建议处理，并保存批量依据
        var batch = svc.ApplySuggestedDispositions(report.PackageId, "本轮按系统建议批量圈定，导演周三会审确认");
        Assert.Equal(0, batch.Skipped);
        Assert.All(svc.Changes(report.PackageId).Where(c => c.Id != dur.Id),
            c => Assert.Contains("批量：", c.DecisionBasis));
        // 已逐条决定的不被批量覆盖
        Assert.Equal("剪辑师确认新时长在余量内，原剪接可沿用",
            t.Db.Conn.Find<RevisionChange>(dur.Id)!.DecisionBasis);
    }

    [Fact]
    public void Confirm_RejectsWhenUndecidedRemain()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "今天天气不错", 1000, 2400, 1200);
        SetTimingAndText(t, lines[2], "早上好", 200, 800, 700);
        SetTimingAndText(t, lines[3], "回头见", 300, 700, 500);
        var svc = new RevisionPackageService(t.Db);
        var report = svc.Analyze(RevisionDraftParser.Parse(DraftJson));
        Assert.Throws<InvalidOperationException>(() => svc.ConfirmPackage(report.PackageId));
    }

    [Fact]
    public async Task Withdraw_AfterConfirm_RestoresTextTimingStateTakesAndPick()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "今天天气不错", 1000, 2400, 1200);
        SetTimingAndText(t, lines[2], "早上好", 200, 800, 700);
        SetTimingAndText(t, lines[3], "回头见", 300, 700, 500);
        var take1 = await RecordAndPickAsync(t, lines[0], "张三", "a.wav");
        var take2 = await RecordAndPickAsync(t, lines[1], "张三", "b.wav");
        var take3 = await RecordAndPickAsync(t, lines[3], "李四", "c.wav");

        var svc = new RevisionPackageService(t.Db);
        var report = svc.Analyze(RevisionDraftParser.Parse(DraftJson));
        svc.ApplySuggestedDispositions(report.PackageId, "按建议");
        svc.ConfirmPackage(report.PackageId);
        var addedId = report.Changes.Single(c => c.Kind == LineChangeKind.Added).LineId;

        svc.WithdrawPackage(report.PackageId, "客户撤回该稿");

        Assert.Equal(RevisionState.Withdrawn, t.Db.Conn.Find<RevisionPackage>(report.PackageId)!.State);
        // 文字/时序/状态回退
        Assert.Equal("你好朋友", t.Db.Conn.Find<ScriptLine>(lines[0].Id)!.TranslatedText);
        Assert.Equal(1000, t.Db.Conn.Find<ScriptLine>(lines[1].Id)!.InPointMs);
        Assert.Equal(LineState.Active, t.Db.Conn.Find<ScriptLine>(lines[3].Id)!.State);
        // 条次与选用恢复
        Assert.Equal(TakeStatus.Usable, t.Db.Conn.Find<Take>(take1.Id)!.Status);
        Assert.Equal(TakeStatus.Usable, t.Db.Conn.Find<Take>(take2.Id)!.Status);
        Assert.Equal(TakeStatus.Usable, t.Db.Conn.Find<Take>(take3.Id)!.Status);
        Assert.Equal(PickStatus.Confirmed,
            t.Db.Conn.Table<DirectorPick>().ToList().First(p => p.LineId == lines[0].Id).Status);
        // 新增句无录音，随撤回删除
        Assert.Null(t.Db.Conn.Find<ScriptLine>(addedId));
        // 本包复核项全部回收
        Assert.False(t.Db.Conn.Table<ReviewItem>().Any(r => r.PackageId == report.PackageId));
    }

    [Fact]
    public async Task SplitLine_CreatesGenealogyAndNewAwaitingLine()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        var take = await RecordAndPickAsync(t, lines[0], "张三", "a.wav");

        var svc = new RevisionPackageService(t.Db);
        var pkg = svc.CreateStructuralPackage(reason: "拆分长句");
                var parts = new List<SplitPart>
        {
            new("你好啊", "你好", 100, 400),
            new("我的老朋友", "我的老朋友", 450, 900)
        };
        var splitChanges = svc.SplitLine(pkg.PackageId, lines[0].Id, parts);
        Assert.Equal(2, splitChanges.Count);
        Assert.Equal(LineDisposition.NeedsRerecord, splitChanges[0].SuggestedDisposition);
        Assert.Equal(LineDisposition.NewRecording, splitChanges[1].SuggestedDisposition);
        svc.ApplySuggestedDispositions(pkg.PackageId, "导演确认拆分");
        svc.ConfirmPackage(pkg.PackageId);

        var genes = t.Db.Conn.Table<LineGenealogy>().Where(g => g.PackageId == pkg.PackageId).ToList();
        Assert.Equal(2, genes.Count);
        Assert.All(genes, g => Assert.Equal(GenealogyKind.Split, g.Kind));
        var newPart = svc.Changes(pkg.PackageId).Single(c => c.Kind == LineChangeKind.Split && c.LineId == 0);
        var newLineId = newPart.TargetLineId!.Value;
        var newLine = t.Db.Conn.Find<ScriptLine>(newLineId)!;
        Assert.Equal("我的老朋友", newLine.OriginalText);
        Assert.Equal(450, newLine.InPointMs);
        Assert.False(t.Db.Conn.Table<Take>().Any(x => x.LineId == newLineId));
        // 源句旧条次待补录
        Assert.Equal(TakeStatus.NeedsReview, t.Db.Conn.Find<Take>(take.Id)!.Status);
    }

    [Fact]
    public async Task MergeLines_RetiresSourcesAndKeepsHistoryOutOfNewBatch()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "天气不错", 1000, 2400, 1200);
        var t1 = await RecordAndPickAsync(t, lines[0], "张三", "a.wav");
        var t2 = await RecordAndPickAsync(t, lines[1], "张三", "b.wav");

        var svc = new RevisionPackageService(t.Db);
        var pkg = svc.CreateStructuralPackage(reason: "合并短句");
        svc.MergeLines(pkg.PackageId, lines[0].Id, new[] { lines[1].Id },
            "你好啊朋友，今天天气不错", "你好，天气不错", 100, 2600, maxDurationMs: 2600);
        svc.ApplySuggestedDispositions(pkg.PackageId, "导演确认合并");
        svc.ConfirmPackage(pkg.PackageId);

        var target = t.Db.Conn.Find<ScriptLine>(lines[0].Id)!;
        Assert.Equal("你好啊朋友，今天天气不错", target.OriginalText);
        Assert.Equal(LineState.Retired, t.Db.Conn.Find<ScriptLine>(lines[1].Id)!.State);
        // 历史录音保留（不删除），但源句条次待复核，不会被当作新批次素材
        Assert.NotNull(t.Db.Conn.Find<Take>(t2.Id));
        Assert.Equal(TakeStatus.NeedsReview, t.Db.Conn.Find<Take>(t2.Id)!.Status);
        Assert.All(t.Db.Conn.Table<LineGenealogy>().Where(g => g.PackageId == pkg.PackageId).ToList(),
            g => Assert.Equal(GenealogyKind.Merge, g.Kind));
    }

    [Fact]
    public async Task TimelineShift_MovesPointsAndTakes_ReditOnly_AndRollsBack()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[2], "早上好", 200, 800, 700);
        var take = await RecordAndPickAsync(t, lines[2], "李四", "a.wav", new byte[] { 5 });
        var startBefore = t.Db.Conn.Find<Take>(take.Id)!.StartMs;

        var draft = RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "timelineShiftMs":500, "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友"},
  {"character":"甲","text":"今天天气不错"},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
""");
        var svc = new RevisionPackageService(t.Db);
        var report = svc.Analyze(draft);
        // 只有带录音的乙句被圈出“仅需重剪”
        var shiftChange = report.Changes.Single();
        Assert.Equal(lines[2].Id, shiftChange.LineId);
        Assert.Equal(LineDisposition.ReeditOnly, shiftChange.SuggestedDisposition);
        svc.ApplySuggestedDispositions(report.PackageId, "整片后移 12 帧");
        svc.ConfirmPackage(report.PackageId);

        Assert.Equal(700, t.Db.Conn.Find<ScriptLine>(lines[2].Id)!.InPointMs);
        Assert.Equal(startBefore + 500, t.Db.Conn.Find<Take>(take.Id)!.StartMs);
        Assert.Equal(TakeStatus.Usable, t.Db.Conn.Find<Take>(take.Id)!.Status); // 重剪不动状态
        Assert.True(t.Db.Conn.Table<ReviewItem>().Any(r => r.PackageId == report.PackageId));

        svc.WithdrawPackage(report.PackageId);
        Assert.Equal(200, t.Db.Conn.Find<ScriptLine>(lines[2].Id)!.InPointMs);
        Assert.Equal(startBefore, t.Db.Conn.Find<Take>(take.Id)!.StartMs);
    }

    [Fact]
    public async Task Deliver_CreatesNewFolderNeverOverwrites_AndCarriesChangeList()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "今天天气不错", 1000, 2400, 1200);
        SetTimingAndText(t, lines[2], "早上好", 200, 800, 700);
        SetTimingAndText(t, lines[3], "回头见", 300, 700, 500);
        await RecordAndPickAsync(t, lines[0], "张三", "a.wav");
        await RecordAndPickAsync(t, lines[1], "张三", "b.wav");
        await RecordAndPickAsync(t, lines[3], "李四", "c.wav");

        var svc = new RevisionPackageService(t.Db);
        var report = svc.Analyze(RevisionDraftParser.Parse(DraftJson));
        svc.ApplySuggestedDispositions(report.PackageId, "按建议");
        svc.ConfirmPackage(report.PackageId);

        var root = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "mix");
        var delivery = new RevisionDeliveryService(t.Db);
        var r1 = await delivery.DeliverAsync(report.PackageId, root);
        Assert.Equal(1, r1.Attempt);
        Assert.True(Directory.Exists(r1.OutputFolder));
        Assert.True(File.Exists(System.IO.Path.Combine(r1.OutputFolder, "changelist.json")));
        Assert.True(File.Exists(System.IO.Path.Combine(r1.OutputFolder, "changelist.txt")));
        // 重剪/保留句音频被复制，需补录句无新录音不计，删句不导出
        Assert.True(r1.ExportedFiles >= 1);
        var firstFiles = Directory.GetFiles(r1.OutputFolder, "*", SearchOption.AllDirectories);
        Assert.Contains(firstFiles, f => f.EndsWith(".wav"));

        // 再次交付：独立新目录 v2，绝不原位替换 v1
        var r2 = await delivery.DeliverAsync(report.PackageId, root);
        Assert.Equal(2, r2.Attempt);
        Assert.NotEqual(r1.OutputFolder, r2.OutputFolder);
        Assert.True(Directory.Exists(r1.OutputFolder));
        Assert.True(Directory.Exists(r2.OutputFolder));
        // 交付记录两条
        Assert.Equal(2, t.Db.Conn.Table<MixDelivery>().Count(d => d.RevisionPackageId == report.PackageId));
    }

    [Fact]
    public async Task ActorReplacement_KeepsHistoryButBlocksOldActorFromNewBatch()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        SetTimingAndText(t, lines[1], "天气", 1000, 2000, 1200);
        var oldTake = await RecordAndPickAsync(t, lines[0], "张三", "old.wav");
        await RecordAndPickAsync(t, lines[1], "张三", "old2.wav");
        var character = t.Db.Conn.Find<Character>(lines[0].CharacterId)!;

        var cast = new CastService(t.Db);
        cast.SetActor(character.Id, "张三"); // 初始演员就是历史录音的张三
        bool replaced = cast.SetActor(character.Id, "王五", "原演员档期冲突");
        Assert.True(replaced);

        // 历史录音保留
        Assert.NotNull(t.Db.Conn.Find<Take>(oldTake.Id));
        Assert.Equal("张三", t.Db.Conn.Find<Take>(oldTake.Id)!.Actor);
        // 但旧条次待复核，且被挡在新批次之外
        Assert.Equal(TakeStatus.NeedsReview, t.Db.Conn.Find<Take>(oldTake.Id)!.Status);
        Assert.False(cast.IsCurrentActor(t.Db.Conn.Find<Take>(oldTake.Id)!));
        Assert.True(t.Db.Conn.Table<ReviewItem>().Any(r => r.Kind == ReviewKind.ActorChanged));

        // 以旧演员名义再录 → 自动入复核，防止混入
        var rogue = (await new RecordingService(t.Db).AddTakeAsync(
            new NewTakeRequest(lines[0].Id, "张三", "MIC", 0, 90,
                ImportNumberingTests.MakeAudio(t, "rogue.wav")))).Take;
        Assert.Equal(TakeStatus.NeedsReview, rogue.Status);

        // 新演员补录后可以选用
        var newAudio = ImportNumberingTests.MakeAudio(t, "new.wav", new byte[] { 9, 8, 7 });
        var newTake = (await new RecordingService(t.Db).AddTakeAsync(
            new NewTakeRequest(lines[0].Id, "王五", "MIC", 100, 200, newAudio))).Take;
        Assert.Equal(TakeStatus.Usable, newTake.Status);

        // 交付修订包：王五的新条次导出，张三的历史条次计入 CastBlocked
        var svc = new RevisionPackageService(t.Db);
        var pkg = svc.Analyze(RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友","translatedText":"你好呀我的老朋友"},
  {"character":"甲","text":"今天天气不错","inPointMs":1100,"mouthCloseMs":2300,"maxDurationMs":1300},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
"""));
        var tweak = pkg.Changes.Single(c => c.LineId == lines[0].Id);
        svc.SetDisposition(tweak.Id, LineDisposition.NeedsRerecord, "换演员后统一补录");
        svc.ApplySuggestedDispositions(pkg.PackageId, "其余按建议");
        // 补录完成并选用
        new DirectorService(t.Db).PickTake(lines[0].Id, newTake.Id);
        new DirectorService(t.Db).ConfirmPick(lines[0].Id);
        svc.ConfirmPackage(pkg.PackageId);

        var root = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "mix2");
        var result = await new RevisionDeliveryService(t.Db).DeliverAsync(pkg.PackageId, root);
        // 王五的补录条次是唯一导出；张三在重剪句上的旧选用被演员守卫挡下（CastBlocked）
        var json = File.ReadAllText(System.IO.Path.Combine(result.OutputFolder, "changelist.json"));
        Assert.Contains("王五", json);
        Assert.Contains("张三", json); // 清单记录被挡原因
        Assert.Equal(1, result.ExportedFiles);
        Assert.Equal(1, result.CastBlocked);
        var audioDir = System.IO.Path.Combine(result.OutputFolder, "audio");
        Assert.Single(Directory.GetFiles(audioDir));
        // 被挡的是重剪句（第二句）的旧条次，导出的是第一句的新补录
        var blocked = new CastService(t.Db).FindBlockedTakes(new[] { lines[1].Id });
        Assert.Contains(blocked, b => b.RecordedActor == "张三" && b.CurrentActor == "王五");
    }

    [Fact]
    public void CrossCheck_FlagsRetireVsKeepConflict_AndOverlappingSplits()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[3], "回头见", 300, 700, 500);

        var svc = new RevisionPackageService(t.Db);
        // 两个修订组独立制稿：A 组删除该句，B 组对同句改时长并判定保留
        var draftDelete = """
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友"},
  {"character":"甲","text":"今天天气不错"},
  {"character":"乙","text":"早上好呀"}
]}]}
""";
        var pA = svc.Analyze(RevisionDraftParser.Parse(draftDelete));
        var pB = svc.Analyze(RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友"},
  {"character":"甲","text":"今天天气不错"},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见","inPointMs":350,"mouthCloseMs":720}
]}]}
"""));
        var aDelete = pA.Changes.Single(c => c.LineId == lines[3].Id);
        var bDur = pB.Changes.Single(c => c.LineId == lines[3].Id);
        svc.SetDisposition(aDelete.Id, LineDisposition.Retire, "A 组删除");
        svc.SetDisposition(bDur.Id, LineDisposition.Keep, "B 组判定保留");

        var cross = new RevisionCrossCheckService(t.Db).CrossCheck(pA.PackageId, pB.PackageId);
        Assert.False(cross.Clean);
        Assert.Contains(cross.Conflicts, c => c.LineId == lines[3].Id);
    }

    [Fact]
    public void Packages_HaveSequentialCodes_AndDraftWithdrawIsNoOpOnData()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        var svc = new RevisionPackageService(t.Db);
        var p1 = svc.Analyze(RevisionDraftParser.Parse(DraftJson));
        var p2 = svc.Analyze(RevisionDraftParser.Parse(DraftJson));
        Assert.Equal("REV-1", p1.PackageCode);
        Assert.Equal("REV-2", p2.PackageCode);

        // 草案阶段撤回：分析条目清空，在制译文不变
        svc.WithdrawPackage(p1.PackageId);
        Assert.Empty(svc.Changes(p1.PackageId));
        Assert.Equal("你好朋友", t.Db.Conn.Find<ScriptLine>(lines[0].Id)!.TranslatedText);
        Assert.Equal(RevisionState.Withdrawn, t.Db.Conn.Find<RevisionPackage>(p1.PackageId)!.State);
    }

    [Fact]
    public async Task LegacyActorTake_IsBlockedEvenWhenStillUsable_AndKeptInDatabase()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[1], "天气", 1000, 2000, 1200);
        var oldTake = await RecordAndPickAsync(t, lines[1], "张三", "old.wav");
        var character = t.Db.Conn.Find<Character>(lines[1].CharacterId)!;
        var cast = new CastService(t.Db);
        cast.SetActor(character.Id, "张三");
        cast.SetActor(character.Id, "王五");

        // 旧条次记录保留在数据库
        Assert.NotNull(t.Db.Conn.Find<Take>(oldTake.Id));
        Assert.False(cast.IsCurrentActor(t.Db.Conn.Find<Take>(oldTake.Id)!));

        // 一个仅需重剪的修订包仍想沿用旧选用 → 交付时被演员守卫挡下
        var svc = new RevisionPackageService(t.Db);
        var pkg = svc.Analyze(RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友"},
  {"character":"甲","text":"今天天气不错","inPointMs":1050,"mouthCloseMs":2050},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
"""));
        svc.ApplySuggestedDispositions(pkg.PackageId, "重剪对齐");
        svc.ConfirmPackage(pkg.PackageId);

        var root = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "mix3");
        var result = await new RevisionDeliveryService(t.Db).DeliverAsync(pkg.PackageId, root);
        Assert.Equal(0, result.ExportedFiles);
        Assert.Equal(1, result.CastBlocked);
        // 历史音频文件原位保留，交付目录里没有它
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(result.OutputFolder, "audio")));
        Assert.True(File.Exists(oldTake.AudioFilePath!));
    }

    [Fact]
    public async Task DeliveredPackage_CanBeWithdrawn_DeliveryFolderRemainsUntouched()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[1], "天气", 1000, 2000, 1200);
        await RecordAndPickAsync(t, lines[1], "张三", "a.wav");
        var svc = new RevisionPackageService(t.Db);
        var pkg = svc.Analyze(RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友"},
  {"character":"甲","text":"今天天气不错","inPointMs":1050,"mouthCloseMs":2050},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
"""));
        svc.ApplySuggestedDispositions(pkg.PackageId, "重剪");
        svc.ConfirmPackage(pkg.PackageId);
        var root = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(t.Path)!, "mix4");
        var delivery = await new RevisionDeliveryService(t.Db).DeliverAsync(pkg.PackageId, root);
        Assert.Equal(RevisionState.Delivered, t.Db.Conn.Find<RevisionPackage>(pkg.PackageId)!.State);

        // 已交混音后允许撤回（不能原位改文件），导出目录保留作追溯凭证
        svc.WithdrawPackage(pkg.PackageId, "客户撤销该版");
        Assert.Equal(RevisionState.Withdrawn, t.Db.Conn.Find<RevisionPackage>(pkg.PackageId)!.State);
        Assert.True(Directory.Exists(delivery.OutputFolder));
        Assert.True(File.Exists(System.IO.Path.Combine(delivery.OutputFolder, "changelist.json")));
        Assert.Equal(1000, t.Db.Conn.Find<ScriptLine>(lines[1].Id)!.InPointMs);

        // 撤回后必须用新修订包重新交付，不能再确认旧包
        Assert.Throws<InvalidOperationException>(() => svc.ConfirmPackage(pkg.PackageId));
        var next = svc.Analyze(RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友"},
  {"character":"甲","text":"今天天气不错","inPointMs":1080,"mouthCloseMs":2080},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
"""));
        Assert.Equal("REV-2", next.PackageCode);
    }

    [Fact]
    public void CrossCheck_WarnsOnRerecordVsReedit_AndRejectsSamePackage()
    {
        using var t = SeedProject();
        var lines = Lines(t);
        SetTimingAndText(t, lines[0], "你好朋友", 100, 900, 1000);
        var svc = new RevisionPackageService(t.Db);
        var pA = svc.Analyze(RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友","translatedText":"你好呀"},
  {"character":"甲","text":"今天天气不错"},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
"""));
        var pB = svc.Analyze(RevisionDraftParser.Parse("""
{ "episodeCode":"E01", "scenes":[{ "code":"S001", "lines":[
  {"character":"甲","text":"你好啊朋友","translatedText":"你好呀"},
  {"character":"甲","text":"今天天气不错"},
  {"character":"乙","text":"早上好呀"},
  {"character":"乙","text":"回头见"}
]}]}
"""));
        svc.ApplySuggestedDispositions(pA.PackageId, "A 补录");
        var tweakB = pB.Changes.Single(c => c.LineId == lines[0].Id);
        svc.SetDisposition(tweakB.Id, LineDisposition.Keep, "B 认为口型完全对得上");

        var cross = new RevisionCrossCheckService(t.Db).CrossCheck(pA.PackageId, pB.PackageId);
        Assert.True(cross.Clean); // 不是硬冲突
        Assert.Contains(cross.Warnings, w => w.LineId == lines[0].Id);
        Assert.Equal(1, cross.LinesTouchedByBoth);
        Assert.Throws<ArgumentException>(() =>
            new RevisionCrossCheckService(t.Db).CrossCheck(pA.PackageId, pA.PackageId));
    }

    [Fact]
    public void DraftParser_TextFormat_IsAccepted_ForStructuralEdits()
    {
        using var t = SeedProject();
        var draft = RevisionDraftParser.Parse("""
EPISODE E01
SCENE S001
甲: 你好啊朋友
甲: 今天天气不错
乙: 早上好呀
""");
        Assert.Equal(3, draft.Scenes[0].Lines.Count);
        Assert.Equal(0, draft.TimelineShiftMs);
        var report = new RevisionPackageService(t.Db).Analyze(draft);
        // “回头见”被删
        Assert.Equal(1, report.Deleted);
    }
}
