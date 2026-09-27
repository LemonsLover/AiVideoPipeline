using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace AiVideoPipeline.Services;

public interface IDialogService
{
    /// <summary>Asks for a video or a project.json; null when cancelled.</summary>
    string? PickVideoOrProject();

    bool Confirm(string message, string title);

    void ShowError(string message);

    /// <summary>Opens a file or folder with its default application (Explorer, player, editor).</summary>
    void OpenInShell(string path);
}

public sealed class DialogService : IDialogService
{
    public string? PickVideoOrProject()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a gameplay recording or a project",
            Filter = "Videos and projects|*.mkv;*.mp4;*.mov;*.avi;*.webm;*.flv;project.json|All files|*.*",
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

    public void ShowError(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, "Highlights", MessageBoxButton.OK, MessageBoxImage.Error);

    public void OpenInShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
