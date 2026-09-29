using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace AiVideoPipeline.Services;

public interface IDialogService
{
    /// <summary>Asks for a video or a project.json; null when cancelled.</summary>
    string? PickVideoOrProject();

    bool Confirm(string message, string title);

    /// <summary>Asks for a video link and download folder; null when cancelled.</summary>
    (string Url, string Folder)? AskImport(string defaultFolder);

    void ShowError(string message);

    /// <summary>Shows a pipeline step's settings; true when they were saved.</summary>
    bool EditStageSettings(ViewModels.StageSettingsViewModel settings);

    /// <summary>Opens a file or folder with its default application (Explorer, player, editor).</summary>
    void OpenInShell(string path);
}

public sealed class DialogService(Highlights.Core.Llm.ILlmClient llm) : IDialogService
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

    public (string Url, string Folder)? AskImport(string defaultFolder)
    {
        // Pre-fill a link from the clipboard: usually the user just copied it from YouTube.
        var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
        var link = clip is not null && Uri.TryCreate(clip, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") ? clip : null;
        var window = new ImportWindow(defaultFolder, link) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? (window.Url, window.Folder) : null;
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

    public bool EditStageSettings(ViewModels.StageSettingsViewModel settings) =>
        new StageSettingsWindow(settings, llm) { Owner = Application.Current.MainWindow }.ShowDialog() == true;

    public void ShowError(string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, "Highlights", MessageBoxButton.OK, MessageBoxImage.Error);

    public void OpenInShell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
