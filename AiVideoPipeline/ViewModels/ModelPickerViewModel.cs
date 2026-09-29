using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Highlights.Core.Llm;
using Highlights.Core.Settings;

namespace AiVideoPipeline.ViewModels;

/// <summary>One row of the model picker.</summary>
public sealed class ModelRowViewModel(LlmModelInfo model)
{
    public LlmModelInfo Model { get; } = model;
    public string Id => Model.Id;
    public string Name => Model.Name.Length == 0 ? Model.Id : Model.Name;
    public string InputPrice => Price(Model.PromptPricePerMillion);
    public string OutputPrice => Price(Model.CompletionPricePerMillion);
    public string Context => Model.ContextLength is { } c ? $"{c / 1000}k" : "";
    public string Images => Model.AcceptsImages ? "✓" : "";
    public string JsonSchema => Model.SupportsJsonSchema ? "✓" : "";

    private static string Price(decimal perMillion) => perMillion < 0 ? "varies"
        : perMillion == 0 ? "free"
        : "$" + perMillion.ToString(perMillion < 1 ? "0.00##" : "0.00", CultureInfo.InvariantCulture);
}

/// <summary>Picks OpenRouter models from the live catalog for a step's Models list.</summary>
public sealed partial class ModelPickerViewModel : ObservableObject
{
    private readonly ILlmClient _llm;
    private IReadOnlyList<LlmModelInfo> _all = [];

    public ModelPickerViewModel(ILlmClient llm, ModelPicker picker, IEnumerable<string> current)
    {
        _llm = llm;
        RequiresImages = picker == ModelPicker.Vision;
        ImagesOnly = RequiresImages;
        Current = new ObservableCollection<string>(current);
    }

    /// <summary>The step sends frames: only image-capable models make sense.</summary>
    public bool RequiresImages { get; }
    public bool CanToggleImages => !RequiresImages;

    public ObservableCollection<ModelRowViewModel> Rows { get; } = [];

    /// <summary>The step's model list being edited, primary first.</summary>
    public ObservableCollection<string> Current { get; }

    public IReadOnlyList<string> SortOptions { get; } = ["Cheapest", "Name", "Largest context"];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool FreeOnly { get; set; }

    [ObservableProperty]
    public partial bool ImagesOnly { get; set; }

    [ObservableProperty]
    public partial bool StructuredOnly { get; set; }

    [ObservableProperty]
    public partial string Sort { get; set; } = "Cheapest";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseAsPrimaryCommand), nameof(AddAsFallbackCommand))]
    public partial ModelRowViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(MoveUpCommand))]
    public partial string? SelectedCurrent { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; } = "Loading the OpenRouter catalog…";

    public string Hint => RequiresImages
        ? "This step sends video frames, so only models that accept images are listed. Free models (\":free\") cost $0 " +
          "but have per-minute and daily request limits: the step waits when rate limited, then moves to the next model — " +
          "keep a cheap paid model as the fallback. Prices are USD per million tokens (a low-detail frame ≈ 300 tokens)."
        : "Free models (\":free\") cost $0 but have request limits; keep a paid fallback. Prices are USD per million tokens.";

    public async Task LoadAsync()
    {
        try
        {
            _all = await _llm.ListModelsAsync();
            Status = null;
            Apply();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or LlmException)
        {
            Status = "Could not load the model list: " + ex.Message;
        }
    }

    partial void OnSearchChanged(string value) => Apply();
    partial void OnFreeOnlyChanged(bool value) => Apply();
    partial void OnImagesOnlyChanged(bool value) => Apply();
    partial void OnStructuredOnlyChanged(bool value) => Apply();
    partial void OnSortChanged(string value) => Apply();

    private void Apply()
    {
        var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var models = _all.Where(m =>
            (!FreeOnly || m.IsFree)
            && (!ImagesOnly || m.AcceptsImages)
            && (!StructuredOnly || m.SupportsJsonSchema)
            && terms.All(t => m.Id.Contains(t, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(t, StringComparison.OrdinalIgnoreCase)));
        models = Sort switch
        {
            "Name" => models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            "Largest context" => models.OrderByDescending(m => m.ContextLength ?? 0),
            // Vision requests are mostly input (frames); weigh input price higher.
            _ => models.OrderBy(m => m.PromptPricePerMillion < 0 || m.CompletionPricePerMillion < 0) // "varies" last
                .ThenBy(m => m.PromptPricePerMillion * 4 + m.CompletionPricePerMillion).ThenBy(m => m.Id),
        };

        var selectedId = Selected?.Id;
        Rows.Clear();
        foreach (var m in models)
            Rows.Add(new ModelRowViewModel(m));
        Selected = Rows.FirstOrDefault(r => r.Id == selectedId);
        if (_all.Count > 0)
            Status = Rows.Count == 0 ? "No models match the filters." : null;
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void UseAsPrimary()
    {
        Current.Remove(Selected!.Id);
        Current.Insert(0, Selected.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddAsFallback()
    {
        if (!Current.Contains(Selected!.Id))
            Current.Add(Selected.Id);
    }

    private bool HasCurrentSelection() => SelectedCurrent is not null;

    [RelayCommand(CanExecute = nameof(HasCurrentSelection))]
    private void Remove() => Current.Remove(SelectedCurrent!);

    [RelayCommand(CanExecute = nameof(HasCurrentSelection))]
    private void MoveUp()
    {
        var item = SelectedCurrent!;
        var i = Current.IndexOf(item);
        if (i > 0)
        {
            Current.Move(i, i - 1);
            SelectedCurrent = item;
        }
    }
}
