using LombardiaTrains.Mcp.Clients;
using LombardiaTrains.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using System.Reflection;

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
builder.Services.AddTransient<LiveCheck>();

// Named on purpose. Left alone, a server introduces itself with its assembly
// name and its four-part assembly version — "LombardiaTrains.Mcp 0.1.0.0" —
// which is what the user sees in the client's list of connected servers, and
// what they have to recognise again when something goes wrong.
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "lombardia-trains",
            Version = ThisAssembly.Version
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

internal static class ThisAssembly
{
    /// <summary>
    /// The package version, taken from the informational version the build
    /// stamps in. AssemblyVersion is the wrong one: it is padded to four parts
    /// and reports 0.1.0.0 where the package says 0.1.0.
    /// </summary>
    public static string Version { get; } =
        typeof(ThisAssembly).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
