using DeveParTools.Core.Operations;
using DeveParTools.Core.Util;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

/// <summary>
/// Applies the optional file-system metadata of a PAR3 set (UNIX/FAT permission packets, Link packets). Following
/// the specification's security section, only safe changes are made: permission bits are masked (no set-uid/gid,
/// no world-writable), timestamps in the future are clamped, ownership is never changed, and links must stay inside
/// the base directory.
/// </summary>
public static class Par3Metadata
{
    private const long FatEpochTicks = 504911232000000000L; // 1600-01-01 in .NET ticks

    public static void Apply(Par3VerifyResult verify, IParOperationObserver observer)
    {
        foreach (var fv in verify.Files.Where(f => f.TargetPath.Length > 0 && File.Exists(f.TargetPath)))
        {
            ApplyTo(fv.TargetPath, fv.File.Options, isDirectory: false, observer);
        }

        foreach (var d in verify.Set.Directories)
        {
            if (PathSafety.TryResolveUnder(verify.BaseDirectory, d.RelativePath, out var path, out _) && Directory.Exists(path))
            {
                ApplyTo(path, d.Options, isDirectory: true, observer);
            }
        }
    }

    internal static void ApplyTo(string path, IReadOnlyList<Par3Packet> options, bool isDirectory, IParOperationObserver observer)
    {
        var unix = options.OfType<Par3UnixPermissionsPacket>().FirstOrDefault();
        var fat = options.OfType<Par3FatPermissionsPacket>().FirstOrDefault();
        var now = DateTime.UtcNow;
        try
        {
            if (unix != null && !OperatingSystem.IsWindows())
            {
                // Only the rwx bits; never setuid/setgid/sticky, never world-writable.
                var mode = (UnixFileMode)(unix.Mode & 0x1FF & ~0x2);
                File.SetUnixFileMode(path, mode);
            }

            if (unix != null && unix.Mtime != Par3UnixPermissionsPacket.UnsetTime)
            {
                var t = DateTime.UnixEpoch.AddTicks(unix.Mtime / 100);
                SetWriteTime(path, t > now ? now : t, isDirectory);
            }
            else if (fat != null && fat.LastWrite != Par3FatPermissionsPacket.UnsetTime)
            {
                var t = new DateTime((long)fat.LastWrite + FatEpochTicks, DateTimeKind.Utc);
                SetWriteTime(path, t > now ? now : t, isDirectory);
            }

            if (fat != null && OperatingSystem.IsWindows() && !isDirectory)
            {
                var attrs = (FileAttributes)(fat.Attributes & 0x2027);
                File.SetAttributes(path, attrs == 0 ? FileAttributes.Normal : attrs);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            observer.Log(ParLogLevel.Warning, $"Could not apply metadata to '{path}': {e.Message}");
        }
    }

    private static void SetWriteTime(string path, DateTime utc, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.SetLastWriteTimeUtc(path, utc);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, utc);
        }
    }

    /// <summary>Creates symbolic links (hard links are created as symbolic links) that point inside the base directory.</summary>
    public static void CreateLinks(Par3VerifyResult verify, IParOperationObserver observer)
    {
        foreach (var link in verify.Set.Links)
        {
            if (link.Packet.TargetIsAbsolute || link.Packet.TargetPath.Any(p => p == ".." || p == "." || p.Length == 0))
            {
                observer.Log(ParLogLevel.Warning, $"Skipping link '{link.RelativePath}': its target leaves the base directory.");
                continue;
            }

            if (!PathSafety.TryResolveUnder(verify.BaseDirectory, link.RelativePath, out var path, out _)
                || !PathSafety.TryResolveUnder(verify.BaseDirectory, string.Join('/', link.Packet.TargetPath), out var target, out _))
            {
                observer.Log(ParLogLevel.Warning, $"Skipping unsafe link '{link.RelativePath}'.");
                continue;
            }

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.CreateSymbolicLink(path, Path.GetRelativePath(Path.GetDirectoryName(path)!, target));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                observer.Log(ParLogLevel.Warning, $"Could not create link '{link.RelativePath}': {e.Message}");
            }
        }
    }
}
