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
    private const long FatEpochTicks = 504911232000000000L; // .NET ticks of 1601-01-01 UTC, the FILETIME epoch

    /// <summary>|t| below this is read as seconds: 10^11 s is in the year 5138, 10^11 ns is 100 s after 1970.</summary>
    private const long SecondsLimit = 100_000_000_000L;

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

    /// <summary>
    /// Applies the permission packets among <paramref name="options"/> to one file or directory: the UNIX mode (not on
    /// Windows), the last-write time (from the UNIX packet, else from the FAT packet) and the FAT attributes (only on
    /// Windows, only for files). Values a creator marked as "not stored" are skipped.
    /// </summary>
    internal static void ApplyTo(string path, IReadOnlyList<Par3Packet> options, bool isDirectory, IParOperationObserver observer)
    {
        var unix = options.OfType<Par3UnixPermissionsPacket>().FirstOrDefault();
        var fat = options.OfType<Par3FatPermissionsPacket>().FirstOrDefault();
        var now = DateTime.UtcNow;
        try
        {
            if (unix != null && SafeUnixMode(unix.Mode) is { } mode && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, mode);
            }

            var mtime = (unix == null ? null : UnixTimeToUtc(unix.Mtime)) ?? (fat == null ? null : FileTimeToUtc(fat.LastWrite));
            if (mtime is { } t)
            {
                SetWriteTime(path, t > now ? now : t, isDirectory);
            }

            if (fat != null && OperatingSystem.IsWindows() && !isDirectory && FatAttributes(fat.Attributes) is { } attributes)
            {
                File.SetAttributes(path, attributes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // ArgumentException: e.g. Windows refuses times before 1601 (a UNIX packet can hold the year 1000).
            observer.Log(ParLogLevel.Warning, $"Could not apply metadata to '{path}': {e.Message}");
        }
    }

    /// <summary>
    /// The mode to set for an i_mode value: only the rwx bits, never set-uid/set-gid/sticky and never
    /// world-writable (spec "Security"). Null when the value is not a 12-bit mode, e.g. par3cmdline's 0xFFFF for
    /// "not stored".
    /// </summary>
    /// <example>0o4777 (set-uid, rwxrwxrwx) → 0o775; 0o640 → 0o640; 0xFFFF → null.</example>
    internal static UnixFileMode? SafeUnixMode(ushort mode) => (mode & 0xF000) != 0 ? null : (UnixFileMode)(mode & 0x1FF & ~0x2);

    /// <summary>
    /// A UNIX Permissions packet time as UTC, or null when unset or out of range. The specification stores
    /// nanoseconds since 1970-01-01 UTC; par3cmdline stores whole seconds in the same field. Both are accepted:
    /// realistic second values are far below 10^11 (the year 5138) and realistic nanosecond values far above it
    /// (10^11 ns is 1970-01-01 00:01:40), so values with |t| &lt; 10^11 are read as seconds.
    /// </summary>
    /// <example>1_700_000_000 (par3cmdline) and 1_700_000_000_000_000_000 (specification) both mean 2023-11-14 22:13:20 UTC.</example>
    internal static DateTime? UnixTimeToUtc(long value)
    {
        if (value == Par3UnixPermissionsPacket.UnsetTime)
        {
            return null;
        }

        long ticks = Math.Abs(value) < SecondsLimit ? value * TimeSpan.TicksPerSecond : value / 100;
        return ticks < -DateTime.UnixEpoch.Ticks ? null : DateTime.UnixEpoch.AddTicks(ticks);
    }

    /// <summary>A FAT Permissions packet time (FILETIME: 100 ns ticks since 1601-01-01 UTC) as UTC, or null when unset or out of range.</summary>
    internal static DateTime? FileTimeToUtc(ulong value)
        => value > (ulong)(DateTime.MaxValue.Ticks - FatEpochTicks) ? null : new DateTime((long)value + FatEpochTicks, DateTimeKind.Utc);

    /// <summary>
    /// The attributes to set for a FAT Permissions packet. Null when bits outside the five defined ones are set, which
    /// the specification forbids and par3cmdline uses (0xFFFF) for "not stored".
    /// </summary>
    internal static FileAttributes? FatAttributes(ushort attributes)
        => (attributes & ~Par3FatPermissionsPacket.KnownAttributes) != 0 ? null
            : attributes == 0 ? FileAttributes.Normal : (FileAttributes)attributes;

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
