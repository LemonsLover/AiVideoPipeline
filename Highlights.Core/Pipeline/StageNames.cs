namespace Highlights.Core.Pipeline;

public static class StageNames
{
    public const string Extract = "extract";
    public const string Transcribe = "transcribe";
    public const string AudioSignals = "audio-signals";
    public const string Frames = "frames";
    public const string Vision = "vision";
    public const string Analyze = "analyze";
    public const string Plan = "plan";
    public const string Refine = "refine";
    public const string Postprocess = "postprocess";
    public const string Review = "review";
    public const string Render = "render";
    public const string Describe = "describe";
}
