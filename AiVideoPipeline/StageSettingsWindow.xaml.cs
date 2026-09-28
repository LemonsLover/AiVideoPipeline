using System.Windows;
using System.Windows.Controls;
using AiVideoPipeline.ViewModels;
using Highlights.Core.Settings;

namespace AiVideoPipeline;

/// <summary>Settings of one pipeline step (opened with the pen icon).</summary>
public partial class StageSettingsWindow : Window
{
    private readonly StageSettingsViewModel _viewModel;

    public StageSettingsWindow(StageSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.Save() is { } error)
            {
                MessageBox.Show(this, error, "Check the values", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Highlights.Core.Pipeline.PipelineException)
        {
            MessageBox.Show(this, ex.Message, "Could not save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>Picks the editor for a setting by its kind.</summary>
public sealed class SettingTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Inline { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? Bool { get; set; }
    public DataTemplate? Multiline { get; set; }
    public DataTemplate? Prompt { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as SettingFieldViewModel)?.Kind switch
        {
            SettingKind.Bool => Bool,
            SettingKind.Choice => Choice,
            SettingKind.MultilineText => Multiline,
            SettingKind.Prompt => Prompt,
            _ => Inline,
        };
}
