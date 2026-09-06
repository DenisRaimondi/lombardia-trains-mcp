using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace LombardiaTrains.Tests;

/// <summary>
/// Starts the server as a client would and speaks JSON-RPC to it over stdio.
///
/// The other tests call the tools directly, which skips everything between a
/// method and a working server: dependency injection, the attributes that make
/// a method a tool at all, the generated schemas, and the rule that stdout
/// belongs to the protocol. Each of those can break on its own while every unit
/// test still passes and the server is unusable.
///
/// The last one is the reason this exists. A single log line written to stdout
/// corrupts the stream, and the failure surfaces in someone else's client as a
/// parse error with nothing to trace it back to.
/// </summary>
public class ProtocolTests(ITestOutputHelper output) : IDisposable
{
    private readonly Process _server = Start();

    private static string ServerDll()
    {
        var here = AppContext.BaseDirectory;                       // .../bin/<cfg>/net10.0/
        var config = new DirectoryInfo(here).Parent!.Name;         // Debug or Release
        var root = new DirectoryInfo(here).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(root, "src", "LombardiaTrains.Mcp",
                               "bin", config, "net10.0", "LombardiaTrains.Mcp.dll");

        Assert.True(File.Exists(dll), $"server not built at {dll}");
        return dll;
    }

    private static Process Start(string? workingDirectory = null)
    {
        var info = new ProcessStartInfo("dotnet", $"\"{ServerDll()}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;

        return Process.Start(info)!;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    private void Send(object message) =>
        _server.StandardInput.WriteLine(JsonSerializer.Serialize(message));

    /// <summary>
    /// Reads until the reply with this id arrives. Notifications and anything
    /// else on the stream are skipped rather than tripping the test, but a line
    /// that is not JSON at all is a corrupted stream and fails immediately —
    /// that is the thing most worth catching here.
    /// </summary>
    private JsonElement Await(int id, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var line = _server.StandardOutput.ReadLine();
            if (line is null) break;
            if (line.Length == 0) continue;

            Assert.True(line.StartsWith('{'),
                $"stdout carried something that is not a JSON-RPC message, which breaks " +
                $"every client: {line}");

            var message = JsonDocument.Parse(line).RootElement;
            if (message.TryGetProperty("id", out var got) && got.GetInt32() == id)
                return message.Clone();
        }

        throw new TimeoutException($"no reply to request {id}");
    }

    private JsonElement Handshake()
    {
        Send(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "battery", version = "1" }
            }
        });

        var reply = Await(1);
        Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        return reply;
    }

    [Fact]
    public void The_server_completes_a_handshake()
    {
        var result = Handshake().GetProperty("result");
        output.WriteLine(result.ToString());

        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("protocolVersion").GetString()));
        var info = result.GetProperty("serverInfo");
        Assert.Equal("lombardia-trains", info.GetProperty("name").GetString());

        // The assembly version would answer here too, as "0.1.0.0". It is not
        // the version anyone can install.
        var version = info.GetProperty("version").GetString();
        Assert.Equal(3, version!.Split('.').Length);
    }

    [Fact]
    public void Every_tool_is_advertised_with_a_description_and_a_schema()
    {
        Handshake();
        Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });

        var tools = Await(2).GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToList();
        output.WriteLine(string.Join(", ", names));

        string[] expected =
        [
            "now", "search_station", "get_departures", "get_arrivals",
            "find_connection", "find_journey", "get_train"
        ];
        Assert.Equal(expected.OrderBy(n => n), names.OrderBy(n => n));

        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();

            // A tool with no description is one the model has to guess the
            // purpose of, and it will guess wrong on the ones that matter.
            var description = tool.GetProperty("description").GetString();
            Assert.False(string.IsNullOrWhiteSpace(description), $"{name} has no description");
            Assert.True(description!.Length > 40, $"{name}'s description says too little");

            Assert.Equal("object",
                tool.GetProperty("inputSchema").GetProperty("type").GetString());
        }
    }

    [Fact]
    public void A_tool_call_returns_content_over_the_wire()
    {
        Handshake();
        Send(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name = "now", arguments = new { } }
        });

        var reply = Await(3);
        Assert.False(reply.TryGetProperty("error", out _), reply.ToString());

        var text = reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        output.WriteLine(text);
        Assert.Contains("Europe/Rome", text);
    }

    [Fact]
    public void An_unknown_tool_is_an_error_and_not_a_crash()
    {
        Handshake();
        Send(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new { name = "book_me_a_seat", arguments = new { } }
        });

        var reply = Await(4);
        output.WriteLine(reply.ToString());

        // Either shape is legitimate: a protocol error, or a result flagged as
        // an error. What must not happen is the process dying and taking the
        // session with it.
        var handled = reply.TryGetProperty("error", out _) ||
                      reply.GetProperty("result").TryGetProperty("isError", out _);
        Assert.True(handled, "an unknown tool was neither refused nor reported");
        Assert.False(_server.HasExited, "the server exited on an unknown tool name");
    }

    /// <summary>
    /// The host takes the working directory as its content root and watches it,
    /// recursively, for configuration changes. A client starts this server from
    /// wherever it happens to be — a home directory, say — and the server then
    /// spends its time on every file event under that tree: seen at 145% CPU
    /// and 11 GB with no tool ever called. The content root has to be the
    /// directory the server is installed in, whatever the client's is.
    /// </summary>
    [Fact]
    public async Task The_content_root_is_the_install_directory_and_not_where_the_client_ran_it()
    {
        var elsewhere = Directory.CreateTempSubdirectory("lombardia-trains-cwd-").FullName;
        var server = Start(workingDirectory: elsewhere);
        try
        {
            var reported = await ReportedContentRoot(server);
            output.WriteLine($"content root: {reported}");

            var installed = Path.GetDirectoryName(ServerDll())!;
            Assert.Equal(Normalize(installed), Normalize(reported), ignoreCase: true);
        }
        finally
        {
            Kill(server);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    /// <summary>
    /// The hosting lifetime logs its content root on stderr at startup; that is
    /// the only place the running server states it.
    /// </summary>
    private static async Task<string> ReportedContentRoot(Process server)
    {
        const string prefix = "Content root path:";
        var deadline = TimeSpan.FromSeconds(60);

        while (await server.StandardError.ReadLineAsync().WaitAsync(deadline) is { } line)
        {
            var at = line.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0) return line[(at + prefix.Length)..].Trim();
        }

        throw new InvalidOperationException("the server never reported its content root");
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public void Dispose()
    {
        Kill(_server);
        GC.SuppressFinalize(this);
    }
}
