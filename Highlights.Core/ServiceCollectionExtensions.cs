using Highlights.Core.Configuration;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Highlights.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHighlightsCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolsOptions>(configuration.GetSection(ToolsOptions.SectionName));
        services.Configure<WhisperOptions>(configuration.GetSection(WhisperOptions.SectionName));

        services.AddSingleton<ToolLocator>();
        services.AddSingleton<IMediaProbe, MediaProbe>();
        services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.AddSingleton<IProjectStore, ProjectStore>();

        services.AddHttpClient(WhisperModelManager.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<WhisperModelManager>();

        services.AddSingleton<IPipelineStage, ExtractStage>();
        services.AddSingleton<IPipelineStage, TranscribeStage>();
        services.AddSingleton<PipelineRunner>();

        return services;
    }
}
