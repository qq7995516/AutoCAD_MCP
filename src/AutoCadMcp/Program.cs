using AutoCadMcp.Cad;
using AutoCadMcp.Com;
using AutoCadMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP stdio servers must not write logs to stdout (reserved for JSON-RPC).
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.Configure<AutoCadOptions>(_ =>
{
    var fromEnv = AutoCadOptions.FromEnvironment();
    _.AcadExePath = fromEnv.AcadExePath;
    _.LaunchTimeoutSeconds = fromEnv.LaunchTimeoutSeconds;
});
builder.Services.AddSingleton<StaDispatcher>();
builder.Services.AddSingleton<AutoCadSession>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AutoCadTools>();

var host = builder.Build();
await host.RunAsync();
