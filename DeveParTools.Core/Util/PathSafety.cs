namespace DeveParTools.Core.Util;

/// <summary>Validation of file names stored inside PAR files (which may come from untrusted sources).</summary>
public static class PathSafety
{
    /// <summary>
    /// Converts a stored name ("dir/sub/file.bin", always '/' separated) into a path below <paramref name="baseDirectory"/>.
    /// Rejects absolute paths, drive letters, empty segments and ".." so a malicious PAR file cannot write outside the
    /// base directory.
    /// </summary>
    public static string ResolveUnder(string baseDirectory, string storedName)
    {
        if (!TryResolveUnder(baseDirectory, storedName, out var path, out var reason))
        {
            throw new InvalidDataException($"Unsafe file name '{storedName}': {reason}");
        }

        return path;
    }

    public static bool TryResolveUnder(string baseDirectory, string storedName, out string path, out string reason)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(storedName))
        {
            reason = "empty name";
            return false;
        }

        var normalized = storedName.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            reason = "absolute path";
            return false;
        }

        var parts = normalized.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == "..")
            {
                reason = "invalid path segment";
                return false;
            }

            if (part.IndexOf('\0') >= 0)
            {
                reason = "NUL character";
                return false;
            }
        }

        var full = Path.GetFullPath(Path.Combine([baseDirectory, .. parts]));
        var root = Path.GetFullPath(baseDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            reason = "escapes the base directory";
            return false;
        }

        path = full;
        reason = string.Empty;
        return true;
    }

    /// <summary>Relative '/'-separated name of <paramref name="path"/> below <paramref name="baseDirectory"/>.</summary>
    public static string ToStoredName(string baseDirectory, string path)
    {
        var rel = Path.GetRelativePath(baseDirectory, path);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            throw new ArgumentException($"'{path}' is not below the base directory '{baseDirectory}'.");
        }

        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
