using System.Buffers.Binary;
using System.Text;

namespace Anode.Interchange.Aaf.Cfb;

/// <summary>A storage or stream of a compound file, as read.</summary>
sealed class CfbEntry {
    readonly List<CfbEntry> children = [];
    Dictionary<string, CfbEntry>? byName;

    internal CfbEntry(int id, string name, CfbEntryType type, Guid clsid, uint start, long size) {
        Id = id;
        Name = name;
        Type = type;
        Clsid = clsid;
        Start = start;
        Size = size;
    }

    public int Id { get; }

    public string Name { get; }

    public CfbEntryType Type { get; }

    public Guid Clsid { get; }

    public uint Start { get; }

    public long Size { get; }

    public bool IsStream => Type == CfbEntryType.Stream;

    /// <summary>The entries of a storage, in the format's sibling order.</summary>
    public IReadOnlyList<CfbEntry> Children => children;

    /// <summary>The child named <paramref name="name" /> (the format's case-insensitive match), or null.</summary>
    public CfbEntry? Child(string name) {
        byName ??= children.DistinctBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static c => c.Name, StringComparer.OrdinalIgnoreCase);
        return byName.GetValueOrDefault(name);
    }

    internal void Add(CfbEntry child) => children.Add(child);

    internal void Sort() => children.Sort(static (a, b) => CfbFormat.Compare(a.Name, b.Name));
}

/// <summary>
///     Reads a compound file ([MS-CFB], version 3 with 512-byte sectors or 4 with 4096-byte ones): the FAT (through the
///     DIFAT), the directory, the mini FAT and the mini stream. Streams are read in place, sector by sector, so a large
///     one is never held in memory. A file that breaks the format (a bad header, a chain out of range or in a loop, a
///     directory tree with a cycle) throws <see cref="FormatException" />.
/// </summary>
sealed class CfbReader : IDisposable {
    readonly Stream file;
    readonly bool ownsFile;
    readonly uint[] fat;
    readonly uint[] miniFat;
    readonly long sectorCount;
    readonly uint[] miniStreamChain;

    CfbReader(Stream file, bool ownsFile) {
        this.file = file;
        this.ownsFile = ownsFile;
        Span<byte> header = stackalloc byte[CfbFormat.HeaderSize];
        if (file.Length < CfbFormat.HeaderSize || ReadAt(0, header) < CfbFormat.HeaderSize) {
            throw new FormatException("Not a compound file: too short.");
        }

        if (!header[..8].SequenceEqual(CfbFormat.Signature)) {
            throw new FormatException("Not a compound file: no signature.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
        var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        // [MS-CFB] pairs version 3 with 512-byte sectors and 4 with 4096-byte ones; pyaaf2 labels 512-byte files 4, so
        // either size is read under either version.
        if (byteOrder != 0xFFFE || major is not (3 or 4) || sectorShift is not (9 or 12) || miniShift != 6) {
            throw new FormatException($"Unsupported compound file: version {major}, sector shift {sectorShift}.");
        }

        Version = major;
        SectorSize = 1 << sectorShift;
        sectorCount = (file.Length + SectorSize - 1) / SectorSize - 1;
        var fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
        var firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
        var cutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[56..]);
        var firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
        var firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(header[68..]);
        var difatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[72..]);
        if (cutoff != CfbFormat.MiniStreamCutoff || fatSectors > sectorCount || difatSectors > sectorCount) {
            throw new FormatException("Corrupt compound file header.");
        }

        fat = ReadFat(header, fatSectors, firstDifat, difatSectors);
        var entries = ReadDirectory(firstDirectory);
        miniFat = firstMiniFat == CfbFormat.EndOfChain ? [] : Words(ReadChainData(firstMiniFat));
        Root = entries[0];
        if (Root.Type != CfbEntryType.Root) {
            throw new FormatException("Corrupt compound file: the first directory entry is not the root.");
        }

        miniStreamChain = Root.Size > 0 ? [..Chain(Root.Start, fat, sectorCount)] : [];
        if ((long)miniStreamChain.Length * SectorSize < Root.Size) {
            throw new FormatException("Corrupt compound file: the mini stream is shorter than its size.");
        }
    }

    /// <summary>3 or 4.</summary>
    public int Version { get; }

    public int SectorSize { get; }

    public CfbEntry Root { get; }

    public void Dispose() {
        if (ownsFile) {
            file.Dispose();
        }
    }

    public static CfbReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), true);

    /// <summary>Reads from <paramref name="stream" /> (seekable), which stays open.</summary>
    public static CfbReader Open(Stream stream) => new(stream, false);

    /// <summary>The whole of a (small) stream.</summary>
    public byte[] ReadAll(CfbEntry entry) {
        if (entry.Size > int.MaxValue) {
            throw new FormatException($"Stream {entry.Name} is too large to read whole.");
        }

        using var stream = OpenStream(entry);
        var data = new byte[entry.Size];
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>A read-only, seekable view of a stream entry's data.</summary>
    public Stream OpenStream(CfbEntry entry) {
        if (!entry.IsStream) {
            throw new ArgumentException($"{entry.Name} is not a stream.", nameof(entry));
        }

        if (entry.Size == 0) {
            return new MemoryStream([], false);
        }

        if (entry.Size < CfbFormat.MiniStreamCutoff) {
            var chain = Chain(
                entry.Start,
                miniFat,
                (Root.Size + CfbFormat.MiniSectorSize - 1) / CfbFormat.MiniSectorSize
            );
            var data = new byte[entry.Size];
            for (var i = 0; i < data.Length; i += CfbFormat.MiniSectorSize) {
                var index = i / CfbFormat.MiniSectorSize;
                if (index >= chain.Count) {
                    throw new FormatException($"Corrupt compound file: stream {entry.Name} ends early.");
                }

                ReadMiniSector(chain[index], data.AsSpan(i, Math.Min(CfbFormat.MiniSectorSize, data.Length - i)));
            }

            return new MemoryStream(data, false);
        }

        var sectors = Chain(entry.Start, fat, sectorCount);
        if ((long)sectors.Count * SectorSize < entry.Size) {
            throw new FormatException($"Corrupt compound file: stream {entry.Name} ends early.");
        }

        return new SectorStream(this, [..sectors], entry.Size);
    }

    static uint[] Words(byte[] data) {
        var words = new uint[data.Length / 4];
        for (var i = 0; i < words.Length; i++) {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        }

        return words;
    }

    /// <summary>The sectors of the chain from <paramref name="start" />; a loop or a sector out of range throws.</summary>
    static List<uint> Chain(uint start, uint[] table, long limit) {
        var chain = new List<uint>();
        var sector = start;
        while (sector != CfbFormat.EndOfChain) {
            if (sector > CfbFormat.MaxRegularSector || sector >= limit || sector >= table.Length) {
                throw new FormatException($"Corrupt compound file: sector {sector:X} out of range.");
            }

            if (chain.Count > table.Length) {
                throw new FormatException("Corrupt compound file: a sector chain loops.");
            }

            chain.Add(sector);
            sector = table[sector];
        }

        return chain;
    }

    uint[] ReadFat(ReadOnlySpan<byte> header, uint fatSectors, uint firstDifat, uint difatSectors) {
        var locations = new List<uint>();
        for (var i = 0; i < CfbFormat.HeaderDifatCount && locations.Count < fatSectors; i++) {
            locations.Add(BinaryPrimitives.ReadUInt32LittleEndian(header[(76 + i * 4)..]));
        }

        var perSector = SectorSize / 4;
        var buffer = new byte[SectorSize];
        var difat = firstDifat;
        for (var d = 0; d < difatSectors && locations.Count < fatSectors; d++) {
            ReadSector(difat, buffer);
            for (var i = 0; i < perSector - 1 && locations.Count < fatSectors; i++) {
                locations.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 4)));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan((perSector - 1) * 4));
        }

        if (locations.Count < fatSectors) {
            throw new FormatException("Corrupt compound file: the DIFAT is shorter than the FAT.");
        }

        var table = new uint[(long)fatSectors * perSector];
        for (var f = 0; f < locations.Count; f++) {
            ReadSector(locations[f], buffer);
            for (var i = 0; i < perSector; i++) {
                table[f * perSector + i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 4));
            }
        }

        return table;
    }

    byte[] ReadChainData(uint start) {
        var chain = Chain(start, fat, sectorCount);
        var data = new byte[(long)chain.Count * SectorSize];
        for (var i = 0; i < chain.Count; i++) {
            ReadSector(chain[i], data.AsSpan(i * SectorSize, SectorSize));
        }

        return data;
    }

    List<CfbEntry> ReadDirectory(uint first) {
        var data = ReadChainData(first);
        var count = data.Length / CfbFormat.DirectoryEntrySize;
        var entries = new List<CfbEntry>(count);
        var links = new List<(uint Left, uint Right, uint Child)>(count);
        for (var i = 0; i < count; i++) {
            var (entry, link) = ParseEntry(
                i,
                data.AsSpan(i * CfbFormat.DirectoryEntrySize, CfbFormat.DirectoryEntrySize)
            );
            entries.Add(entry);
            links.Add(link);
        }

        if (entries.Count == 0) {
            throw new FormatException("Corrupt compound file: no directory.");
        }

        LinkTree(entries, links);
        foreach (var storage in entries.Where(static e => e.Type is CfbEntryType.Storage or CfbEntryType.Root)) {
            storage.Sort();
        }

        return entries;
    }

    (CfbEntry Entry, (uint Left, uint Right, uint Child) Links) ParseEntry(int id, ReadOnlySpan<byte> raw) {
        var type = (CfbEntryType)raw[66];
        var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(raw[64..]);
        if (type != CfbEntryType.Unused && (nameBytes is < 2 or > 64 || nameBytes % 2 != 0)) {
            throw new FormatException("Corrupt compound file: a directory entry's name length is invalid.");
        }

        var name = type == CfbEntryType.Unused ? string.Empty : Encoding.Unicode.GetString(raw[..(nameBytes - 2)]);
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw[120..]);
        if (Version == 3) {
            size &= 0xFFFFFFFF;
        }

        if (size < 0) {
            throw new FormatException("Corrupt compound file: a stream size is negative.");
        }

        var entry = new CfbEntry(
            id,
            name,
            type,
            new(raw.Slice(80, 16)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[116..]),
            size
        );
        return (entry, (BinaryPrimitives.ReadUInt32LittleEndian(raw[68..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[72..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[76..])));
    }

    /// <summary>Gives each storage its children by walking the sibling trees; an entry reached twice is a cycle.</summary>
    static void LinkTree(List<CfbEntry> entries, List<(uint Left, uint Right, uint Child)> links) {
        var visited = new bool[entries.Count];
        visited[0] = true;
        var pending = new Stack<CfbEntry>();
        pending.Push(entries[0]);
        while (pending.Count > 0) {
            var storage = pending.Pop();
            var nodes = new Stack<uint>();
            nodes.Push(links[storage.Id].Child);
            while (nodes.TryPop(out var id)) {
                if (id == CfbFormat.NoStream) {
                    continue;
                }

                if (id >= entries.Count
                    || visited[id]
                    || entries[(int)id].Type is CfbEntryType.Unused or CfbEntryType.Root) {
                    throw new FormatException("Corrupt compound file: the directory tree is invalid.");
                }

                visited[id] = true;
                var entry = entries[(int)id];
                storage.Add(entry);
                nodes.Push(links[(int)id].Left);
                nodes.Push(links[(int)id].Right);
                if (entry.Type == CfbEntryType.Storage) {
                    pending.Push(entry);
                }
            }
        }
    }

    void ReadSector(uint sector, Span<byte> into) {
        if (sector > CfbFormat.MaxRegularSector || sector >= sectorCount) {
            throw new FormatException($"Corrupt compound file: sector {sector:X} out of range.");
        }

        var read = ReadAt((sector + 1L) * SectorSize, into);
        into[read..].Clear();
    }

    void ReadMiniSector(uint miniSector, Span<byte> into) {
        var offset = (long)miniSector * CfbFormat.MiniSectorSize;
        var index = offset / SectorSize;
        if (index >= miniStreamChain.Length || offset + into.Length > Root.Size) {
            throw new FormatException($"Corrupt compound file: mini sector {miniSector:X} out of range.");
        }

        var position = (miniStreamChain[index] + 1L) * SectorSize + offset % SectorSize;
        var read = ReadAt(position, into);
        into[read..].Clear();
    }

    int ReadAt(long position, Span<byte> into) {
        file.Position = position;
        var total = 0;
        while (total < into.Length) {
            var n = file.Read(into[total..]);
            if (n == 0) {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>A regular stream's data, read through its sector chain.</summary>
    sealed class SectorStream(CfbReader reader, uint[] sectors, long length) : Stream {
        long position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position {
            get => position;
            set => position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            var total = 0;
            var size = reader.SectorSize;
            while (total < buffer.Length && position < length) {
                var index = (int)(position / size);
                var within = (int)(position % size);
                var n = (int)Math.Min(Math.Min(size - within, buffer.Length - total), length - position);
                var read = reader.ReadAt((sectors[index] + 1L) * size + within, buffer.Slice(total, n));
                if (read < n) {
                    throw new FormatException("Corrupt compound file: a stream runs past the end of the file.");
                }

                total += n;
                position += n;
            }

            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            Position = origin switch {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                _ => length + offset
            };

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
