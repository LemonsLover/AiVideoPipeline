using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Highlights.Core.Projects;
using Highlights.Core.Settings;

namespace AiVideoPipeline.ViewModels;

/// <summary>One editable setting in the step settings window.</summary>
public sealed partial class SettingFieldViewModel : ObservableObject
{
    public SettingFieldViewModel(SettingValue value)
    {
        Field = value.Field;
        Default = value.Default;
        Value = value.Value;
    }

    public SettingField Field { get; }
    public string Default { get; }

    public string Label => Field.Label;
    public string? Help => Field.Help;
    public string Group => Field.Group;
    public SettingKind Kind => Field.Kind;
    public IReadOnlyList<string> Choices => Field.Choices ?? [];

    /// <summary>Shown under the field: the default, so it's clear what a reset does.</summary>
    public string DefaultText => Kind is SettingKind.Prompt or SettingKind.MultilineText
        ? ""
        : $"Default: {(Default.Length == 0 ? (Field.Optional ? "inherit" : "empty") : Default)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(BoolValue))]
    public partial string Value { get; set; }

    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    public bool IsModified => new SettingValue(Field, Value, Default).IsModified;

    [RelayCommand]
    private void Reset() => Value = Default;

    /// <summary>Error text for an unparseable number, else null.</summary>
    public string? Validate()
    {
        var v = Value.Trim();
        if (v.Length == 0)
            return Field.Optional || Kind is SettingKind.Text or SettingKind.MultilineText or SettingKind.Prompt or SettingKind.List
                ? null
                : $"{Label}: a value is required";
        return Kind switch
        {
            SettingKind.Integer when !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => $"{Label}: whole number expected",
            SettingKind.Number when !double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _) => $"{Label}: number expected",
            _ => null,
        };
    }
}

/// <summary>The settings window of one pipeline step.</summary>
public sealed partial class StageSettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly HighlightsProject? _project;

    public StageSettingsViewModel(StageSettings stage, SettingsStore store, HighlightsProject? project)
    {
        Stage = stage;
        _store = store;
        _project = project;
        Fields = new ObservableCollection<SettingFieldViewModel>(store.Load(stage, project).Select(v => new SettingFieldViewModel(v)));
        ModeNote = stage.Fields.Any(f => f.Source == SettingSource.Mode)
            ? $"\"Editing mode\" fields change the mode \"{store.ModeId(project)}\" (used by every project in that mode)."
            : null;
    }

    public StageSettings Stage { get; }
    public string Title => Stage.Title;
    public ObservableCollection<SettingFieldViewModel> Fields { get; }
    public string? ModeNote { get; }

    public string Footer => "Saved to %USERPROFILE%\\.highlights (pipeline.json, prompts\\, modes\\) — the built-in defaults stay untouched.";

    /// <summary>Validates and saves; returns an error message, or null on success.</summary>
    public string? Save()
    {
        var errors = Fields.Select(f => f.Validate()).OfType<string>().ToList();
        if (errors.Count > 0)
            return string.Join("\n", errors);
        _store.Save(Stage, _project, Fields.ToDictionary(f => f.Field, f => f.Value));
        return null;
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var f in Fields)
            f.Value = f.Default;
    }
}
