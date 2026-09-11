using DubStudio.App.Pages;
using DubStudio.App.ViewModels;
using DubStudio.Core.Data;
using DubStudio.Core.Media;
using DubStudio.Core.Services;
using Microsoft.Extensions.Logging;

namespace DubStudio.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

#if WINDOWS
        builder.Services.AddSingleton<Services.IFileDialogs, Platforms.Windows.WindowsFileDialogs>();
#elif MACCATALYST
        builder.Services.AddSingleton<Services.IFileDialogs, Platforms.MacCatalyst.MacFileDialogs>();
#else
        builder.Services.AddSingleton<Services.IFileDialogs, Services.FallbackFileDialogs>();
#endif
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton(sp =>
            new FfmpegMediaService(sp.GetRequiredService<IProcessRunner>()));
        builder.Services.AddSingleton<ProjectSession>();

        builder.Services.AddTransient<ProjectsViewModel>();
        builder.Services.AddTransient<ProjectsPage>();
        builder.Services.AddTransient<ScriptViewModel>();
        builder.Services.AddTransient<ScriptPage>();
        builder.Services.AddTransient<RecordingViewModel>();
        builder.Services.AddTransient<RecordingPage>();
        builder.Services.AddTransient<DirectorViewModel>();
        builder.Services.AddTransient<DirectorPage>();
        builder.Services.AddTransient<RevisionViewModel>();
        builder.Services.AddTransient<RevisionPage>();
        builder.Services.AddTransient<ReviewViewModel>();
        builder.Services.AddTransient<ReviewPage>();
        builder.Services.AddTransient<ToolsViewModel>();
        builder.Services.AddTransient<ToolsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
