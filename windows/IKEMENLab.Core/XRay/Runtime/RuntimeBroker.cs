using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IKEMENLab.Core.XRay.Runtime;

/// <summary>Who holds the playback engine: the client (the desktop app, the CLI, an MCP session), what it is running, its process and since when.</summary>
public sealed record RuntimeHolder(string Client, string Job, int ProcessId, DateTime StartedUtc)
{
    public static RuntimeHolder Now(string client, string job) => new(client, job, Environment.ProcessId, DateTime.UtcNow);

    /// <summary>"IKEMEN Lab app — Play Ability · Kung Fu Palm (since 10:42, process 1234)".</summary>
    public string Describe() =>
        $"{Client} — {Job} (since {StartedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}, process {ProcessId.ToString(CultureInfo.InvariantCulture)})";
}

/// <summary>The playback engine is in use by someone else. Nothing was launched.</summary>
public sealed class RuntimeBusyException(RuntimeHolder? holder)
    : InvalidOperationException("The playback engine is busy: " + (holder?.Describe() ?? "another IKEMEN Lab process is running a playback") +
                                ". Only one playback runs at a time; wait for it to finish or cancel it where it was started.")
{
    public RuntimeHolder? Holder { get; } = holder;
}

/// <summary>An exclusive hold on the playback engine. Disposing it (or the process ending, however it ends) releases it.</summary>
public sealed class RuntimeLease : IDisposable
{
    private FileStream? _stream;

    internal RuntimeLease(FileStream stream, RuntimeHolder holder)
    {
        _stream = stream;
        Holder = holder;
    }

    public RuntimeHolder Holder { get; }

    public void Dispose()
    {
        var s = Interlocked.Exchange(ref _stream, null);
        if (s is null) return;
        try { s.SetLength(0); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* releasing is what matters */ }
        s.Dispose();
    }
}

/// <summary>
/// The one-engine-at-a-time broker shared by the desktop app, the CLI and the MCP server. Only one playback engine can usefully run at a time, so whoever
/// owns a playback job (the app's <see cref="Playback.PlaybackSession"/>, a CLI runtime command, the MCP job queue) holds a lease for the whole job and a
/// second client is refused with a clear <see cref="RuntimeBusyException"/> instead of racing it.
/// <para>
/// The lease is an exclusive handle on a lock file (shared read, so others can see who holds it). The operating system drops the handle when the process
/// ends, so a crashed holder never leaves the engine locked; the file's content is only a description of the holder.
/// The machine-wide lock lives in <c>%LOCALAPPDATA%\IKEMEN Lab\runtime\engine.lock</c> and deliberately ignores the app-data override used to isolate QA
/// data: an isolated test run still must not launch an engine while the user's app is playing. <see cref="LockPathVariable"/> moves it for tests.
/// </para>
/// </summary>
public sealed class RuntimeBroker
{
    public const string LockPathVariable = "IKEMENLAB_RUNTIME_LOCK";

    private static readonly Lazy<RuntimeBroker> SharedBroker = new(() => new RuntimeBroker(DefaultLockPath));

    public RuntimeBroker(string lockPath) => LockPath = Path.GetFullPath(lockPath);

    /// <summary>The broker every real playback uses.</summary>
    public static RuntimeBroker Shared => SharedBroker.Value;

    public static string DefaultLockPath =>
        Environment.GetEnvironmentVariable(LockPathVariable) is { Length: > 0 } custom
            ? custom.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Settings.AppDataPaths.AppFolderName, "runtime", "engine.lock");

    public string LockPath { get; }

    /// <summary>Takes the engine, or returns null with whoever holds it (null when the holder could not be read).</summary>
    public RuntimeLease? TryAcquire(RuntimeHolder holder, out RuntimeHolder? busyWith)
    {
        busyWith = null;
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        FileStream? stream = null;
        // A few quick retries: another process's CurrentHolder() check holds the file for an instant and must not read as "busy".
        for (var attempt = 0; stream is null; attempt++)
        {
            try
            {
                stream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 3) { Thread.Sleep(15); continue; }
                busyWith = CurrentHolder();
                return null;
            }
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema = "ikemenlab.runtime-lock/1", client = holder.Client, job = holder.Job, processId = holder.ProcessId,
                startedUtc = holder.StartedUtc.ToString("o", CultureInfo.InvariantCulture)
            }));
            stream.SetLength(0);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            // The description is informational; the exclusive handle is the lease.
        }

        return new RuntimeLease(stream, holder);
    }

    /// <summary>Takes the engine or throws <see cref="RuntimeBusyException"/>.</summary>
    public RuntimeLease Acquire(RuntimeHolder holder) =>
        TryAcquire(holder, out var busy) ?? throw new RuntimeBusyException(busy);

    /// <summary>Whoever holds the engine right now, or null when it is free. Never takes it.</summary>
    public RuntimeHolder? CurrentHolder()
    {
        if (!File.Exists(LockPath)) return null;
        try
        {
            // Free: nobody holds an exclusive handle, so this read-write open succeeds (and is closed at once).
            using (new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held: read the holder's description.
        }

        try
        {
            using var fs = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return Unknown();
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "?" : "?";
            var pid = r.TryGetProperty("processId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            var started = DateTime.TryParse(S("startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.UtcNow;
            return new RuntimeHolder(S("client"), S("job"), pid, started);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Unknown();
        }

        static RuntimeHolder Unknown() => new("another IKEMEN Lab process", "a playback", 0, DateTime.UtcNow);
    }
}
