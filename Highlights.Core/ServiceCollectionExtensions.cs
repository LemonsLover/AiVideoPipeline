using Highlights.Core.Configuration;
using Highlights.Core.Llm;
using Highlights.Core.Media;
using Highlights.Core.Models;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Highlights.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHighlightsCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolsOptions>(configuration.GetSection(ToolsOptions.SectionName));
        services.Configure<ModelsOptions>(configuration.GetSection(ModelsOptions.SectionName));
        services.Configure<WhisperOptions>(configuration.GetSection(WhisperOptions.SectionName));
        services.Configure<AudioSignalsOptions>(configuration.GetSection(AudioSignalsOptions.SectionName));
        services.Configure<OpenRouterOptions>(configuration.GetSection(OpenRouterOptions.SectionName));
        services.Configure<AnalyzeOptions>(configuration.GetSection(AnalyzeOptions.SectionName));

        services.AddSingleton<ToolLocator>();
        services.AddSingleton<IMediaProbe, MediaProbe>();
        services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.AddSingleton<IProjectStore, ProjectStore>();

        services.AddHttpClient(ModelDownloader.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<ModelDownloader>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<YamnetModelManager>();

        services.AddHttpClient(OpenRouterClient.HttpClientName, (sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
            c.BaseAddress = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
            c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        services.AddSingleton<ILlmClient, OpenRouterClient>();
        services.AddSingleton<StructuredLlm>();
        services.AddSingleton<GameProfileStore>();

        services.AddSingleton<IPipelineStage, ExtractStage>();
        services.AddSingleton<IPipelineStage, TranscribeStage>();
        services.AddSingleton<IPipelineStage, AudioSignalsStage>();
        services.AddSingleton<IPipelineStage, AnalyzeStage>();
        services.AddSingleton<PipelineRunner>();

        return services;
    }
}
