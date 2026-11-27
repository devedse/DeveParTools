using DeveParTools.Core.Hashing;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

/// <summary>
/// The fingerprint of a whole file (File packet). For files with unprotected chunks ("PAR inside") two definitions are in
/// use, and both are accepted.
/// </summary>
/// <remarks>
/// <para>The specification says unprotected bytes are hashed as if they were zeros. par3cmdline, the reference
/// implementation, skips them: it hashes only the protected chunks, back to back. For a protected ZIP:</para>
/// <code>
///   file:           [ data ][ footer ][ PAR3 packets ][ footer copy ]
///   specification:  BLAKE3( data ‖ footer ‖ 00 00 … 00 ‖ footer copy )
///   par3cmdline:    BLAKE3( data ‖ footer ‖ footer copy )
/// </code>
/// <para>Files without unprotected chunks give the same value both ways. This hasher computes both forms in one pass,
/// so files from either kind of writer verify. <see cref="Par3Inside"/> writes the par3cmdline form, the only one
/// another program can check today.</para>
/// </remarks>
internal sealed class Par3FileHasher
{
    private static readonly byte[] Zeros = new byte[1 << 16];
    private readonly Blake3Hasher _zeros = new();
    private readonly Blake3Hasher? _skipped;

    public Par3FileHasher(Par3FileEntry file)
    {
        _skipped = file.Chunks.Any(c => !c.Protected) ? new Blake3Hasher() : null;
    }

    public void Protected(ReadOnlySpan<byte> data)
    {
        _zeros.Update(data);
        _skipped?.Update(data);
    }

    public void Unprotected(ulong length)
    {
        for (ulong left = length; left > 0;)
        {
            int n = (int)Math.Min(left, (ulong)Zeros.Length);
            _zeros.Update(Zeros.AsSpan(0, n));
            left -= (ulong)n;
        }
    }

    public bool Matches(Fingerprint expected) => Final(_zeros) == expected || (_skipped != null && Final(_skipped) == expected);

    private static Fingerprint Final(Blake3Hasher hasher)
    {
        Span<byte> h = stackalloc byte[16];
        hasher.Finalize(h);
        return Fingerprint.FromBytes(h);
    }
}
