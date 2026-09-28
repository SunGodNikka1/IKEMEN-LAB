using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace IKEMENLab.Core.Mutations;

/// <summary>
/// Serialises read-modify-write edits of one IKEMEN file (select.def, config.ini) inside this process,
/// so two quick roster toggles or a toggle racing a collection activation cannot both read the same
/// original and silently drop one change. Cross-process edits are caught by the expected-hash check
/// in <see cref="ISafeMutationService.ReplaceFile"/>.
/// </summary>
public static class TargetWriteGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable Enter(string path)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        return new Releaser(gate);
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
