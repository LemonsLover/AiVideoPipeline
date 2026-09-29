using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;

namespace Highlights.Core.Settings;

public enum SettingKind { Text, MultilineText, Prompt, Number, Integer, Bool, Choice, List }

/// <summary>Where a setting is stored: configuration (pipeline.json), a prompt template, or the project's editing mode.</summary>
public enum SettingSource { Config, Prompt, Mode }

/// <summary>A list of OpenRouter model ids that can be picked from the catalog; Vision = the models must accept images.</summary>
public enum ModelPicker { None, Text, Vision }

/// <param name="Key">Config path ("Whisper:Model"), prompt file name ("plan.system.md") or mode property ("maxClipSeconds").</param>
/// <param name="Group">Section header in the settings window.</param>
/// <param name="Optional">An empty value is allowed and means "inherit" (e.g. per-step temperature).</param>
/// <param name="Picker">Offer a "Browse…" model picker for this field.</param>
public sealed record SettingField(
    string Key, string Label, SettingKind Kind, SettingSource Source = SettingSource.Config, string Group = "General",
    string? Help = null, IReadOnlyList<string>? Choices = null, bool Optional = false, ModelPicker Picker = ModelPicker.None);

public sealed record StageSettings(string Stage, string Title, IReadOnlyList<SettingField> Fields);

/// <summary>Settings shown behind the pen icon of each pipeline step. Steps without an entry have no settings.</summary>
public static class StageSettingsCatalog
{
    public static StageSettings? For(string stage) => All.GetValueOrDefault(stage);

    public static readonly IReadOnlyDictionary<string, StageSettings> All = new Dictionary<string, StageSettings>(StringComparer.OrdinalIgnoreCase)
    {
        [StageNames.Transcribe] = new(StageNames.Transcribe, "Transcription (Whisper)",
        [
            new("Whisper:Model", "Model", SettingKind.Text, Help: "ggml model name (large-v3-turbo, large-v3, medium, small…) or a path to a .bin file; downloaded on first use"),
            new("Whisper:Language", "Language", SettingKind.Text, Help: "auto, or a code: ru, uk, en, …"),
            new("Whisper:Prompt", "Recognition hint", SettingKind.MultilineText, Help: "Nicknames, game terms and slang — helps Whisper spell them right"),
            new("Whisper:NoContext", "Don't feed previous text back", SettingKind.Bool, Help: "Prevents repetition loops on noisy game audio"),
            new("Whisper:MaxConsecutiveRepeats", "Max repeats of a phrase", SettingKind.Integer, Help: "A phrase seen this many times among the last 6 segments is dropped as a hallucination"),
            new("Whisper:WordTimestamps", "Word timestamps", SettingKind.Bool, Help: "Needed to avoid cutting in the middle of words"),
            new("Whisper:Threads", "CPU threads", SettingKind.Integer, Help: "0 = automatic"),
        ]),

        [StageNames.AudioSignals] = new(StageNames.AudioSignals, "Audio signals (laughter, yelling, gunfire)",
        [
            new("AudioSignals:EventThreshold", "Sound event threshold", SettingKind.Number, Help: "YAMNet score 0–1 at which laughter/scream/gunfire becomes an event; lower = more events"),
            new("AudioSignals:MergeGapSeconds", "Merge events closer than (s)", SettingKind.Number),
            new("AudioSignals:LoudSpikeDb", "Loud spike (dB above surroundings)", SettingKind.Number, Group: "Loudness"),
            new("AudioSignals:MinSpikeDb", "Ignore spikes quieter than (dBFS)", SettingKind.Number, Group: "Loudness"),
            new("AudioSignals:BaselineWindowSeconds", "Baseline window (s)", SettingKind.Number, Group: "Loudness"),
            new("AudioSignals:LoudnessWindowSeconds", "Loudness window (s)", SettingKind.Number, Group: "Loudness"),
        ]),

        [StageNames.Frames] = new(StageNames.Frames, "Frames & motion",
        [
            new("Vision:FrameWidth", "Frame width (px)", SettingKind.Integer, Help: "Frames kept for the vision passes, one per second; wider = sharper but more tokens"),
            new("Vision:JpegQuality", "JPEG quality", SettingKind.Integer, Help: "2 = best … 31 = worst"),
        ]),

        [StageNames.Vision] = new(StageNames.Vision, "Watch the video (vision LLM)",
        [
            new("Vision:Enabled", "Enabled", SettingKind.Bool, Help: "Off = no frames are sent to an LLM; motion and game sounds are still used"),
            .. Llm(StageNames.Vision, ModelPicker.Vision),
            new("Vision:OverviewIntervalSeconds", "Frame every (s)", SettingKind.Integer, Group: "Overview"),
            new("Vision:OverviewBatchFrames", "Frames per request", SettingKind.Integer, Group: "Overview"),
            new("Vision:OverviewLowDetail", "Low detail (≈4× cheaper)", SettingKind.Bool, Group: "Overview", Help: "Kills and rounds are still recognized; small text like nicknames may be misread"),
            new("Vision:MaxParallelRequests", "Parallel requests", SettingKind.Integer, Group: "Overview"),
            new("vision.system.md", "System prompt", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
        ]),

        [StageNames.Analyze] = new(StageNames.Analyze, "Map the session (LLM)",
        [
            .. Llm(StageNames.Analyze),
            new("Analyze:OutputLanguage", "Output language", SettingKind.Text, Group: "General", Help: "Language of titles, captions, descriptions (all LLM steps)"),
            new("Analyze:MinLoudPeakDb", "Show loud spikes from (dB)", SettingKind.Number, Help: "Weaker loudness spikes are left out of the timeline the LLM reads"),
            new("analyze.system.md", "System prompt", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
            new("analyze.user.md", "User message template", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
        ]),

        [StageNames.Plan] = new(StageNames.Plan, "Plan the cut (LLM)",
        [
            .. Llm(StageNames.Plan),
            new("instructions", "Mode instructions", SettingKind.MultilineText, SettingSource.Mode, "Editing mode", "What this mode selects and how; saved for the project's current mode"),
            new("targetFraction", "Target length (fraction of the session)", SettingKind.Number, SettingSource.Mode, "Editing mode"),
            new("minMinutes", "Minimum length (min)", SettingKind.Number, SettingSource.Mode, "Editing mode"),
            new("maxMinutes", "Maximum length (min)", SettingKind.Number, SettingSource.Mode, "Editing mode"),
            new("maxClipSeconds", "Longest clip (s)", SettingKind.Integer, SettingSource.Mode, "Editing mode"),
            new("contextCaptions", "Context captions by default", SettingKind.Bool, SettingSource.Mode, "Editing mode"),
            new("plan.system.md", "System prompt", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
            new("plan.user.md", "User message template", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
        ]),

        [StageNames.Refine] = new(StageNames.Refine, "Refine cuts by frames (vision LLM)",
        [
            new("Vision:RefineEnabled", "Enabled", SettingKind.Bool, Help: "Also off when \"Watch the video\" is disabled"),
            .. Llm(StageNames.Refine, ModelPicker.Vision),
            new("Vision:RefineLeadInSeconds", "Look before the in point (s)", SettingKind.Integer, Group: "Window"),
            new("Vision:RefineTailSeconds", "Look after the out point (s)", SettingKind.Integer, Group: "Window"),
            new("refine.system.md", "System prompt", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
        ]),

        [StageNames.Postprocess] = new(StageNames.Postprocess, "Build clips",
        [
            new("prePaddingSeconds", "Padding before (s)", SettingKind.Number, SettingSource.Mode, "Editing mode"),
            new("postPaddingSeconds", "Padding after (s)", SettingKind.Number, SettingSource.Mode, "Editing mode"),
            new("maxSilenceSeconds", "Cut pauses longer than (s)", SettingKind.Number, SettingSource.Mode, "Editing mode", "0 = don't trim pauses"),
            new("keepSilenceSeconds", "Keep of a cut pause (s)", SettingKind.Number, SettingSource.Mode, "Editing mode"),
            new("Postprocess:MergeGapSeconds", "Merge clips closer than (s)", SettingKind.Number),
            new("Postprocess:MaxSnapSeconds", "Snap edges to words within (s)", SettingKind.Number),
            new("Postprocess:MinSegmentSeconds", "Drop pieces shorter than (s)", SettingKind.Number),
            new("Vision:ActionMotionFactor", "Action = motion above × median", SettingKind.Number, Help: "Stretches with this much on-screen motion are never trimmed as pauses"),
        ]),

        [StageNames.Render] = new(StageNames.Render, "Render video",
        [
            new("Render:ClipTransition", "Between clips", SettingKind.Choice, Group: "Transitions", Choices: RenderOptions.Transitions),
            new("Render:CrossfadeSeconds", "Between clips: length (s)", SettingKind.Number, Group: "Transitions", Help: "0 = hard cut"),
            new("Render:InnerTransition", "Inside a clip (jump cuts)", SettingKind.Choice, Group: "Transitions", Choices: RenderOptions.Transitions),
            new("Render:InnerCrossfadeSeconds", "Inside a clip: length (s)", SettingKind.Number, Group: "Transitions", Help: "0 = hard cut"),
            new("Render:FadeInSeconds", "Fade in at the start (s)", SettingKind.Number, Group: "Transitions"),
            new("Render:FadeOutSeconds", "Fade out at the end (s)", SettingKind.Number, Group: "Transitions"),

            new("Render:Captions", "Show context captions", SettingKind.Bool, Group: "Context captions", Help: "Off = never drawn, even if the plan wrote them"),
            new("Render:CaptionPosition", "Position", SettingKind.Choice, Group: "Context captions", Choices: RenderOptions.TextPositions),
            new("Render:CaptionFontSize", "Font size", SettingKind.Integer, Group: "Context captions"),
            new("Render:CaptionSeconds", "On screen for (s)", SettingKind.Number, Group: "Context captions"),
            new("Render:CaptionColor", "Text color", SettingKind.Text, Group: "Context captions", Help: "Name (white, yellow) or #RRGGBB"),
            new("Render:CaptionBoxOpacity", "Background opacity", SettingKind.Number, Group: "Context captions", Help: "0 = outlined text without a box, 1 = solid"),

            new("Render:TitlePosition", "Position", SettingKind.Choice, Group: "Titles", Choices: RenderOptions.TextPositions, Help: "Titles are switched on per project in the main window"),
            new("Render:TitleFontSize", "Font size", SettingKind.Integer, Group: "Titles"),
            new("Render:TitleSeconds", "On screen for (s)", SettingKind.Number, Group: "Titles"),
            new("Render:TitleColor", "Text color", SettingKind.Text, Group: "Titles"),
            new("Render:TitleBoxOpacity", "Background opacity", SettingKind.Number, Group: "Titles"),
            new("Render:TitleFont", "Font file (titles and captions)", SettingKind.Text, Group: "Titles"),
            new("Render:MaxLineChars", "Wrap lines at (characters)", SettingKind.Integer, Group: "Titles"),

            new("Render:Encoder", "Encoder", SettingKind.Choice, Group: "Encoding", Choices: ["auto", "nvenc", "x264"], Help: "auto = NVENC if the driver supports it, else x264"),
            new("Render:X264Preset", "x264 preset", SettingKind.Choice, Group: "Encoding", Choices: ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"]),
            new("Render:X264Crf", "x264 quality (CRF)", SettingKind.Integer, Group: "Encoding", Help: "Lower = better and bigger; 18 ≈ visually lossless"),
            new("Render:NvencPreset", "NVENC preset", SettingKind.Choice, Group: "Encoding", Choices: ["p1", "p2", "p3", "p4", "p5", "p6", "p7"]),
            new("Render:NvencCq", "NVENC quality (CQ)", SettingKind.Integer, Group: "Encoding"),
            new("Render:Width", "Width", SettingKind.Integer, Group: "Encoding"),
            new("Render:Height", "Height", SettingKind.Integer, Group: "Encoding"),
            new("Render:FrameRate", "Frame rate", SettingKind.Number, Group: "Encoding", Optional: true, Help: "Empty = source frame rate (max 60)"),

            new("Render:LoudnessLufs", "Loudness target (LUFS)", SettingKind.Number, Group: "Audio", Optional: true, Help: "YouTube ≈ -14; empty = no normalization"),
            new("Render:TruePeakDb", "True peak limit (dB)", SettingKind.Number, Group: "Audio"),
            new("Render:AudioBitrate", "AAC bitrate", SettingKind.Text, Group: "Audio"),
        ]),

        [StageNames.Describe] = new(StageNames.Describe, "YouTube text (LLM)",
        [
            .. Llm(StageNames.Describe),
            new("describe.system.md", "System prompt", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
            new("describe.user.md", "User message template", SettingKind.Prompt, SettingSource.Prompt, "Prompt"),
        ]),
    };

    /// <summary>Per-step LLM settings; empty = the general OpenRouter setting.</summary>
    private static SettingField[] Llm(string stage, ModelPicker picker = ModelPicker.Text) =>
    [
        new($"OpenRouter:Stages:{stage}:Models", "Models", SettingKind.List, Group: "Model", Optional: true,
            Help: picker == ModelPicker.Vision
                ? "OpenRouter model ids that accept images, primary first, comma-separated; empty = the general list. " +
                  "Browse… lists free and cheap ones (free models are rate limited: slower, but $0)"
                : "OpenRouter model ids, primary first, comma-separated; empty = the general list",
            Picker: picker),
        new($"OpenRouter:Stages:{stage}:Temperature", "Temperature", SettingKind.Number, Group: "Model", Optional: true,
            Help: "Empty = provider default (many current models ignore or reject it)"),
        new($"OpenRouter:Stages:{stage}:MaxOutputTokens", "Max output tokens", SettingKind.Integer, Group: "Model", Optional: true,
            Help: "Empty = the general limit"),
    ];
}
