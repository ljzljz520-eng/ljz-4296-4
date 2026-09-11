using DubStudio.Core.Data;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record CrossConflict(
    int LineId,
    string LineCode,
    string PackageA,
    LineChangeKind KindA,
    LineDisposition DispositionA,
    string PackageB,
    LineChangeKind KindB,
    LineDisposition DispositionB,
    string Detail);

public sealed record CrossWarning(string PackageCode, int LineId, string LineCode, string Detail);

public sealed record CrossCheckReport(
    string PackageA,
    string PackageB,
    List<CrossConflict> Conflicts,
    List<CrossWarning> Warnings,
    int LinesTouchedByBoth,
    bool Clean);

/// <summary>两个修订包交叉测试：按稳定语句身份（行 Id，删除行同样保留身份）找出
/// 处置互相冲突（一个退役、一个保留）、身份重复（同句都拆分/新增）以及交付缺口。
/// 两个包独立制作、先后交付时用于合并前检查。</summary>
public sealed class RevisionCrossCheckService
{
    private readonly StudioDatabase _db;
    public RevisionCrossCheckService(StudioDatabase db) => _db = db;

    public CrossCheckReport CrossCheck(int packageAId, int packageBId)
    {
        var a = _db.Conn.Find<RevisionPackage>(packageAId)
            ?? throw new InvalidOperationException("修订包 A 不存在");
        var b = _db.Conn.Find<RevisionPackage>(packageBId)
            ?? throw new InvalidOperationException("修订包 B 不存在");
        if (a.Id == b.Id) throw new ArgumentException("不能与同一个修订包交叉测试");

        var ca = _db.Conn.Table<RevisionChange>().Where(c => c.PackageId == a.Id).ToList();
        var cb = _db.Conn.Table<RevisionChange>().Where(c => c.PackageId == b.Id).ToList();

        var conflicts = new List<CrossConflict>();
        var warnings = new List<CrossWarning>();

        var byLineA = ca.Where(c => c.LineId > 0).GroupBy(c => c.LineId).ToDictionary(g => g.Key, g => g.ToList());
        var byLineB = cb.Where(c => c.LineId > 0).GroupBy(c => c.LineId).ToDictionary(g => g.Key, g => g.ToList());
        var targetA = ca.Where(c => (c.TargetLineId ?? 0) > 0).ToDictionary(c => c.TargetLineId!.Value, c => c);
        var targetB = cb.Where(c => (c.TargetLineId ?? 0) > 0).ToDictionary(c => c.TargetLineId!.Value, c => c);

        var shared = new HashSet<int>();
        foreach (var (lineId, listA) in byLineA)
        {
            if (!byLineB.TryGetValue(lineId, out var listB)) continue;
            shared.Add(lineId);
            var code = _db.Conn.Find<ScriptLine>(lineId)?.Code ?? $"#{lineId}";
            foreach (var x in listA)
                foreach (var y in listB)
                    CheckPair(x, y, code, a.Code, b.Code, conflicts, warnings);
        }

        // 同一稳定身份在两个包里被各自拆出/合并出指向同一新句 → 血缘冲突
        foreach (var (targetId, x) in targetA)
        {
            if (targetB.TryGetValue(targetId, out var y))
            {
                var code = _db.Conn.Find<ScriptLine>(targetId)?.Code ?? $"#{targetId}";
                conflicts.Add(new CrossConflict(targetId, code,
                    a.Code, x.Kind, x.Disposition, b.Code, y.Kind, y.Disposition,
                    "两个修订包对同一语句身份都执行了拆分/新增，合并会重复建句"));
                shared.Add(targetId);
            }
        }

        // 交付缺口：一个包退役的句，另一个包仍计划交付（重剪/保留）
        foreach (var ch in ca.Concat(cb))
        {
            if (ch.LineId == 0) continue;
            var pkg = ch.PackageId == a.Id ? a : b;
            var other = ch.PackageId == a.Id ? b : a;
            var others = ch.PackageId == a.Id ? cb : ca;
            var counterpart = others.FirstOrDefault(o => o.LineId == ch.LineId);
            if (counterpart == null && ch.Disposition is LineDisposition.Retire)
            {
                var code = _db.Conn.Find<ScriptLine>(ch.LineId)?.Code ?? $"#{ch.LineId}";
                warnings.Add(new CrossWarning(other.Code, ch.LineId, code,
                    $"{pkg.Code} 退役该句，但 {other.Code} 未涉及；合并前确认 {other.Code} 的交付清单不再包含它"));
            }
        }

        return new CrossCheckReport(a.Code, b.Code, conflicts, warnings,
            shared.Count, conflicts.Count == 0);
    }

    private static void CheckPair(RevisionChange x, RevisionChange y, string code,
        string codeA, string codeB, List<CrossConflict> conflicts, List<CrossWarning> warnings)
    {
        var retA = x.Disposition == LineDisposition.Retire
                   || (x.Kind == LineChangeKind.Deleted && x.Disposition != LineDisposition.Keep);
        var retB = y.Disposition == LineDisposition.Retire
                   || (y.Kind == LineChangeKind.Deleted && y.Disposition != LineDisposition.Keep);
        if (retA != retB)
        {
            conflicts.Add(new CrossConflict(x.LineId, code, codeA, x.Kind, x.Disposition,
                codeB, y.Kind, y.Disposition,
                $"处置冲突：{codeA} 判定{(retA ? "退役" : "保留")}，{codeB} 判定{(retB ? "退役" : "保留")}"));
            return;
        }

        var rerecordA = x.Disposition is LineDisposition.NeedsRerecord or LineDisposition.NewRecording;
        var rerecordB = y.Disposition is LineDisposition.NeedsRerecord or LineDisposition.NewRecording;
        if (rerecordA != rerecordB)
            warnings.Add(new CrossWarning(codeB, x.LineId, code,
                $"{codeA} 要求补录而 {codeB} 仅重剪/保留：合并后以补录为准，避免漏录"));

        if (x.Kind is LineChangeKind.Split or LineChangeKind.Merged
            && y.Kind is LineChangeKind.Split or LineChangeKind.Merged)
            conflicts.Add(new CrossConflict(x.LineId, code, codeA, x.Kind, x.Disposition,
                codeB, y.Kind, y.Disposition,
                "两个修订包对同一语句分别做了拆分/合并，结构操作不可叠加，需人工合并剧本"));
    }
}
