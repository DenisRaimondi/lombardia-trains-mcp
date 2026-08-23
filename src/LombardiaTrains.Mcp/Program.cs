using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Services;
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
builder.Services.AddHttpClient<SwissTransportClient>();
builder.Services.AddHttpClient<GtfsClient>();

// Transient, not singleton: it depends on the typed HttpClients, which the
// factory registers as transient so it can rotate their handlers. A singleton
// holding them would keep the first handler alive for the life of the process.
builder.Services.AddTransient<ConnectionFinder>();
builder.Services.AddTransient<GtfsPlanner>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
