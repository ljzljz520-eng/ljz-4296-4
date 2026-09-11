using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubStudio.App.Services;
using DubStudio.Core.Media;
using DubStudio.Core.Models;
using DubStudio.Core.Services;
using DubStudio.Core.Timecode;

namespace DubStudio.App.ViewModels;

public partial class ProjectsViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly FfmpegMediaService _ffmpeg;
    private readonly Services.IFileDialogs _dialogs;

    [ObservableProperty] private string _projectName = "新译制工程";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _fpsText = "23.976";
    [ObservableProperty] private bool _vfr;
    [ObservableProperty] private string _sourceMedia = "";
    [ObservableProperty] private string _mediaSummary = "尚未探测";

    public ProjectsViewModel(ProjectSession session, FfmpegMediaService ffmpeg, Services.IFileDialogs dialogs)
    {
        _session = session;
        _ffmpeg = ffmpeg;
        _dialogs = dialogs;
        _session.Changed += () =>
        {
            IsOpen = _session.IsOpen;
            StatusText = _session.IsOpen ? $"已打开：{_session.ProjectName}" : "未打开工程";
        };
        IsOpen = _session.IsOpen;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        try
        {
            var path = await _dialogs.SaveFileAsync(ProjectName + ".dsproj", ".dsproj");
            if (path == null) return;
            var fr = FrameRate.Parse(FpsText);
            _session.Create(path, ProjectName, fr.Num, fr.Den, Vfr);
            StatusText = "工程已创建：" + path;
        }
        catch (Exception ex) { StatusText = "创建失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        try
        {
            var path = await _dialogs.OpenFileAsync(".dsproj");
            if (path == null) return;
            _session.Open(path);
            var p = _session.Require().RequireProject();
            FpsText = p.FrameRateDen == 1 ? p.FrameRateNum.ToString() : (p.FrameRateNum / p.FrameRateDen).ToString("0.###");
            Vfr = p.VariableFrameRate;
            SourceMedia = p.SourceMediaPath ?? "";
            StatusText = "工程已打开：" + path;
        }
        catch (Exception ex) { StatusText = "打开失败：" + ex.Message; }
    }

    [RelayCommand]
    private void CloseProject()
    {
        _session.Close();
        StatusText = "工程已关闭";
    }

    [RelayCommand]
    private async Task BrowseMediaAsync()
    {
        try
        {
            var path = await _dialogs.OpenFileAsync();
            if (path == null) return;
            SourceMedia = path;
            StatusText = "正在通过 ffprobe 独立进程探测…";
            var info = await _ffmpeg.ProbeAsync(path);
            var vfr = info.VariableFrameRate;
            if (info.HasVideo && !vfr)
            {
                vfr = await _ffmpeg.AnalyzePacketSpacingAsync(path);
            }
            Vfr = vfr;
            if (info.AvgFrameRateNum is > 0)
                FpsText = info.AvgFrameRateDen == 1
                    ? info.AvgFrameRateNum.Value.ToString("0.###")
                    : $"{info.AvgFrameRateNum}/{info.AvgFrameRateDen}";
            var hash = await FileHasher.Sha256Async(path);
            if (_session.IsOpen)
            {
                var db = _session.Require();
                ProjectService.RegisterMedia(db, new MediaSource
                {
                    ProjectId = 1,
                    Path = path,
                    FrameRateNum = info.AvgFrameRateNum ?? 0,
                    FrameRateDen = info.AvgFrameRateDen ?? 1,
                    VariableFrameRate = vfr,
                    DurationMs = info.DurationMs,
                    ContentHash = hash
                });
            }
            MediaSummary = $"时长 {info.DurationMs / 1000:0.###}s | 视频 {info.HasVideo} | 音频 {info.HasAudio} | VFR {vfr}";
            StatusText = "探测完成";
        }
        catch (Exception ex)
        {
            MediaSummary = "探测失败";
            StatusText = "探测失败：" + ex.Message + "（请确认 ffprobe/ffmpeg 已安装）";
        }
    }


}
