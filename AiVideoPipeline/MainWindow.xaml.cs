using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AiVideoPipeline.ViewModels;

namespace AiVideoPipeline;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        // VideoView needs its MediaPlayer after the native window exists.
        Loaded += (_, _) => VideoView.MediaPlayer = viewModel.Player.Player;
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    /// <summary>Space toggles playback unless the user is typing.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox)
        {
            _viewModel.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && _viewModel.IsIdle)
            await _viewModel.OpenPathAsync(files[0]);
    }

    private void OnTimelineClipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipViewModel clip })
            _viewModel.SelectedClip = clip;
    }
}
