using System.Windows;
using Microsoft.Win32;

namespace AiVideoPipeline;

/// <summary>Asks for a video link and a download folder.</summary>
public partial class ImportWindow : Window
{
    public ImportWindow(string folder, string? clipboardLink)
    {
        InitializeComponent();
        FolderBox.Text = folder;
        UrlBox.Text = clipboardLink ?? "";
        UrlBox.SelectAll();
    }

    public string Url => UrlBox.Text.Trim();
    public string Folder => FolderBox.Text;

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save imported videos to", InitialDirectory = Folder };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "Paste a video link, e.g. https://youtu.be/…", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
