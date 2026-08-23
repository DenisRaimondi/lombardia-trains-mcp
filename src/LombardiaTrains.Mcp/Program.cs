using LombardiaTrains.Mcp.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// An MCP server speaking over stdio owns stdout: every byte written there is
// read as protocol. Anything logged to the console has to go to stderr instead,
// otherwise the first log line breaks the session with a parse error that is
// very hard to trace back to its cause.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddHttpClient<ViaggiaTrenoClient>();
builder.Services.AddHttpClient<TrenordClient>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
