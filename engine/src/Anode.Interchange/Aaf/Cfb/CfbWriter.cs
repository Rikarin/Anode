using System.Buffers.Binary;
using System.Text;

namespace Anode.Interchange.Aaf.Cfb;

/// <summary>A storage to write: a name, a class id and its children (storages and streams).</summary>
sealed class CfbStorage(string name, Guid clsid) {
    readonly List<object> children = [];

    public string Name { get; } = CfbNames.Check(name);

    public Guid Clsid { get; } = clsid;

    public IReadOnlyList<object> Children => children;

    public CfbStorage AddStorage(string name, Guid clsid) {
        var storage = new CfbStorage(name, clsid);
        children.Add(storage);
        return storage;
    }

    public void AddStream(string name, byte[] data) =>
        children.Add(new CfbStreamSource(name, data.Length, s => s.Write(data)));

    /// <summary>A stream of <paramref name="length" /> bytes that <paramref name="write" /> produces when the file is written.</summary>
    public void AddStream(string name, long length, Action<Stream> write) =>
        children.Add(new CfbStreamSource(name, length, write));
}

/// <summary>A stream to write: its length is known up front; its data is produced while the file is written.</summary>
sealed record CfbStreamSource(string Name, long Length, Action<Stream> Write) {
    public string Name { get; } = CfbNames.Check(Name);
}

static class CfbNames {
    public static string Check(string name) {
        if (name.Length is 0 or > CfbFormat.MaxNameLength || name.IndexOfAny(['/', '\\', ':', '!']) >= 0) {
            throw new ArgumentException($"Invalid compound file entry name: \"{name}\".", nameof(name));
        }

        return name;
    }
}

/// <summary>
///     Writes a compound file ([MS-CFB]) in one sequential pass: the header, the regular streams (each produced by its
///     source straight into the file, so a large stream is never buffered), the mini stream with the small ones, the mini
///     FAT, the directory, the FAT and the DIFAT. Version 4 (4096-byte sectors, as the AAF SDK writes) by default, or 3
///     (512-byte sectors). Each storage's children form a balanced red-black tree.
/// </summary>
static class CfbWriter {
    public static void Write(string path, CfbStorage root, int version = 4) {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Write(file, root, version);
    }

    public static void Write(Stream output, CfbStorage root, int version = 4) {
        if (version is not (3 or 4)) {
            throw new ArgumentOutOfRangeException(nameof(version), "Compound file version 3 or 4.");
        }

        var entries = Flatten(root);
        var layout = Allocate(entries, version);
        WriteHeader(output, layout);
        foreach (var entry in entries.Where(static e => e.Stream is { Length: >= CfbFormat.MiniStreamCutoff })) {
            WriteSource(output, entry.Stream!, layout.SectorSize);
        }

        WriteMiniStream(output, entries, layout);
        WriteMiniFat(output, entries, layout);
        WriteDirectory(output, entries, layout);
        WriteFat(output, entries, layout);
        WriteDifat(output, layout);
        output.Flush();
    }

    static bool IsMini(Entry entry) => entry.Stream is { Length: > 0 and < CfbFormat.MiniStreamCutoff };

    static long Units(long length, int unit) => (length + unit - 1) / unit;

    /// <summary>
    ///     Gives each stream its first (mini) sector and lays out the rest: the regular streams' sectors, then the mini
    ///     stream's, the mini FAT, the directory, the FAT and the DIFAT.
    /// </summary>
    static Layout Allocate(List<Entry> entries, int version) {
        var sectorSize = version == 4 ? 4096 : 512;
        long next = 0;
        long miniNext = 0;
        foreach (var entry in entries.Where(static e => e.Stream is not null)) {
            var length = entry.Stream!.Length;
            if (version == 3 && length > uint.MaxValue) {
                throw new ArgumentException($"Stream {entry.Name} is too large for a version 3 compound file.");
            }

            if (length == 0) {
                entry.Start = CfbFormat.EndOfChain;
            } else if (length < CfbFormat.MiniStreamCutoff) {
                entry.Start = (uint)miniNext;
                miniNext += Units(length, CfbFormat.MiniSectorSize);
            } else {
                entry.Start = (uint)next;
                next += Units(length, sectorSize);
            }
        }

        var miniStreamSize = miniNext * CfbFormat.MiniSectorSize;
        var miniStreamSectors = Units(miniStreamSize, sectorSize);
        var miniFatSectors = Units(miniNext * 4, sectorSize);
        var directoryStart = next + miniStreamSectors + miniFatSectors;
        var directorySectors = Units((long)entries.Count * CfbFormat.DirectoryEntrySize, sectorSize);
        var (fatSectors, difatSectors) = FatSize(directoryStart + directorySectors, sectorSize / 4);
        var layout = new Layout(
            version,
            sectorSize,
            next,
            miniStreamSize,
            miniStreamSectors,
            miniFatSectors,
            directoryStart,
            directorySectors,
            fatSectors,
            difatSectors
        );
        if (layout.DifatStart + difatSectors > CfbFormat.MaxRegularSector) {
            throw new ArgumentException("The compound file is too large.");
        }

        entries[0].Start = miniStreamSize > 0 ? (uint)next : CfbFormat.EndOfChain;
        entries[0].Size = miniStreamSize;
        return layout;
    }

    /// <summary>The FAT and DIFAT sectors that <paramref name="used" /> sectors need, themselves included.</summary>
    static (long Fat, long Difat) FatSize(long used, int perSector) {
        long fatSectors = 0;
        long difatSectors = 0;
        while (true) {
            var difat = fatSectors > CfbFormat.HeaderDifatCount
                ? Units(fatSectors - CfbFormat.HeaderDifatCount, perSector - 1)
                : 0;
            var fat = Units(used + fatSectors + difat, perSector);
            if (fat == fatSectors && difat == difatSectors) {
                return (fatSectors, difatSectors);
            }

            (fatSectors, difatSectors) = (Math.Max(fat, fatSectors), difat);
        }
    }

    static void WriteMiniStream(Stream output, List<Entry> entries, Layout layout) {
        if (layout.MiniStreamSize == 0) {
            return;
        }

        foreach (var entry in entries.Where(IsMini)) {
            WriteSource(output, entry.Stream!, CfbFormat.MiniSectorSize);
        }

        Pad(output, layout.MiniStreamSize, layout.SectorSize);
    }

    static void WriteMiniFat(Stream output, List<Entry> entries, Layout layout) {
        if (layout.MiniFatSectors == 0) {
            return;
        }

        var table = new uint[layout.MiniFatSectors * layout.PerSector];
        Array.Fill(table, CfbFormat.FreeSector);
        foreach (var entry in entries.Where(IsMini)) {
            Link(table, entry.Start, Units(entry.Stream!.Length, CfbFormat.MiniSectorSize));
        }

        WriteWords(output, table);
    }

    static void WriteDirectory(Stream output, List<Entry> entries, Layout layout) {
        var directory = new byte[layout.DirectorySectors * layout.SectorSize];
        for (var i = 0; i < directory.Length / CfbFormat.DirectoryEntrySize; i++) {
            var raw = directory.AsSpan(i * CfbFormat.DirectoryEntrySize, CfbFormat.DirectoryEntrySize);
            if (i < entries.Count) {
                WriteEntry(raw, entries[i]);
            } else {
                BinaryPrimitives.WriteUInt32LittleEndian(raw[68..], CfbFormat.NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(raw[72..], CfbFormat.NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(raw[76..], CfbFormat.NoStream);
            }
        }

        output.Write(directory);
    }

    static void WriteFat(Stream output, List<Entry> entries, Layout layout) {
        var table = new uint[layout.FatSectors * layout.PerSector];
        Array.Fill(table, CfbFormat.FreeSector);
        foreach (var entry in entries.Where(static e => e.Stream is { Length: >= CfbFormat.MiniStreamCutoff })) {
            Link(table, entry.Start, Units(entry.Stream!.Length, layout.SectorSize));
        }

        Link(table, (uint)layout.MiniStreamStart, layout.MiniStreamSectors);
        Link(table, (uint)layout.MiniFatStart, layout.MiniFatSectors);
        Link(table, (uint)layout.DirectoryStart, layout.DirectorySectors);
        Array.Fill(table, CfbFormat.FatSector, (int)layout.FatStart, (int)layout.FatSectors);
        Array.Fill(table, CfbFormat.DifatSector, (int)layout.DifatStart, (int)layout.DifatSectors);
        WriteWords(output, table);
    }

    /// <summary>The DIFAT beyond the header's 109 entries: each sector's last entry chains to the next.</summary>
    static void WriteDifat(Stream output, Layout layout) {
        if (layout.DifatSectors == 0) {
            return;
        }

        var perSector = layout.PerSector;
        var table = new uint[layout.DifatSectors * perSector];
        Array.Fill(table, CfbFormat.FreeSector);
        for (var f = CfbFormat.HeaderDifatCount; f < layout.FatSectors; f++) {
            var k = f - CfbFormat.HeaderDifatCount;
            table[k / (perSector - 1) * perSector + k % (perSector - 1)] = (uint)(layout.FatStart + f);
        }

        for (var d = 0L; d < layout.DifatSectors; d++) {
            table[d * perSector + perSector - 1] =
                d + 1 < layout.DifatSectors ? (uint)(layout.DifatStart + d + 1) : CfbFormat.EndOfChain;
        }

        WriteWords(output, table);
    }

    static void WriteHeader(Stream output, Layout layout) {
        var header = new byte[layout.SectorSize];
        var span = header.AsSpan();
        CfbFormat.Signature.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[24..], 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)layout.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)(layout.Version == 4 ? 12 : 9));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], layout.Version == 4 ? (uint)layout.DirectorySectors : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], (uint)layout.FatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], (uint)layout.DirectoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], CfbFormat.MiniStreamCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(
            span[60..],
            layout.MiniFatSectors > 0 ? (uint)layout.MiniFatStart : CfbFormat.EndOfChain
        );
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], (uint)layout.MiniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(
            span[68..],
            layout.DifatSectors > 0 ? (uint)layout.DifatStart : CfbFormat.EndOfChain
        );
        BinaryPrimitives.WriteUInt32LittleEndian(span[72..], (uint)layout.DifatSectors);
        for (var i = 0; i < CfbFormat.HeaderDifatCount; i++) {
            BinaryPrimitives.WriteUInt32LittleEndian(
                span[(76 + i * 4)..],
                i < layout.FatSectors ? (uint)(layout.FatStart + i) : CfbFormat.FreeSector
            );
        }

        output.Write(header);
    }

    /// <summary>Where each part of the file goes, in sectors after the header.</summary>
    sealed record Layout(
        int Version,
        int SectorSize,
        long MiniStreamStart,
        long MiniStreamSize,
        long MiniStreamSectors,
        long MiniFatSectors,
        long DirectoryStart,
        long DirectorySectors,
        long FatSectors,
        long DifatSectors
    ) {
        public int PerSector => SectorSize / 4;

        public long MiniFatStart => MiniStreamStart + MiniStreamSectors;

        public long FatStart => DirectoryStart + DirectorySectors;

        public long DifatStart => FatStart + FatSectors;
    }

    static void WriteSource(Stream output, CfbStreamSource source, int unit) {
        var counted = new BoundedStream(output, source.Length, source.Name);
        source.Write(counted);
        if (counted.Written != source.Length) {
            throw new InvalidOperationException(
                $"Stream {source.Name} wrote {counted.Written} bytes, not the {source.Length} it announced."
            );
        }

        Pad(output, source.Length, unit);
    }

    static void Pad(Stream output, long length, int unit) {
        var rest = (int)((unit - length % unit) % unit);
        if (rest > 0) {
            output.Write(new byte[rest]);
        }
    }

    static void Link(uint[] table, uint start, long count) {
        for (var i = 0L; i < count; i++) {
            table[start + i] = i + 1 < count ? (uint)(start + i + 1) : CfbFormat.EndOfChain;
        }
    }

    static void WriteWords(Stream output, uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        output.Write(bytes);
    }

    static void WriteEntry(Span<byte> raw, Entry entry) {
        var name = Encoding.Unicode.GetBytes(entry.Name);
        name.CopyTo(raw);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[64..], (ushort)(name.Length + 2));
        raw[66] = (byte)entry.Type;
        raw[67] = entry.Red ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(raw[68..], entry.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[72..], entry.Right);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[76..], entry.Child);
        if (entry.Type != CfbEntryType.Stream) {
            entry.Clsid.TryWriteBytes(raw.Slice(80, 16));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(raw[116..], entry.Start);
        BinaryPrimitives.WriteUInt64LittleEndian(raw[120..], (ulong)entry.Size);
    }

    /// <summary>Every entry, the root first, each storage's children linked as a balanced red-black tree.</summary>
    static List<Entry> Flatten(CfbStorage root) {
        var entries = new List<Entry> { new("Root Entry", CfbEntryType.Root, root.Clsid, null) };
        var pending = new Queue<(CfbStorage Storage, Entry Entry)>();
        pending.Enqueue((root, entries[0]));
        while (pending.TryDequeue(out var item)) {
            var children = new List<Entry>();
            foreach (var child in item.Storage.Children) {
                var entry = child switch {
                    CfbStorage s => new Entry(s.Name, CfbEntryType.Storage, s.Clsid, null),
                    CfbStreamSource s => new Entry(s.Name, CfbEntryType.Stream, Guid.Empty, s),
                    _ => throw new InvalidOperationException()
                };
                entry.Id = (uint)entries.Count;
                entries.Add(entry);
                children.Add(entry);
                if (child is CfbStorage sub) {
                    pending.Enqueue((sub, entry));
                }
            }

            item.Entry.Child = Siblings(children, item.Storage.Name);
        }

        return entries;
    }

    /// <summary>Links a storage's children as a red-black tree in the format's order; returns its root.</summary>
    static uint Siblings(List<Entry> children, string storage) {
        children.Sort(static (a, b) => CfbFormat.Compare(a.Name, b.Name));
        for (var i = 1; i < children.Count; i++) {
            if (CfbFormat.Compare(children[i - 1].Name, children[i].Name) == 0) {
                throw new ArgumentException($"Two entries named \"{children[i].Name}\" in {storage}.");
            }
        }

        var perfect = (children.Count & (children.Count + 1)) == 0;
        return Tree(children, 0, children.Count, 0, perfect ? -1 : Depth(children.Count));
    }

    /// <summary>The depth of the deepest node of a balanced tree of <paramref name="count" /> nodes (0 for one).</summary>
    static int Depth(int count) {
        var depth = -1;
        while (count > 0) {
            depth++;
            count >>= 1;
        }

        return depth;
    }

    /// <summary>
    ///     Links <paramref name="nodes" /> [lo, hi) as a size-balanced tree: every path to a leaf has the same number of
    ///     black nodes when the nodes at the deepest level of a tree that is not perfect are red.
    /// </summary>
    static uint Tree(List<Entry> nodes, int lo, int hi, int depth, int redDepth) {
        if (lo >= hi) {
            return CfbFormat.NoStream;
        }

        var mid = lo + (hi - lo) / 2;
        var node = nodes[mid];
        node.Red = depth == redDepth;
        node.Left = Tree(nodes, lo, mid, depth + 1, redDepth);
        node.Right = Tree(nodes, mid + 1, hi, depth + 1, redDepth);
        return node.Id;
    }

    sealed class Entry(string name, CfbEntryType type, Guid clsid, CfbStreamSource? stream) {
        public string Name { get; } = name;

        public CfbEntryType Type { get; } = type;

        public Guid Clsid { get; } = clsid;

        public CfbStreamSource? Stream { get; } = stream;

        public uint Id { get; set; }

        public uint Left { get; set; } = CfbFormat.NoStream;

        public uint Right { get; set; } = CfbFormat.NoStream;

        public uint Child { get; set; } = CfbFormat.NoStream;

        public bool Red { get; set; }

        public uint Start { get; set; } = CfbFormat.EndOfChain;

        public long Size { get; set; } = stream?.Length ?? 0;
    }

    /// <summary>Passes a stream source's bytes to the file, refusing more than it announced.</summary>
    sealed class BoundedStream(Stream inner, long limit, string name) : Stream {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written;

        public override long Position {
            get => Written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) {
            if (Written + buffer.Length > limit) {
                throw new InvalidOperationException($"Stream {name} wrote more than the {limit} bytes it announced.");
            }

            inner.Write(buffer);
            Written += buffer.Length;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
