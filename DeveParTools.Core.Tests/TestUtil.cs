using System.Runtime.InteropServices;

namespace DeveParTools.Core.Tests;

/// <summary>Pins a set of managed arrays and exposes them as a native <c>byte**</c> table (for the region kernels).</summary>
internal sealed unsafe class PinnedArrays : IDisposable
{
    private readonly GCHandle[] _handles;

    public PinnedArrays(IReadOnlyList<byte[]> arrays)
    {
        _handles = arrays.Select(a => GCHandle.Alloc(a, GCHandleType.Pinned)).ToArray();
        Pointers = (byte**)NativeMemory.Alloc((nuint)Math.Max(1, arrays.Count), (nuint)sizeof(byte*));
        for (int i = 0; i < arrays.Count; i++)
        {
            Pointers[i] = (byte*)_handles[i].AddrOfPinnedObject();
        }
    }

    public byte** Pointers { get; private set; }

    public nint[] Addresses => _handles.Select(h => h.AddrOfPinnedObject()).ToArray();

    public void Dispose()
    {
        foreach (var h in _handles)
        {
            h.Free();
        }

        NativeMemory.Free(Pointers);
        Pointers = null;
    }
}

internal static class TestData
{
    public static byte[] RandomBytes(Random rnd, int length)
    {
        var b = new byte[length];
        rnd.NextBytes(b);
        return b;
    }

    /// <summary>A fresh empty directory below the system temp directory; delete it with <see cref="DeleteDirectory"/>.</summary>
    public static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deveparcore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
