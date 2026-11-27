namespace DeveParTools.Par3.Tests.TestSupport;

/// <summary>A fresh directory under the system temp directory, deleted (with everything in it) on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deveparttools-par3-tests", Guid.NewGuid().ToString("N"));
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

    public byte[] Read(string relative) => File.ReadAllBytes(this[relative]);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
            {
                if (!OperatingSystem.IsWindows() && File.Exists(f) && !new FileInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp directory must not fail a test.
        }
    }
}
