using System.Buffers.Binary;
using System.Security.Cryptography;
using DeveParTools.Par2.Packets;

namespace DeveParTools.Par2.Tests.TestSupport;

/// <summary>
/// Byte-level editing of .par2 files: split an intact file into its packets, change, drop or add packets, and
/// write it back with valid packet MD5s. Used to craft sets that a correct creator never writes (unsafe names,
/// missing or inconsistent critical packets, tampered recovery data).
/// </summary>
internal static class PacketSurgery
{
    public static List<byte[]> Split(byte[] file)
    {
        var packets = new List<byte[]>();
        for (int pos = 0; pos < file.Length;)
        {
            int len = (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(pos + 8));
            packets.Add(file.AsSpan(pos, len).ToArray());
            pos += len;
        }

        return packets;
    }

    public static byte[] Join(IEnumerable<byte[]> packets) => packets.SelectMany(p => p).ToArray();

    public static bool Is(byte[] packet, byte[] type) => packet.AsSpan(48, 16).SequenceEqual(type);

    public static byte[] Body(byte[] packet) => packet.AsSpan(Par2PacketTypes.HeaderSize).ToArray();

    public static Md5Hash SetId(byte[] packet) => Md5Hash.FromBytes(packet.AsSpan(32, 16));

    /// <summary>Recomputes the packet MD5 (bytes 16..31) over set ID, type and body.</summary>
    public static byte[] Reseal(byte[] packet)
    {
        MD5.HashData(packet.AsSpan(32), packet.AsSpan(16, 16));
        return packet;
    }

    /// <summary>Same set ID and type, new body (zero-padded to a multiple of 4).</summary>
    public static byte[] WithBody(byte[] packet, byte[] body) => Par2PacketWriter.Build(SetId(packet), packet.AsSpan(48, 16), body);

    /// <summary>Replaces every packet of a file by the packets <paramref name="edit"/> returns for it (none = drop).</summary>
    public static void Rewrite(string path, Func<byte[], IEnumerable<byte[]>> edit)
        => File.WriteAllBytes(path, Join(Split(File.ReadAllBytes(path)).SelectMany(edit)));

    /// <summary>Applies <paramref name="edit"/> to every file of a created set.</summary>
    public static void RewriteAll(Par2CreateResult created, Func<byte[], IEnumerable<byte[]>> edit)
    {
        foreach (var f in created.WrittenFiles)
        {
            Rewrite(f, edit);
        }
    }

    /// <summary>Drops all packets of the given type from every file of a set.</summary>
    public static void DropAll(Par2CreateResult created, byte[] type) => RewriteAll(created, p => Is(p, type) ? [] : [p]);
}
