using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace DeveParTools.Core.IO;

/// <summary>
/// Thread-safe cache of open read-only file handles for positional (pread-style) reads via
/// <see cref="RandomAccess"/>. Positional reads need no shared file position, so many threads can read the same
/// handle concurrently.
/// </summary>
public sealed class FileHandleCache : IDisposable
{
    private readonly ConcurrentDictionary<string, SafeFileHandle> _handles = new(StringComparer.Ordinal);

    public SafeFileHandle Get(string path) => _handles.GetOrAdd(path, static p =>
        File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess));

    /// <summary>
    /// Reads as many bytes as available at <paramref name="offset"/> into <paramref name="destination"/> and
    /// zero-fills the rest. Returns the number of real bytes read.
    /// </summary>
    public int ReadPadded(string path, long offset, Span<byte> destination) => ReadPadded(Get(path), offset, destination);

    public static int ReadPadded(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(handle, destination[total..], offset + total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        destination[total..].Clear();
        return total;
    }

    /// <summary>Close one cached handle (e.g. before the file is replaced).</summary>
    public void Close(string path)
    {
        if (_handles.TryRemove(path, out var h))
        {
            h.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var h in _handles.Values)
        {
            h.Dispose();
        }

        _handles.Clear();
    }
}
