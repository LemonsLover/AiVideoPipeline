using System.Windows;
using System.Windows.Input;
using AiVideoPipeline.ViewModels;

namespace AiVideoPipeline;

/// <summary>Picks OpenRouter models (with prices) for a step's Models list.</summary>
public partial class ModelPickerWindow : Window
{
    private readonly ModelPickerViewModel _viewModel;

    public ModelPickerWindow(ModelPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Double-click = use as primary.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.UseAsPrimaryCommand.CanExecute(null))
            _viewModel.UseAsPrimaryCommand.Execute(null);
    }
}
