using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubStudio.App.Services;
using DubStudio.Core.Import;
using DubStudio.Core.Models;
using DubStudio.Core.Services;

namespace DubStudio.App.ViewModels;

public partial class PackageRow : ObservableObject
{
    public RevisionPackage Package { get; }
    [ObservableProperty] private int _pendingChanges;
    public PackageRow(RevisionPackage package, int pendingChanges)
    {
        Package = package;
        _pendingChanges = pendingChanges;
    }
    public string Title => $"{Package.Code} [{StateText}] {Package.Reason}";
    public string StateText => Package.State switch
    {
        RevisionState.Draft => "草案·待导演确认",
        RevisionState.Confirmed => "已确认·可交付",
        RevisionState.Delivered => "已交混音",
        RevisionState.Withdrawn => "已撤回",
        _ => Package.State.ToString()
    };
}

public partial class ChangeRow
{
    public RevisionChange Change { get; }
    public string LineCode { get; }
    public string KindText => Change.Kind switch
    {
        LineChangeKind.TextTweak => "文字微调→需补录",
        LineChangeKind.DurationChanged => "时长变化→仅重剪",
        LineChangeKind.TextAndDuration => "文字+时长→需补录",
        LineChangeKind.Deleted => "删句",
        LineChangeKind.Added => "新增句",
        LineChangeKind.Split => "拆分",
        LineChangeKind.Merged => "合并",
        _ => Change.Kind.ToString()
    };
    public string DispositionText => Change.Disposition switch
    {
        LineDisposition.NeedsRerecord => "需补录",
        LineDisposition.ReeditOnly => "仅需重剪",
        LineDisposition.Keep => "可以保留",
        LineDisposition.Retire => "退役删句",
        LineDisposition.NewRecording => "等待补录",
        _ => "未决定"
    };
    public ChangeRow(RevisionChange change, string lineCode) { Change = change; LineCode = lineCode; }
}

/// <summary>混录后修订：提交新稿分析、逐条/批量处置、确认、撤回、交付修订包。</summary>
public partial class RevisionViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly IFileDialogs _dialogs;

    public ObservableCollectionEx<PackageRow> Packages { get; } = new();
    public ObservableCollectionEx<ChangeRow> Changes { get; } = new();

    [ObservableProperty] private PackageRow? _selectedPackage;
    [ObservableProperty] private ChangeRow? _selectedChange;
    [ObservableProperty] private string _batchBasis = "";
    [ObservableProperty] private string _decisionBasis = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summary = "";

    public RevisionViewModel(ProjectSession session, IFileDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        _session.Changed += Load;
        Load();
    }

    [RelayCommand]
    public void Load()
    {
        Packages.Clear();
        Changes.Clear();
        Summary = "";
        if (!_session.IsOpen) { StatusText = "请先打开工程"; return; }
        var db = _session.Require();
        foreach (var p in db.Conn.Table<RevisionPackage>().OrderByDescending(p => p.Id).ToList())
        {
            var pending = db.Conn.Table<RevisionChange>().ToList()
                .Count(c => c.PackageId == p.Id && c.Disposition == LineDisposition.Undecided);
            Packages.Add(new PackageRow(p, pending));
        }
        SelectedPackage = Packages.FirstOrDefault();
        StatusText = $"{Packages.Count} 个修订包";
    }

    partial void OnSelectedPackageChanged(PackageRow? value) => LoadChanges();

    private void LoadChanges()
    {
        Changes.Clear();
        if (SelectedPackage == null) return;
        var db = _session.Require();
        var codes = db.Conn.Table<ScriptLine>().ToDictionary(l => l.Id, l => l.Code);
        foreach (var c in _session.Revisions().Changes(SelectedPackage.Package.Id))
        {
            var lineId = c.LineId > 0 ? c.LineId : c.TargetLineId ?? 0;
            codes.TryGetValue(lineId, out var code);
            Changes.Add(new ChangeRow(c, code ?? $"#{lineId}"));
        }
        Summary = $"差异 {Changes.Count} 条｜未决定 " +
                  $"{Changes.Count(r => r.Change.Disposition == LineDisposition.Undecided)} 条";
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (!_session.IsOpen) { StatusText = "请先打开工程"; return; }
        try
        {
            var path = await _dialogs.OpenFileAsync(".json,.txt");
            if (path == null) { StatusText = "未选择修订稿"; return; }
            var content = File.ReadAllText(path);
            var draft = RevisionDraftParser.Parse(content);
            var report = _session.Revisions().Analyze(draft);
            StatusText = $"{report.PackageCode} 分析完成：文字微调 {report.TextTweaks}、" +
                         $"时长 {report.DurationChanges}、删 {report.Deleted}、增 {report.Added}" +
                         (report.TimelineShiftMs != 0 ? $"、平移 {report.TimelineShiftMs:0}ms" : "");
            Load();
        }
        catch (Exception ex) { StatusText = "分析失败：" + ex.Message; }
    }

    [RelayCommand]
    private void Decide(LineDisposition disposition)
    {
        if (SelectedChange == null) { StatusText = "请选择差异条目"; return; }
        try
        {
            _session.Revisions().SetDisposition(SelectedChange.Change.Id, disposition, DecisionBasis);
            StatusText = "处置已保存（含依据）";
            DecisionBasis = "";
            LoadChanges();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void BatchApply()
    {
        if (SelectedPackage == null) return;
        try
        {
            var r = _session.Revisions().ApplySuggestedDispositions(SelectedPackage.Package.Id, BatchBasis);
            StatusText = $"批量判断 {r.Applied} 条，剩余未决定 {r.Skipped} 条（依据已展开保存）";
            BatchBasis = "";
            LoadChanges();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (SelectedPackage == null) return;
        try
        {
            _session.Revisions().ConfirmPackage(SelectedPackage.Package.Id);
            StatusText = "修订包已确认并落实到在制数据";
            Load();
        }
        catch (Exception ex) { StatusText = "无法确认：" + ex.Message; }
    }

    [RelayCommand]
    private void Withdraw()
    {
        if (SelectedPackage == null) return;
        var wasDelivered = SelectedPackage.Package.State == RevisionState.Delivered;
        try
        {
            _session.Revisions().WithdrawPackage(SelectedPackage.Package.Id, "导演界面撤回");
            StatusText = wasDelivered
                ? "已交混音的包已撤回：导出目录保留，需以新修订包重新交付"
                : "修订包已撤回，在制数据按快照回退";
            Load();
        }
        catch (Exception ex) { StatusText = "撤回失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task DeliverAsync()
    {
        if (SelectedPackage == null) return;
        try
        {
            // 混音交付根目录：桌面下按工程命名的目录；每次交付生成独立 REV-n_vN 子目录
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "DubStudio交付",
                _session.ProjectName);
            var r = await _session.RevisionDelivery().DeliverAsync(SelectedPackage.Package.Id, root);
            StatusText = $"已导出修订包 {r.ExportedFiles} 个文件 → {r.OutputFolder}" +
                         (r.CastBlocked > 0 ? $"；{r.CastBlocked} 条历史演员录音被挡" : "") +
                         (r.RerecordPending > 0 ? $"；{r.RerecordPending} 句仍待补录" : "");
            Load();
        }
        catch (Exception ex) { StatusText = "交付失败：" + ex.Message; }
    }
}
