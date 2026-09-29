using System.Windows;
using System.Windows.Controls;
using AiVideoPipeline.ViewModels;
using Highlights.Core.Llm;
using Highlights.Core.Settings;

namespace AiVideoPipeline;

/// <summary>Settings of one pipeline step (opened with the pen icon).</summary>
public partial class StageSettingsWindow : Window
{
    private readonly StageSettingsViewModel _viewModel;
    private readonly ILlmClient _llm;

    public StageSettingsWindow(StageSettingsViewModel viewModel, ILlmClient llm)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _llm = llm;
    }

    private void OnBrowseModels(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingFieldViewModel field)
            return;
        // Empty = inherited: start from the default so "add as fallback" extends what the step actually uses.
        var current = SettingFieldViewModel.SplitList(field.Value.Trim().Length > 0 ? field.Value : field.Default);
        var picker = new ModelPickerViewModel(_llm, field.Field.Picker, current);
        if (new ModelPickerWindow(picker) { Owner = this }.ShowDialog() == true)
            field.Value = string.Join(", ", picker.Current);
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
