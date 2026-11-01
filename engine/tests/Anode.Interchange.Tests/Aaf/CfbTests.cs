using Anode.Interchange.Aaf;
using Anode.Interchange.Aaf.Cfb;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Anode.Interchange.Tests.Aaf;

/// <summary>The compound file writer and reader ([MS-CFB]): round trips in both versions, and corrupt files refused.</summary>
public sealed class CfbTests {
    static readonly Guid Class = new("0d010101-0101-2f00-060e-2b3402060101");

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void An_empty_file_round_trips(int version) {
        var file = Round(new("Root Entry", Class), version, out var bytes);
        Assert.Empty(file.Root.Children);
        Assert.Equal(Class, file.Root.Clsid);
        Assert.Equal(version, file.Version);
        Assert.Equal(version == 4 ? 4096 * 3 : 512 * 3, bytes.Length);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 63)]
    [InlineData(3, 64)]
    [InlineData(3, 4095)]
    [InlineData(3, 4096)]
    [InlineData(3, 4097)]
    [InlineData(4, 0)]
    [InlineData(4, 100)]
    [InlineData(4, 4095)]
    [InlineData(4, 4096)]
    [InlineData(4, 12_289)]
    public void Streams_on_both_sides_of_the_mini_stream_cutoff_round_trip(int version, int size) {
        var root = new CfbStorage("Root Entry", Class);
        var data = Data(size, 1);
        root.AddStream("data", data);
        root.AddStream("small", Data(10, 2));
        var nested = root.AddStorage("child", Guid.NewGuid());
        nested.AddStream("data", Data(size, 3));
        var file = Round(root, version, out _);
        Assert.Equal(data, file.ReadAll(file.Root.Child("data")!));
        Assert.Equal(Data(10, 2), file.ReadAll(file.Root.Child("small")!));
        var child = file.Root.Child("child")!;
        Assert.Equal(nested.Clsid, child.Clsid);
        Assert.Equal(Data(size, 3), file.ReadAll(child.Child("data")!));
    }

    [Theory]
    [InlineData(3, 10_000_000)]
    [InlineData(4, 10_000_000)]
    public void A_large_stream_is_written_from_its_source_and_read_in_place(int version, int size) {
        // Version 3 at 10 MB needs more FAT sectors than the header's 109: the DIFAT.
        var root = new CfbStorage("Root Entry", Class);
        var expected = SHA256.Create();
        root.AddStream(
            "big",
            size,
            s => {
                var block = new byte[65_536];
                for (var at = 0; at < size; at += block.Length) {
                    var n = Math.Min(block.Length, size - at);
                    for (var i = 0; i < n; i++) {
                        block[i] = (byte)(((at + i) * 31) >> 3);
                    }

                    s.Write(block, 0, n);
                    expected.TransformBlock(block, 0, n, null, 0);
                }
            }
        );
        root.AddStream("after", Data(5000, 4));
        using var output = new MemoryStream();
        CfbWriter.Write(output, root, version);
        expected.TransformFinalBlock([], 0, 0);
        output.Position = 0;
        using var file = CfbReader.Open(output);
        var entry = file.Root.Child("big")!;
        Assert.Equal(size, entry.Size);
        using var stream = file.OpenStream(entry);
        using var actual = SHA256.Create();
        var buffer = new byte[10_007];
        int read;
        while ((read = stream.Read(buffer)) > 0) {
            actual.TransformBlock(buffer, 0, read, null, 0);
        }

        actual.TransformFinalBlock([], 0, 0);
        Assert.Equal(expected.Hash, actual.Hash);
        stream.Position = 5_000_001;
        Assert.Equal(unchecked((byte)((5_000_001 * 31) >> 3)), (byte)stream.ReadByte());
        Assert.Equal(Data(5000, 4), file.ReadAll(file.Root.Child("after")!));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Many_entries_round_trip_in_their_storages(int version) {
        var root = new CfbStorage("Root Entry", Class);
        for (var i = 0; i < 700; i++) {
            var storage = root.AddStorage($"Item{{{i:x}}}", Class);
            storage.AddStream("properties", Data(i % 300, i));
            if (i % 50 == 0) {
                for (var k = 0; k < 20; k++) {
                    storage.AddStream($"s{k}", Data(k, k));
                }
            }
        }

        var file = Round(root, version, out _);
        Assert.Equal(700, file.Root.Children.Count);
        for (var i = 0; i < 700; i++) {
            var storage = file.Root.Child($"ITEM{{{i:X}}}")!;
            Assert.Equal(Data(i % 300, i), file.ReadAll(storage.Child("properties")!));
            Assert.Equal(i % 50 == 0 ? 21 : 1, storage.Children.Count);
        }

        Assert.Equal(Data(7, 7), file.ReadAll(file.Root.Child("Item{0}")!.Child("s7")!));
    }

    [Fact]
    public void Siblings_form_a_valid_red_black_tree_in_the_formats_order() {
        var root = new CfbStorage("Root Entry", Class);
        foreach (var name in new[] { "b", "A", "aa", "c", "Z", "ab", "abc", "d", "e", "f" }) {
            root.AddStream(name, [1]);
        }

        using var output = new MemoryStream();
        CfbWriter.Write(output, root, 3);
        var bytes = output.ToArray();
        // The directory, where the header says.
        var directory = 512 * (1 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(48)));
        var entries = Enumerable.Range(0, 11).Select(i => bytes.AsSpan(directory + 128 * i, 128).ToArray()).ToList();

        string Name(uint id) =>
            System.Text.Encoding.Unicode.GetString(
                entries[(int)id],
                0,
                BinaryPrimitives.ReadUInt16LittleEndian(entries[(int)id].AsSpan(64)) - 2
            );

        uint Link(uint id, int at) => BinaryPrimitives.ReadUInt32LittleEndian(entries[(int)id].AsSpan(at));
        var order = new List<string>();

        int Walk(uint id, bool parentRed) {
            if (id == 0xFFFFFFFF) {
                return 1;
            }

            var red = entries[(int)id][67] == 0;
            Assert.False(red && parentRed, "a red node with a red child");
            var left = Walk(Link(id, 68), red);
            order.Add(Name(id));
            var right = Walk(Link(id, 72), red);
            Assert.Equal(left, right);
            return left + (red ? 0 : 1);
        }

        Walk(Link(0, 76), true);
        Assert.Equal(["A", "b", "c", "d", "e", "f", "Z", "aa", "ab", "abc"], order);
    }

    [Fact]
    public void A_source_that_writes_other_than_it_announced_is_refused() {
        var root = new CfbStorage("Root Entry", Class);
        root.AddStream("short", 5000, static s => s.Write(new byte[10]));
        Assert.Throws<InvalidOperationException>(() => CfbWriter.Write(new MemoryStream(), root));
        var other = new CfbStorage("Root Entry", Class);
        other.AddStream("long", 10, static s => s.Write(new byte[11]));
        Assert.Throws<InvalidOperationException>(() => CfbWriter.Write(new MemoryStream(), other));
    }

    [Fact]
    public void Invalid_names_are_refused() {
        var root = new CfbStorage("Root Entry", Class);
        Assert.Throws<ArgumentException>(() => root.AddStream(new('x', 32), [1]));
        Assert.Throws<ArgumentException>(() => root.AddStorage("a/b", Class));
        root.AddStream("Same", [1]);
        root.AddStream("SAME", [1]);
        Assert.Throws<ArgumentException>(() => CfbWriter.Write(new MemoryStream(), root));
    }

    [Fact]
    public void Corrupt_files_are_refused_with_a_format_exception() {
        Assert.Throws<FormatException>(static () => CfbReader.Open(new MemoryStream(new byte[100])));
        Assert.Throws<FormatException>(static () => CfbReader.Open(new MemoryStream(Data(4096, 9))));

        var root = new CfbStorage("Root Entry", Class);
        root.AddStream("data", Data(5000, 1));
        using var output = new MemoryStream();
        CfbWriter.Write(output, root, 3);
        var good = output.ToArray();

        // A FAT chain that loops: the stream's first sector points at itself.
        var looped = (byte[])good.Clone();
        var fat = 512 * (1 + (int)BinaryPrimitives.ReadUInt32LittleEndian(looped.AsSpan(76)));
        BinaryPrimitives.WriteUInt32LittleEndian(looped.AsSpan(fat), 0);
        Assert.Throws<FormatException>(() => {
                using var file = CfbReader.Open(new MemoryStream(looped));
                file.ReadAll(file.Root.Child("data")!);
            }
        );

        // A directory whose root points at itself.
        var cyclic = (byte[])good.Clone();
        var directory = 512 * (1 + (int)BinaryPrimitives.ReadUInt32LittleEndian(cyclic.AsSpan(48)));
        BinaryPrimitives.WriteUInt32LittleEndian(cyclic.AsSpan(directory + 76), 0);
        Assert.Throws<FormatException>(() => CfbReader.Open(new MemoryStream(cyclic)));

        // Truncated: the stream's sectors are gone.
        Assert.Throws<FormatException>(() => {
                using var file = CfbReader.Open(new MemoryStream(good[..1536]));
                file.ReadAll(file.Root.Child("data")!);
            }
        );

        // A sector number past the end.
        var outside = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(48), 100_000);
        Assert.Throws<FormatException>(() => CfbReader.Open(new MemoryStream(outside)));
    }

    [Fact]
    public void A_file_that_is_not_aaf_is_refused_by_the_reader() {
        var folder = Directory.CreateTempSubdirectory("anode-aaf-bad-").FullName;
        try {
            var text = Path.Combine(folder, "text.aaf");
            File.WriteAllText(text, "not an AAF file at all");
            Assert.Throws<FormatException>(() => AafReader.Read(text, folder));

            var empty = Path.Combine(folder, "empty.aaf");
            File.WriteAllBytes(empty, []);
            Assert.Throws<FormatException>(() => AafReader.Read(empty, folder));

            // A compound file without the AAF objects.
            var plain = Path.Combine(folder, "plain.aaf");
            var root = new CfbStorage("Root Entry", Class);
            root.AddStream("properties", [0x4C, 0x20, 0, 0]);
            CfbWriter.Write(plain, root);
            Assert.Throws<FormatException>(() => AafReader.Read(plain, folder));

            // Garbage in the root's property stream.
            var garbage = Path.Combine(folder, "garbage.aaf");
            root = new("Root Entry", Class);
            root.AddStream("properties", [0x4C, 0x20, 9, 0, 1, 2]);
            CfbWriter.Write(garbage, root);
            Assert.Throws<FormatException>(() => AafReader.Read(garbage, folder));

            // An AAF file cut short.
            var scenario = new AafScenario();
            try {
                var whole = Path.Combine(folder, "whole.aaf");
                AafWriter.Write(scenario.State, scenario.Folder, new("whole"), whole);
                var cut = Path.Combine(folder, "cut.aaf");
                File.WriteAllBytes(cut, File.ReadAllBytes(whole)[..200_000]);
                Assert.Throws<FormatException>(() => AafReader.Read(cut, Path.Combine(folder, "media")));
            } finally {
                scenario.Dispose();
            }
        } finally {
            Directory.Delete(folder, true);
        }
    }

    static CfbReader Round(CfbStorage root, int version, out byte[] bytes) {
        using var output = new MemoryStream();
        CfbWriter.Write(output, root, version);
        bytes = output.ToArray();
        return CfbReader.Open(new MemoryStream(bytes));
    }

    static byte[] Data(int size, int seed) {
        var data = new byte[size];
        for (var i = 0; i < size; i++) {
            data[i] = (byte)(i * 7 + seed * 13 + (i >> 8));
        }

        return data;
    }
}
