using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoPipeline.ViewModels;

public enum StageState { NotRun, UpToDate, Outdated, Running, Failed }

/// <summary>A pipeline stage row: status, progress and a short detail line.</summary>
public sealed partial class StageViewModel(string name, string title, bool usesLlm) : ObservableObject
{
    public string Name { get; } = name;
    public string Title { get; } = title;

    /// <summary>Re-running costs money: ask before forcing it.</summary>
    public bool UsesLlm { get; } = usesLlm;

    /// <summary>The step has settings (pen icon).</summary>
    public bool HasSettings { get; } = Highlights.Core.Settings.StageSettingsCatalog.For(name) is not null;

    [ObservableProperty]
    public partial StageState State { get; set; }

    [ObservableProperty]
    public partial double? Progress { get; set; }

    [ObservableProperty]
    public partial string? Detail { get; set; }
}
