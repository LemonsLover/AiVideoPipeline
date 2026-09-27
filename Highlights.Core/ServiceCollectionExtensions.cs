using Highlights.Core.Configuration;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Highlights.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHighlightsCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ToolsOptions>(configuration.GetSection(ToolsOptions.SectionName));

        services.AddSingleton<ToolLocator>();
        services.AddSingleton<IMediaProbe, MediaProbe>();
        services.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        services.AddSingleton<IProjectStore, ProjectStore>();

        services.AddSingleton<IPipelineStage, ExtractStage>();
        services.AddSingleton<PipelineRunner>();

        return services;
    }
}
