using System.Text;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Mcp;

// ikemenlab-mcp: the Character X-Ray MCP server (stdio). The protocol owns stdout; everything else (logs, anything Core might print) goes to stderr.
var options = McpOptions.Parse(args, out var problem);
if (problem is not null)
{
    Console.Error.WriteLine(problem);
    return 2;
}

var protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
var protocolIn = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
Console.SetOut(Console.Error);
DefFileReader.EnsureEncodingsRegistered();

var app = new McpApp(options, Console.Error);
Console.Error.WriteLine($"[mcp] ikemenlab-mcp {McpApp.Version} · data {app.Context.DataDirectory} · IKEMEN root {app.Context.IkemenRoot ?? "(not set)"}");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await app.Server.RunAsync(protocolIn, protocolOut, stop.Token);

// The client closed the pipe (or Ctrl+C): cancel jobs, stop the engine, delete sandboxes.
var shutdown = await app.ShutdownAsync();
if (!shutdown.Clean) Console.Error.WriteLine("[mcp] shutdown: " + (shutdown.Problem ?? "leftover sandboxes: " + string.Join(", ", shutdown.LeftoverSandboxes)));
return 0;
