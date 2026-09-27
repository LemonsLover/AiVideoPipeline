using System.CommandLine;
using System.Text;
using Highlights.Cli.Commands;
using Highlights.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = Encoding.UTF8;

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables("HIGHLIGHTS_");

// Logs go to stderr so they don't interfere with Spectre progress output.
builder.Logging
    .AddConfiguration(builder.Configuration.GetSection("Logging"))
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddHighlightsCore(builder.Configuration);

using var host = builder.Build();

var root = new RootCommand("Finds funny moments in gameplay recordings and builds a highlights video.")
{
    NewCommand.Create(host.Services),
    TracksCommand.Create(host.Services),
    ExtractCommand.Create(host.Services),
    TranscribeCommand.Create(host.Services),
    SignalsCommand.Create(host.Services),
    AnalyzeCommand.Create(host.Services),
    PostprocessCommand.Create(host.Services),
    ReviewCommand.Create(host.Services),
};

return await root.Parse(args).InvokeAsync();
