namespace DeveParTools.Par2.Tests.TestSupport;

/// <summary>A fresh directory under the system temp directory, deleted (with everything in it) on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deveparttools-par2-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Full path of a '/'-separated relative name.</summary>
    public string this[string relative] => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Writes a file (creating its directories) and returns its full path.</summary>
    public string Write(string relative, byte[] data)
    {
        var p = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>Writes <paramref name="size"/> deterministic pseudo-random bytes and returns the full path.</summary>
    public string WriteRandom(string relative, int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return Write(relative, data);
    }

    public byte[] Read(string relative) => File.ReadAllBytes(this[relative]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp directory must not fail a test.
        }
    }
}
