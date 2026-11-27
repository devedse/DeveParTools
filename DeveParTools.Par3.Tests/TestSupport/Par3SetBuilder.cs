using System.Buffers.Binary;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3.Tests.TestSupport;

/// <summary>One chunk of a file for <see cref="Par3SetBuilder.File"/>.</summary>
internal abstract record ChunkSpec(ulong Length);

/// <summary>Bytes that are not protected (the file hash treats them as zeros).</summary>
internal sealed record UnprotectedChunk(ulong Length) : ChunkSpec(Length);

/// <summary>
/// Protected bytes. Full blocks go to new input blocks unless <see cref="FirstBlock"/> is given (then the blocks are
/// shared with whatever else uses them); a tail of 40+ bytes goes to a new block at offset 0 unless
/// <see cref="TailAt"/> is given.
/// </summary>
internal sealed record ProtectedChunk(ulong Length, ulong? FirstBlock = null, (ulong Block, ulong Offset)? TailAt = null) : ChunkSpec(Length);

/// <summary>
/// Builds PAR3 sets packet by packet with <see cref="Par3PacketWriter"/>, independently of <see cref="Par3Creator"/>:
/// for layouts our creator never writes (unprotected chunks, shared and overlapping chunks, Sparse and Explicit
/// matrices, links, incremental sets, Data packets without checksums, ...). Recovery data is computed symbol by symbol
/// with plain field multiplications, not with the library's region code.
/// </summary>
internal sealed class Par3SetBuilder
{
    private readonly Dictionary<ulong, byte[]> _blocks = new();
    private readonly List<byte[]> _metadata = new();
    private readonly List<MatrixSpec> _matrices = new();
    private readonly byte[] _startBody;
    private ulong _next;
    private byte[]? _root;

    public Par3SetBuilder(ulong blockSize, byte[] generator, ulong unique = 1, InputSetId parent = default, Fingerprint parentRoot = default, ulong firstNewBlock = 0, bool specStartLayout = false)
    {
        BlockSize = blockSize;
        Generator = generator;
        _next = firstNewBlock;
        if (specStartLayout)
        {
            // The specification's layout: the unique number is part of the body.
            _startBody = new byte[41 + generator.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(_startBody, unique);
            Par3PacketWriter.StartBody(parent, parentRoot, blockSize, generator).CopyTo(_startBody, 8);
            SetId = InputSetId.FromBytes(Blake3.Hash(_startBody));
        }
        else
        {
            _startBody = Par3PacketWriter.StartBody(parent, parentRoot, blockSize, generator);
            SetId = Par3PacketWriter.ComputeSetId(unique, _startBody);
        }
    }

    public ulong BlockSize { get; }

    public byte[] Generator { get; }

    public InputSetId SetId { get; }

    public IReadOnlyDictionary<ulong, byte[]> Blocks => _blocks;

    public Fingerprint RootChecksum { get; private set; }

    public ulong LowestUnused { get; private set; }

    public byte[] StartPacket => Par3PacketWriter.Build(SetId, Par3PacketTypes.Start, _startBody);

    public string CreatorText { get; init; } = "Par3SetBuilder (tests)";

    /// <summary>Puts bytes into input block <paramref name="index"/> at <paramref name="offset"/> (the rest of the block stays zero).</summary>
    public void SetBlock(ulong index, ReadOnlySpan<byte> data, ulong offset = 0)
    {
        if (!_blocks.TryGetValue(index, out var block))
        {
            _blocks[index] = block = new byte[BlockSize];
        }

        data.CopyTo(block.AsSpan((int)offset));
        _next = Math.Max(_next, index + 1);
    }

    /// <summary>Adds a prebuilt packet (options, links, unknown types, ...) to the index file and returns its checksum.</summary>
    public Fingerprint Add(byte[] packet)
    {
        _metadata.Add(packet);
        return Par3PacketWriter.ChecksumOf(packet);
    }

    /// <summary>Adds a File packet for <paramref name="content"/>; by default one protected chunk.</summary>
    public Fingerprint File(string name, byte[] content, IReadOnlyList<ChunkSpec>? chunks = null, IReadOnlyList<Fingerprint>? options = null)
    {
        chunks ??= [new ProtectedChunk((ulong)content.Length)];
        if (chunks.Sum(c => (long)c.Length) != content.Length)
        {
            throw new ArgumentException("Chunks must cover the content exactly.");
        }

        var described = new List<Par3Chunk>();
        var hashed = (byte[])content.Clone();
        int o = 0;
        foreach (var spec in chunks)
        {
            int len = (int)spec.Length;
            if (spec is not ProtectedChunk p)
            {
                described.Add(new Par3Chunk { Protected = false, Length = spec.Length });
                Array.Clear(hashed, o, len);
                o += len;
                continue;
            }

            ulong full = spec.Length / BlockSize;
            ulong first = p.FirstBlock ?? _next;
            for (ulong k = 0; k < full; k++)
            {
                SetBlock(first + k, content.AsSpan(o + (int)(k * BlockSize), (int)BlockSize));
            }

            var tail = content.AsSpan(o + (int)(full * BlockSize), (int)(spec.Length % BlockSize));
            var chunk = new Par3Chunk { Length = spec.Length, FirstBlock = full > 0 ? first : 0 };
            if (tail.Length >= Par3Chunk.InlineLimit)
            {
                var (tb, to) = p.TailAt ?? (_next, 0UL);
                SetBlock(tb, tail, to);
                chunk = chunk with { TailCrc = Crc64Iso.Compute(tail[..Par3Chunk.InlineLimit]), TailHash = Fingerprint.Compute(tail), TailBlock = tb, TailOffset = to };
            }
            else if (tail.Length > 0)
            {
                chunk = chunk with { InlineTail = tail.ToArray() };
            }

            described.Add(chunk);
            o += len;
        }

        var packet = Par3PacketWriter.File(SetId, name, Crc64Iso.Compute(content.AsSpan(0, Math.Min(16384, content.Length))), Fingerprint.Compute(hashed), options ?? [], described, BlockSize);
        return Add(packet);
    }

    public Fingerprint Directory(string name, IReadOnlyList<Fingerprint> children, IReadOnlyList<Fingerprint>? options = null)
        => Add(Par3PacketWriter.Directory(SetId, name, options ?? [], children));

    /// <summary>Sets the Root packet. Call after all files and before adding recovery data.</summary>
    public Fingerprint Root(IReadOnlyList<Fingerprint> children, IReadOnlyList<Fingerprint>? options = null, ulong? lowestUnused = null)
    {
        LowestUnused = lowestUnused ?? _next;
        _root = Par3PacketWriter.Root(SetId, LowestUnused, false, options ?? [], children);
        RootChecksum = Par3PacketWriter.ChecksumOf(_root);
        return RootChecksum;
    }

    public Fingerprint Cauchy(IReadOnlyList<ulong> recoveryIndices, Par3CauchyConvention convention = Par3CauchyConvention.Par3cmdline, ulong first = 0, ulong lastPlus1 = 0)
    {
        var packet = Par3PacketWriter.Cauchy(SetId, first, lastPlus1, (ulong)recoveryIndices.Count);
        _matrices.Add(new CauchySpec(packet, recoveryIndices, first, lastPlus1, convention));
        return Par3PacketWriter.ChecksumOf(packet);
    }

    public Fingerprint Sparse(IReadOnlyList<ulong> recoveryIndices, ulong maxRecovery, ulong nonZero, ulong seed, ulong first = 0, ulong lastPlus1 = 0)
    {
        var packet = Par3PacketWriter.Sparse(SetId, first, lastPlus1, maxRecovery, nonZero, seed);
        _matrices.Add(new SparseSpec(packet, recoveryIndices, new Par3SparseMatrixPacket(first, lastPlus1, maxRecovery, nonZero, seed)));
        return Par3PacketWriter.ChecksumOf(packet);
    }

    /// <summary>An Explicit matrix row (recovery index 0) with the given factors (little-endian field elements).</summary>
    public Fingerprint Explicit(IReadOnlyList<(ulong Index, byte[] Value)> entries, IReadOnlyList<ulong>? recoveryIndices = null)
    {
        var packet = Par3PacketWriter.Explicit(SetId, entries);
        _matrices.Add(new ExplicitSpec(packet, recoveryIndices ?? [0UL], entries));
        return Par3PacketWriter.ChecksumOf(packet);
    }

    /// <summary>External Data packets for the blocks <paramref name="include"/> accepts (default all), one per run of blocks.</summary>
    public List<byte[]> ExternalData(Func<ulong, bool>? include = null)
    {
        var result = new List<byte[]>();
        var run = new List<BlockChecksum>();
        ulong runStart = 0;
        for (ulong i = 0; i <= LowestUnused; i++)
        {
            bool take = i < LowestUnused && (include?.Invoke(i) ?? true) && _blocks.ContainsKey(i);
            if (take)
            {
                if (run.Count == 0)
                {
                    runStart = i;
                }

                var b = _blocks[i];
                run.Add(new BlockChecksum(Crc64Iso.Compute(b), Fingerprint.Compute(b)));
            }
            else if (run.Count > 0)
            {
                result.Add(Par3PacketWriter.ExternalData(SetId, runStart, run.ToList()));
                run.Clear();
            }
        }

        return result;
    }

    /// <summary>A Data packet holding input block <paramref name="index"/> (optionally only its first <paramref name="length"/> bytes).</summary>
    public byte[] DataPacket(ulong index, int? length = null)
    {
        var data = _blocks[index].AsSpan(0, length ?? (int)BlockSize);
        var body = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(body, index);
        data.CopyTo(body.AsSpan(8));
        return Par3PacketWriter.Build(SetId, Par3PacketTypes.Data, body);
    }

    /// <summary>Recovery Data packets of every matrix, computed over input blocks [0, <see cref="LowestUnused"/>).</summary>
    public List<byte[]> RecoveryPackets()
    {
        var root = RootChecksum;
        return Par3Fields.With(Generator, new RecoveryComputation(this)).Select(r =>
        {
            var body = new byte[40 + r.Data.Length];
            root.WriteTo(body);
            r.Matrix.WriteTo(body.AsSpan(16));
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(32), r.Index);
            r.Data.CopyTo(body.AsSpan(40));
            return Par3PacketWriter.Build(SetId, Par3PacketTypes.RecoveryData, body);
        }).ToList();
    }

    /// <summary>Start, matrices, External Data, options/files/directories, Root and Creator: everything but bulk data.</summary>
    public List<byte[]> IndexPackets(bool externalData = true)
    {
        var list = new List<byte[]> { StartPacket };
        list.AddRange(_matrices.Select(m => m.Packet));
        if (externalData)
        {
            list.AddRange(ExternalData());
        }

        list.AddRange(_metadata);
        list.Add(_root ?? throw new InvalidOperationException("Call Root first."));
        list.Add(Par3PacketWriter.Text(SetId, Par3PacketTypes.Creator, CreatorText));
        return list;
    }

    /// <summary>Writes the index packets plus all recovery packets to one file.</summary>
    public string WriteAll(string path) => Write(path, [.. IndexPackets(), .. RecoveryPackets()]);

    public static string Write(string path, IEnumerable<byte[]> packets)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var p in packets)
        {
            fs.Write(p);
        }

        return path;
    }

    private abstract record MatrixSpec(byte[] Packet, IReadOnlyList<ulong> Indices)
    {
        public Fingerprint Checksum => Par3PacketWriter.ChecksumOf(Packet);
    }

    private sealed record CauchySpec(byte[] Packet, IReadOnlyList<ulong> Indices, ulong First, ulong LastPlus1, Par3CauchyConvention Convention) : MatrixSpec(Packet, Indices);

    private sealed record SparseSpec(byte[] Packet, IReadOnlyList<ulong> Indices, Par3SparseMatrixPacket Parsed) : MatrixSpec(Packet, Indices);

    private sealed record ExplicitSpec(byte[] Packet, IReadOnlyList<ulong> Indices, IReadOnlyList<(ulong Index, byte[] Value)> Entries) : MatrixSpec(Packet, Indices);

    private sealed class RecoveryComputation(Par3SetBuilder b) : IFieldAction<List<(Fingerprint Matrix, ulong Index, byte[] Data)>>
    {
        public List<(Fingerprint Matrix, ulong Index, byte[] Data)> Run<T>(IGaloisField<T> field) where T : IEquatable<T>
        {
            var result = new List<(Fingerprint, ulong, byte[])>();
            int sym = field.SymbolSize;
            foreach (var m in b._matrices)
            {
                var sparseRows = m is SparseSpec s ? Par3Sparse.Generate(field, s.Parsed, b.LowestUnused) : null;
                foreach (var r in m.Indices)
                {
                    var data = new byte[b.BlockSize];
                    for (ulong i = 0; i < b.LowestUnused; i++)
                    {
                        var c = m switch
                        {
                            CauchySpec cs => Par3Cauchy.InRange(cs.First, cs.LastPlus1, i) ? Par3Cauchy.Coefficient(field, r, i, cs.Convention) : field.Zero,
                            SparseSpec ss => Par3Cauchy.InRange(ss.Parsed.First, ss.Parsed.LastPlus1, i) && r < ss.Parsed.MaxRecovery && i - ss.Parsed.First < (ulong)sparseRows!.Count
                                ? sparseRows[(int)(i - ss.Parsed.First)][r] : field.Zero,
                            ExplicitSpec es => r == 0 && es.Entries.FirstOrDefault(e => e.Index == i).Value is { } v ? field.Read(v) : field.Zero,
                            _ => throw new InvalidOperationException(),
                        };
                        if (field.IsZero(c) || !b._blocks.TryGetValue(i, out var block))
                        {
                            continue;
                        }

                        for (int j = 0; j + sym <= data.Length; j += sym)
                        {
                            var sum = field.Add(field.Read(data.AsSpan(j, sym)), field.Mul(c, field.Read(block.AsSpan(j, sym))));
                            field.Write(sum, data.AsSpan(j, sym));
                        }
                    }

                    result.Add((m.Checksum, r, data));
                }
            }

            return result;
        }
    }
}
