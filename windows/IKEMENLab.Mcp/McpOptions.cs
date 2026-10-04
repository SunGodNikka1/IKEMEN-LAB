using IKEMENLab.Core.Settings;

namespace IKEMENLab.Mcp;

/// <summary>
/// How the server was started. Every store lives under <see cref="DataDirectory"/> (the app's own data folder by default, the same one the desktop app
/// uses; <c>--data-dir</c> or <c>IKEMENLAB_APPDATA_DIR</c> moves ALL of it, which is how tests and acceptance runs stay away from the user's data).
/// </summary>
public sealed record McpOptions
{
    /// <summary>The IKEMEN installation (read-only; playbacks copy it into disposable sandboxes). Null = the app's configured root.</summary>
    public string? IkemenRoot { get; init; }

    /// <summary>App data (names, saved sequences, experiments, MCP run records, settings.json). Null = <see cref="AppDataPaths.GetAppDataDirectory"/>.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Overrides for the playback setup; unset values come from the app's settings (never written).</summary>
    public string? EnginePath { get; init; }
    public string? EngineDlls { get; init; }
    public string? Dummy { get; init; }
    public string? Stage { get; init; }

    /// <summary>How long a queued job waits for an engine another client holds before it fails with a clear message.</summary>
    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public string ResolvedDataDirectory => Path.GetFullPath(DataDirectory ?? AppDataPaths.GetAppDataDirectory());

    public static McpOptions Parse(string[] args, out string? problem)
    {
        problem = null;
        var o = new McpOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--root": o = o with { IkemenRoot = Next() }; break;
                case "--data-dir": o = o with { DataDirectory = Next() }; break;
                case "--engine": o = o with { EnginePath = Next() }; break;
                case "--engine-dlls": o = o with { EngineDlls = Next() }; break;
                case "--dummy": o = o with { Dummy = Next() }; break;
                case "--stage": o = o with { Stage = Next() }; break;
                case "--queue-timeout-minutes" when int.TryParse(Next(), out var m) && m > 0: o = o with { QueueTimeout = TimeSpan.FromMinutes(m) }; break;
                default:
                    problem = $"Unknown argument '{args[i]}'. Use: ikemenlab-mcp [--root <IKEMEN folder>] [--data-dir <dir>] [--engine <exe>] [--dummy <folder>] [--stage <stages/x.def>]";
                    return o;
            }
        }

        return o;
    }
}
