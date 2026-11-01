using Anode.Interchange.Aaf.Cfb;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Anode.Interchange.Aaf;

/// <summary>
///     AAF's structured storage (the AAF SDK's "OM" layer, as pyaaf2 implements it): each object is a storage whose
///     class id is the object's class, holding a <c>properties</c> stream (byte order <c>0x4C</c>, version 32, then
///     (pid, stored form, size) entries and their data), a child storage per strong reference, an <c>index</c> stream
///     and a storage per element for strong reference vectors and sets, and a stream per stream property. Weak
///     references store the referenced object's unique key, the key's pid, and an index into the root's
///     <c>referenced properties</c> table of pid paths to the property that holds the targets.
/// </summary>
static class AafStoredForm {
    public const ushort Data = 0x82;
    public const ushort DataStream = 0x42;
    public const ushort StrongReference = 0x22;
    public const ushort StrongReferenceVector = 0x32;
    public const ushort StrongReferenceSet = 0x3A;
    public const ushort WeakReference = 0x02;
    public const ushort WeakReferenceVector = 0x12;
    public const ushort WeakReferenceSet = 0x1A;
    public const byte ByteOrderLittle = 0x4C;
    public const byte PropertyVersion = 32;

    /// <summary>The storage name of a property: its name squeezed to fit, a dash and its pid in hex (as the SDK does).</summary>
    public static string Mangle(string name, ushort pid, int size) {
        var hex = pid.ToString("x", CultureInfo.InvariantCulture);
        var max = size - hex.Length - 2;
        return Squeeze(name, max) + "-" + hex;
    }

    static string Squeeze(string name, int size) {
        if (name.Length <= size) {
            return name;
        }

        var half = size / 2;
        var builder = new StringBuilder(size);
        for (var i = 0; i < size; i++) {
            builder.Append(
                i < half ? name[i]
                : i == half ? '-'
                : name[name.Length - (size - i)]
            );
        }

        return builder.ToString();
    }
}

/// <summary>An AAF object to write: its class and its properties in the order they are stored.</summary>
sealed class AafObject(Guid classId) {
    readonly List<(ushort Pid, AafValue Value)> properties = [];

    public Guid ClassId { get; } = classId;

    public IReadOnlyList<(ushort Pid, AafValue Value)> Properties => properties;

    public AafObject Data(ushort pid, byte[] data) => Add(pid, new AafValue.Data(data));

    public AafObject Strong(ushort pid, AafObject target) => Add(pid, new AafValue.Strong(target));

    public AafObject Vector(ushort pid, IEnumerable<AafObject> items) => Add(pid, new AafValue.Vector([..items]));

    /// <summary>A strong reference set: each element with its unique key (the value of its <paramref name="keyPid" />).</summary>
    public AafObject Set(ushort pid, ushort keyPid, IEnumerable<(byte[] Key, AafObject Item)> items) =>
        Add(pid, new AafValue.Set(keyPid, [..items]));

    public AafObject Weak(ushort pid, AafWeakTarget target, byte[] key) => Add(pid, new AafValue.Weak(target, key));

    public AafObject WeakVector(ushort pid, AafWeakTarget target, IEnumerable<byte[]> keys) =>
        Add(pid, new AafValue.WeakArray(AafStoredForm.WeakReferenceVector, target, [..keys]));

    public AafObject WeakSet(ushort pid, AafWeakTarget target, IEnumerable<byte[]> keys) =>
        Add(pid, new AafValue.WeakArray(AafStoredForm.WeakReferenceSet, target, [..keys]));

    public AafObject Stream(ushort pid, long length, Action<Stream> write) =>
        Add(pid, new AafValue.Stream(length, write));

    AafObject Add(ushort pid, AafValue value) {
        if (properties.Exists(p => p.Pid == pid)) {
            throw new InvalidOperationException($"Property {pid:X4} set twice.");
        }

        properties.Add((pid, value));
        return this;
    }
}

/// <summary>The property holding a weak reference's targets: its pid path from the root, and the targets' key pid.</summary>
sealed record AafWeakTarget(ushort[] Path, ushort KeyPid) {
    public string PathKey => string.Join('/', Path);
}

abstract record AafValue {
    public sealed record Data(byte[] Bytes) : AafValue;

    public sealed record Strong(AafObject Target) : AafValue;

    public sealed record Vector(List<AafObject> Items) : AafValue;

    public sealed record Set(ushort KeyPid, List<(byte[] Key, AafObject Item)> Items) : AafValue;

    public sealed record Weak(AafWeakTarget Target, byte[] Key) : AafValue;

    public sealed record WeakArray(ushort Form, AafWeakTarget Target, List<byte[]> Keys) : AafValue;

    public sealed record Stream(long Length, Action<System.IO.Stream> Write) : AafValue;
}

/// <summary>Lays AAF objects out as compound file storages, from the root object down.</summary>
sealed class AafStorageWriter {
    readonly List<AafWeakTarget> table = [];

    /// <summary>The root storage for the <c>Root</c> object <paramref name="root" />, with the referenced properties.</summary>
    public static CfbStorage Build(AafObject root) {
        var writer = new AafStorageWriter();
        var storage = new CfbStorage("Root Entry", root.ClassId);
        writer.Fill(storage, root);
        writer.WriteTable(storage);
        return storage;
    }

    void Fill(CfbStorage storage, AafObject item) {
        var entries = new List<(ushort Pid, ushort Form, byte[] Data)>();
        foreach (var (pid, value) in item.Properties) {
            var name = AafMetaModel.PropertyName(pid);
            entries.Add(
                value switch {
                    AafValue.Data d => (pid, AafStoredForm.Data, d.Bytes),
                    AafValue.Strong s => (pid, AafStoredForm.StrongReference, FillStrong(storage, name, pid, s)),
                    AafValue.Vector v => (pid, AafStoredForm.StrongReferenceVector, FillVector(storage, name, pid, v)),
                    AafValue.Set s => (pid, AafStoredForm.StrongReferenceSet, FillSet(storage, name, pid, s)),
                    AafValue.Weak w => (pid, AafStoredForm.WeakReference, WeakData(w)),
                    AafValue.WeakArray w => (pid, w.Form, FillWeakArray(storage, name, pid, w)),
                    AafValue.Stream s => (pid, AafStoredForm.DataStream, FillStream(storage, name, pid, s)),
                    _ => throw new InvalidOperationException($"Property {pid:X4} has no stored form.")
                }
            );
        }

        storage.AddStream("properties", PropertiesStream(entries));
    }

    byte[] FillStrong(CfbStorage storage, string name, ushort pid, AafValue.Strong value) {
        var child = AafStoredForm.Mangle(name, pid, 32);
        Fill(storage.AddStorage(child, value.Target.ClassId), value.Target);
        return AafEncoding.String(child);
    }

    byte[] FillVector(CfbStorage storage, string name, ushort pid, AafValue.Vector value) {
        var index = AafStoredForm.Mangle(name, pid, 22);
        using var data = new MemoryStream();
        data.Write(AafEncoding.UInt32((uint)value.Items.Count));
        data.Write(AafEncoding.UInt32((uint)value.Items.Count));
        data.Write(AafEncoding.UInt32(0xFFFFFFFF));
        for (var i = 0; i < value.Items.Count; i++) {
            data.Write(AafEncoding.UInt32((uint)i));
            Fill(storage.AddStorage(Element(index, i), value.Items[i].ClassId), value.Items[i]);
        }

        storage.AddStream(index + " index", data.ToArray());
        return AafEncoding.String(index);
    }

    byte[] FillSet(CfbStorage storage, string name, ushort pid, AafValue.Set value) {
        var index = AafStoredForm.Mangle(name, pid, 22);
        var keySize = value.Items.Count > 0 ? value.Items[0].Key.Length : 16;
        if (value.Items.Exists(i => i.Key.Length != keySize)) {
            throw new InvalidOperationException("A set's keys differ in size.");
        }

        using var data = new MemoryStream();
        data.Write(AafEncoding.UInt32((uint)value.Items.Count));
        data.Write(AafEncoding.UInt32((uint)value.Items.Count));
        data.Write(AafEncoding.UInt32(0xFFFFFFFF));
        data.Write(AafEncoding.UInt16(value.KeyPid));
        data.WriteByte((byte)keySize);
        for (var i = 0; i < value.Items.Count; i++) {
            data.Write(AafEncoding.UInt32((uint)i));
            data.Write(AafEncoding.UInt32(1));
            data.Write(value.Items[i].Key);
            Fill(storage.AddStorage(Element(index, i), value.Items[i].Item.ClassId), value.Items[i].Item);
        }

        storage.AddStream(index + " index", data.ToArray());
        return AafEncoding.String(index);
    }

    byte[] WeakData(AafValue.Weak value) {
        var data = new byte[5 + value.Key.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, IndexOf(value.Target));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), value.Target.KeyPid);
        data[4] = (byte)value.Key.Length;
        value.Key.CopyTo(data, 5);
        return data;
    }

    byte[] FillWeakArray(CfbStorage storage, string name, ushort pid, AafValue.WeakArray value) {
        var index = AafStoredForm.Mangle(name, pid, 22);
        using var data = new MemoryStream();
        data.Write(AafEncoding.UInt32((uint)value.Keys.Count));
        data.Write(AafEncoding.UInt16(IndexOf(value.Target)));
        data.Write(AafEncoding.UInt16(value.Target.KeyPid));
        data.WriteByte((byte)(value.Keys.Count > 0 ? value.Keys[0].Length : 16));
        foreach (var key in value.Keys) {
            data.Write(key);
        }

        storage.AddStream(index + " index", data.ToArray());
        return AafEncoding.String(index);
    }

    static byte[] FillStream(CfbStorage storage, string name, ushort pid, AafValue.Stream value) {
        var stream = AafStoredForm.Mangle(name, pid, 32);
        storage.AddStream(stream, value.Length, value.Write);
        return [0x55, ..AafEncoding.String(stream)];
    }

    static byte[] PropertiesStream(List<(ushort Pid, ushort Form, byte[] Data)> entries) {
        using var properties = new MemoryStream();
        properties.WriteByte(AafStoredForm.ByteOrderLittle);
        properties.WriteByte(AafStoredForm.PropertyVersion);
        properties.Write(AafEncoding.UInt16((ushort)entries.Count));
        foreach (var (pid, form, data) in entries) {
            if (data.Length > ushort.MaxValue) {
                throw new InvalidOperationException($"Property {pid:X4} is too large.");
            }

            properties.Write(AafEncoding.UInt16(pid));
            properties.Write(AafEncoding.UInt16(form));
            properties.Write(AafEncoding.UInt16((ushort)data.Length));
        }

        foreach (var (_, _, data) in entries) {
            properties.Write(data);
        }

        return properties.ToArray();
    }

    static string Element(string index, int key) => $"{index}{{{key:x}}}";

    ushort IndexOf(AafWeakTarget target) {
        var at = table.FindIndex(t => t.PathKey == target.PathKey);
        if (at < 0) {
            at = table.Count;
            table.Add(target);
        }

        return (ushort)at;
    }

    void WriteTable(CfbStorage root) {
        using var data = new MemoryStream();
        data.WriteByte(AafStoredForm.ByteOrderLittle);
        data.Write(AafEncoding.UInt16((ushort)table.Count));
        data.Write(AafEncoding.UInt32((uint)table.Sum(static t => t.Path.Length + 1)));
        foreach (var target in table) {
            foreach (var pid in target.Path) {
                data.Write(AafEncoding.UInt16(pid));
            }

            data.Write(AafEncoding.UInt16(0));
        }

        root.AddStream("referenced properties", data.ToArray());
    }
}

/// <summary>An AAF object as read: its class, its properties' stored forms and data, and where it lives.</summary>
sealed class AafStored {
    readonly CfbReader file;
    readonly CfbEntry storage;
    readonly Dictionary<ushort, (ushort Form, byte[] Data)> properties = [];

    AafStored(CfbReader file, CfbEntry storage) {
        this.file = file;
        this.storage = storage;
        var stream = storage.Child("properties");
        if (stream is null || !stream.IsStream) {
            return;
        }

        var data = file.ReadAll(stream);
        if (data.Length < 4 || data[0] != AafStoredForm.ByteOrderLittle) {
            throw new FormatException($"AAF object {storage.Name}: unsupported property stream.");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var offset = 4 + 6 * count;
        if (offset > data.Length) {
            throw new FormatException($"AAF object {storage.Name}: truncated property stream.");
        }

        for (var i = 0; i < count; i++) {
            var pid = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4 + 6 * i));
            var form = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6 + 6 * i));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8 + 6 * i));
            if (offset + size > data.Length) {
                throw new FormatException($"AAF object {storage.Name}: truncated property {pid:X4}.");
            }

            properties[pid] = (form, data.AsSpan(offset, size).ToArray());
            offset += size;
        }
    }

    public Guid ClassId => storage.Clsid;

    public string Path => storage.Name;

    public static AafStored Load(CfbReader file, CfbEntry storage) => new(file, storage);

    public bool Has(ushort pid) => properties.ContainsKey(pid);

    public byte[]? Data(ushort pid) => properties.TryGetValue(pid, out var p) ? p.Data : null;

    public string? String(ushort pid) => Data(pid) is { } d ? AafEncoding.ReadString(d) : null;

    public long? Int64(ushort pid) =>
        Data(pid) is { Length: >= 8 } d ? BinaryPrimitives.ReadInt64LittleEndian(d) : null;

    public uint? UInt32(ushort pid) =>
        Data(pid) is { Length: >= 4 } d ? BinaryPrimitives.ReadUInt32LittleEndian(d) : null;

    public Guid? Auid(ushort pid) => Data(pid) is { Length: >= 16 } d ? new Guid(d.AsSpan(0, 16)) : null;

    public (int Numerator, int Denominator)? Rational(ushort pid) =>
        Data(pid) is { Length: >= 8 } d
            ? (BinaryPrimitives.ReadInt32LittleEndian(d), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)))
            : null;

    /// <summary>The object a strong reference names, or null.</summary>
    public AafStored? Strong(ushort pid) {
        if (!properties.TryGetValue(pid, out var p)) {
            return null;
        }

        Expect(pid, p.Form, AafStoredForm.StrongReference);
        var child = storage.Child(AafEncoding.ReadString(p.Data));
        return child is { IsStream: false }
            ? new AafStored(file, child)
            : throw new FormatException($"AAF object {storage.Name}: missing object for property {pid:X4}.");
    }

    /// <summary>The elements of a strong reference vector or set, in stored order (empty when absent).</summary>
    public IReadOnlyList<AafStored> Elements(ushort pid) {
        if (!properties.TryGetValue(pid, out var p)) {
            return [];
        }

        if (p.Form is not (AafStoredForm.StrongReferenceVector or AafStoredForm.StrongReferenceSet)) {
            throw new FormatException($"AAF object {storage.Name}: property {pid:X4} is not a strong reference array.");
        }

        var index = AafEncoding.ReadString(p.Data);
        var stream = storage.Child(index + " index");
        if (stream is not { IsStream: true }) {
            throw new FormatException($"AAF object {storage.Name}: no index for property {pid:X4}.");
        }

        var keys = LocalKeys(file.ReadAll(stream), p.Form == AafStoredForm.StrongReferenceVector)
            ?? throw new FormatException($"AAF object {storage.Name}: truncated index for property {pid:X4}.");
        var items = new List<AafStored>(keys.Count);
        foreach (var key in keys) {
            var child = storage.Child($"{index}{{{key:x}}}");
            if (child is not { IsStream: false }) {
                throw new FormatException($"AAF object {storage.Name}: missing element {key:x} of property {pid:X4}.");
            }

            items.Add(new(file, child));
        }

        return items;
    }

    /// <summary>
    ///     The local keys of a vector's index (count, next free key, last free key, then the keys) or a set's (the same,
    ///     the key pid and size, then local key, reference count and unique key per element); null when truncated.
    /// </summary>
    static List<uint>? LocalKeys(byte[] data, bool vector) {
        if (data.Length < (vector ? 12 : 15)) {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var (start, stride) = vector ? (12, 4) : (15, 8 + data[14]);
        if (start + (long)stride * count > data.Length) {
            return null;
        }

        var keys = new List<uint>((int)count);
        for (var i = 0; i < count; i++) {
            keys.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + stride * i)));
        }

        return keys;
    }

    /// <summary>The key a weak reference stores (an AUID or a MobID), or null.</summary>
    public byte[]? WeakKey(ushort pid) {
        if (!properties.TryGetValue(pid, out var p)) {
            return null;
        }

        Expect(pid, p.Form, AafStoredForm.WeakReference);
        if (p.Data.Length < 5 || p.Data.Length < 5 + p.Data[4]) {
            throw new FormatException($"AAF object {storage.Name}: truncated weak reference {pid:X4}.");
        }

        return [..p.Data.AsSpan(5, p.Data[4])];
    }

    /// <summary>A stream property's data, or null.</summary>
    public Stream? OpenStream(ushort pid) {
        if (!properties.TryGetValue(pid, out var p)) {
            return null;
        }

        Expect(pid, p.Form, AafStoredForm.DataStream);
        var child = storage.Child(AafEncoding.ReadString([..p.Data.AsSpan(1)]));
        return child is { IsStream: true }
            ? file.OpenStream(child)
            : throw new FormatException($"AAF object {storage.Name}: missing stream for property {pid:X4}.");
    }

    void Expect(ushort pid, ushort form, ushort expected) {
        if (form != expected) {
            throw new FormatException($"AAF object {storage.Name}: property {pid:X4} has stored form {form:X2}.");
        }
    }
}

/// <summary>The little-endian encodings of AAF's property types.</summary>
static class AafEncoding {
    /// <summary>The 12-byte SMPTE UMID label of a MobID (basic UMID, material number by UUID).</summary>
    static ReadOnlySpan<byte> UmidLabel => [0x06, 0x0A, 0x2B, 0x34, 0x01, 0x01, 0x01, 0x05, 0x01, 0x01, 0x0F, 0x20];

    public static byte[] String(string value) => [..Encoding.Unicode.GetBytes(value), 0, 0];

    public static string ReadString(byte[] data) {
        var text = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    public static byte[] UInt8(byte value) => [value];

    public static byte[] Bool(bool value) => [value ? (byte)1 : (byte)0];

    public static byte[] UInt16(ushort value) {
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        return data;
    }

    public static byte[] Int16(short value) {
        var data = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(data, value);
        return data;
    }

    public static byte[] UInt32(uint value) {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return data;
    }

    public static byte[] Int32(int value) {
        var data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, value);
        return data;
    }

    public static byte[] Int64(long value) {
        var data = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(data, value);
        return data;
    }

    /// <summary>An AUID as stored: the first three fields little-endian (as <see cref="Guid.ToByteArray()" />).</summary>
    public static byte[] Auid(Guid value) => value.ToByteArray();

    public static byte[] Rational(int numerator, int denominator) => [..Int32(numerator), ..Int32(denominator)];

    /// <summary>A TimeStamp: year, month, day, hour, minute, second and hundredths.</summary>
    public static byte[] TimeStamp(DateTimeOffset value) {
        var utc = value.UtcDateTime;
        return [
            ..UInt16((ushort)utc.Year), (byte)utc.Month, (byte)utc.Day, (byte)utc.Hour, (byte)utc.Minute,
            (byte)utc.Second, (byte)(utc.Millisecond / 10)
        ];
    }

    /// <summary>An Indirect value: byte order, the value's type AUID and its data.</summary>
    public static byte[] Indirect(Guid type, byte[] value) => [ByteOrderMark, ..Auid(type), ..value];

    public static byte[] Auids(IEnumerable<Guid> values) => [..values.SelectMany(static v => v.ToByteArray())];

    public static byte[] Strings(IEnumerable<string> values) => [..values.SelectMany(String)];

    public static byte[] Int64s(IEnumerable<long> values) => [..values.SelectMany(Int64)];

    /// <summary>A MobID: the UMID label, length 0x13, instance 0, and a material number.</summary>
    public static byte[] MobId(Guid material) => [..UmidLabel, 0x13, 0, 0, 0, ..material.ToByteArray()];

    public static string MobKey(byte[] mobId) => Convert.ToHexString(mobId);

    const byte ByteOrderMark = AafStoredForm.ByteOrderLittle;
}
