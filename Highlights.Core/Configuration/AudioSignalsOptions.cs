namespace Highlights.Core.Configuration;

public sealed class AudioSignalsOptions
{
    public const string SectionName = "AudioSignals";

    /// <summary>Loudness (RMS dBFS) window.</summary>
    public double LoudnessWindowSeconds { get; set; } = 0.1;

    /// <summary>Loudness spike = smoothed level this many dB above the rolling median.</summary>
    public double LoudSpikeDb { get; set; } = 10;

    /// <summary>Rolling median window for the loudness baseline.</summary>
    public double BaselineWindowSeconds { get; set; } = 60;

    /// <summary>Levels below this are never spikes (keeps noise in quiet parts out).</summary>
    public double MinSpikeDb { get; set; } = -40;

    /// <summary>Zip with yamnet.onnx + labels.txt (Qualcomm AI Hub export: input [1,1,96,64] log-mel patch).</summary>
    public string YamnetModelUrl { get; set; } =
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/yamnet/releases/v0.63.0/yamnet-onnx-float.zip";

    /// <summary>A group is "active" in a 0.96 s window when its score reaches this value.</summary>
    public double EventThreshold { get; set; } = 0.2;

    /// <summary>Events of the same type closer than this are merged.</summary>
    public double MergeGapSeconds { get; set; } = 1.0;

    /// <summary>Named groups of AudioSet class names (see labels.txt); a group's score is the max of its classes.</summary>
    public Dictionary<string, string[]>? ClassGroups { get; set; }

    public IReadOnlyDictionary<string, string[]> EffectiveClassGroups => ClassGroups is { Count: > 0 }
        ? ClassGroups
        : new Dictionary<string, string[]>
        {
            ["laughter"] = ["Laughter", "Giggle", "Snicker", "Belly laugh", "Chuckle, chortle", "Baby laughter"],
            ["scream"] = ["Screaming", "Shout", "Yell", "Whoop", "Children shouting"],
            ["gasp"] = ["Gasp"],
            ["cheer"] = ["Cheering", "Applause", "Clapping"],
        };

    /// <summary>
    /// Groups stored as score curves only, without events: context for later stages
    /// (e.g. transcript lines where there is music but no speech are likely Whisper hallucinations).
    /// </summary>
    public Dictionary<string, string[]>? ContextGroups { get; set; }

    public IReadOnlyDictionary<string, string[]> EffectiveContextGroups => ContextGroups is { Count: > 0 }
        ? ContextGroups
        : new Dictionary<string, string[]>
        {
            ["speech"] = ["Speech", "Conversation", "Narration, monologue"],
            ["music"] = ["Music"],
        };
}
