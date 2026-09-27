using System.Windows;
using System.Windows.Threading;
using AiVideoPipeline.Services;
using AiVideoPipeline.ViewModels;
using Highlights.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiVideoPipeline;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        LibVLCSharp.Shared.Core.Initialize();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddHighlightsConfiguration();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging")).AddDebug();
        builder.Services.AddHighlightsCore(builder.Configuration);

        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<PlayerService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;

        // "HighlightsStudio.exe <video or project>" opens it once the window (and its video surface) exists:
        // starting VLC playback before VideoView is attached makes VLC open its own window.
        if (e.Args.Length > 0)
            window.ContentRendered += async (_, _) =>
                await _host.Services.GetRequiredService<MainViewModel>().OpenPathAsync(e.Args[0]);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Services.GetService<PlayerService>()?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
