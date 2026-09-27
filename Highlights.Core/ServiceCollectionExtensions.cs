using Highlights.Core.Configuration;
using Highlights.Core.Media;
using Highlights.Core.Models;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Highlights.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHighlightsCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolsOptions>(configuration.GetSection(ToolsOptions.SectionName));
        services.Configure<ModelsOptions>(configuration.GetSection(ModelsOptions.SectionName));
        services.Configure<WhisperOptions>(configuration.GetSection(WhisperOptions.SectionName));
        services.Configure<AudioSignalsOptions>(configuration.GetSection(AudioSignalsOptions.SectionName));

        services.AddSingleton<ToolLocator>();
        services.AddSingleton<IMediaProbe, MediaProbe>();
        services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.AddSingleton<IProjectStore, ProjectStore>();

        services.AddHttpClient(ModelDownloader.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<ModelDownloader>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<YamnetModelManager>();

        services.AddSingleton<IPipelineStage, ExtractStage>();
        services.AddSingleton<IPipelineStage, TranscribeStage>();
        services.AddSingleton<IPipelineStage, AudioSignalsStage>();
        services.AddSingleton<PipelineRunner>();

        return services;
    }
}
