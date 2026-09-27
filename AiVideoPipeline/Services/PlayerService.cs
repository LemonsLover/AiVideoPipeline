using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;

namespace AiVideoPipeline.Services;

/// <summary>
/// LibVLC player with a "play this range" mode for clip review. Time/position are polled on the UI thread,
/// so bindings never see VLC's worker threads.
/// </summary>
public sealed partial class PlayerService : ObservableObject, IDisposable
{
    private readonly LibVLC _libVlc = new("--no-video-title-show");
    private readonly DispatcherTimer _timer;
    private double? _stopAt;

    public PlayerService()
    {
        Player = new MediaPlayer(_libVlc);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Poll(),
            Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public MediaPlayer Player { get; }

    /// <summary>Current time in seconds (updated ~10×/s).</summary>
    [ObservableProperty]
    public partial double Time { get; private set; }

    /// <summary>
    /// Two-way slider binding: reads the current time; writes seek only when the value really moved
    /// (the 10 Hz time updates echoing back through the binding must not cause seeks).
    /// </summary>
    public double Position
    {
        get => Time;
        set
        {
            if (Math.Abs(value - Time) > 0.3)
                Seek(value);
        }
    }

    partial void OnTimeChanged(double value) => OnPropertyChanged(nameof(Position));

    [ObservableProperty]
    public partial double Duration { get; private set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial string? LoadedPath { get; private set; }

    public void Load(string path)
    {
        if (LoadedPath == path)
            return;
        using var media = new Media(_libVlc, new Uri(path));
        Player.Media = media;
        LoadedPath = path;
        _stopAt = null;
        // VLC reports the length and shows a frame only once playback starts: play muted, pause on the first
        // "Playing" event. VLC events arrive on its own thread and must not call back into VLC synchronously.
        Player.Mute = true;
        Player.Playing += PauseOnFirstFrame;
        Player.Play();
    }

    private void PauseOnFirstFrame(object? sender, EventArgs e)
    {
        Player.Playing -= PauseOnFirstFrame;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Player.SetPause(true);
            Player.Mute = false;
        });
    }

    public void Unload()
    {
        Player.Stop();
        Player.Media = null;
        LoadedPath = null;
        Time = Duration = 0;
    }

    public void TogglePlay()
    {
        if (Player.Media is null)
            return;
        _stopAt = null;
        if (Player.IsPlaying)
            Player.SetPause(true);
        else if (Player.State == VLCState.Ended)
        {
            Player.Stop();
            Player.Play();
        }
        else
            Player.Play();
    }

    public void Seek(double seconds)
    {
        if (Player.Media is null)
            return;
        Player.Time = (long)(Math.Max(0, seconds) * 1000);
        Time = seconds;
    }

    /// <summary>Plays [start, end) and pauses at the end.</summary>
    public void PlayRange(double start, double end)
    {
        if (Player.Media is null)
            return;
        if (!Player.IsPlaying)
            Player.Play();
        Seek(start);
        _stopAt = end;
    }

    private void Poll()
    {
        IsPlaying = Player.IsPlaying;
        if (Player.Length > 0)
            Duration = Player.Length / 1000.0;
        if (Player.Media is null)
            return;

        Time = Math.Max(0, Player.Time / 1000.0);
        if (_stopAt is { } stop && Time >= stop)
        {
            _stopAt = null;
            Player.SetPause(true);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        Player.Dispose();
        _libVlc.Dispose();
    }
}
