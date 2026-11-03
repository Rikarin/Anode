using Anode.Contracts;
using Anode.Interchange.Adm;
using Anode.Model.Audio;
using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace Anode.Interchange.Tests.Adm;

/// <summary>ADM BWF: the writer's chunks and ADM, the reader's round trip, BW64 with ds64, and malformed files.</summary>
public sealed class AdmTests : IDisposable {
    readonly AdmScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    static float[][] Channels(string path) {
        using var reader = WavReader.Open(path);
        var info = reader.Info;
        var all = new float[info.Frames * info.Channels];
        var read = 0;
        while (read < all.Length) {
            var n = reader.Read(all.AsSpan(read));
            if (n == 0) {
                break;
            }

            read += n * info.Channels;
        }

        return [
            ..Enumerable.Range(0, info.Channels)
                .Select(c => Enumerable.Range(0, (int)info.Frames).Select(f => all[f * info.Channels + c]).ToArray())
        ];
    }

    AdmScene Mixed(long frames) {
        var bed = scenario.Noise("bed916.wav", 16, frames, [..Enumerable.Repeat(0.3f, 16)], 5);
        var stereo = scenario.Noise("pad.wav", 2, frames, [0.4f, 0.2f], 6);
        var mono = scenario.Noise("voice.wav", 1, frames, [0.5f], 7);
        return new(
            "Mixed",
            AdmScenario.Rate,
            frames,
            [new("Bed 9.1.6", ChannelFormat.Surround916, bed)],
            [
                new("Pad", stereo, 2, [new(0, frames, 60, 10, 0.8, 0.25, false)]),
                new(
                    "Voice",
                    mono,
                    1,
                    [
                        new(0, 1000, -20, 0, 1, 0, false),
                        new(1000, 960, -10, 5, 1, 0, true),
                        new(1960, frames - 1960, 0, 10, 1, 0, true)
                    ]
                )
            ]
        );
    }

    [Fact]
    public void Write_then_read_gives_back_the_scene_and_its_audio() {
        const long frames = 9_600;
        var scene = Mixed(frames);
        var path = scenario.PathOf("mixed.wav");
        AdmWriter.Write(scene, path, 32);
        var media = scenario.PathOf("media");
        var back = AdmReader.Read(path, media);

        Assert.Equal(("Mixed", AdmScenario.Rate, frames), (back.Name, back.SampleRate, back.Frames));
        var bed = Assert.Single(back.Beds);
        Assert.Equal(("Bed 9.1.6", ChannelFormat.Surround916), (bed.Name, bed.Format));
        Assert.Equal(Channels(scene.Beds[0].AudioPath), Channels(bed.AudioPath));
        Assert.Equal(["Pad", "Voice"], back.Objects.Select(static o => o.Name));
        for (var o = 0; o < 2; o++) {
            Assert.Equal(scene.Objects[o].Channels, back.Objects[o].Channels);
            Assert.Equal(Channels(scene.Objects[o].AudioPath), Channels(back.Objects[o].AudioPath));
            Assert.Equal(scene.Objects[o].Blocks.Count, back.Objects[o].Blocks.Count);
            foreach (var (written, read) in scene.Objects[o].Blocks.Zip(back.Objects[o].Blocks)) {
                Assert.Equal(written.StartFrame, read.StartFrame);
                Assert.Equal(written.DurationFrames, read.DurationFrames);
                Assert.Equal(written.Azimuth, read.Azimuth, 6);
                Assert.Equal(written.Elevation, read.Elevation, 6);
                Assert.Equal(written.Distance, read.Distance, 6);
                Assert.Equal(written.Size, read.Size, 6);
                Assert.Equal(written.Interpolate, read.Interpolate);
            }
        }

        Assert.Equal(3, Directory.GetFiles(media).Length);
    }

    [Fact]
    public void Twenty_four_bit_files_round_trip_within_the_quantisation() {
        const long frames = 4_800;
        var scene = scenario.GoldenPath(frames);
        var path = scenario.PathOf("golden.wav");
        AdmWriter.Write(scene, path);
        var back = AdmReader.Read(path, scenario.PathOf("media24"));
        var written = Channels(scene.Beds[0].AudioPath);
        var read = Channels(back.Beds[0].AudioPath);
        for (var c = 0; c < 12; c++) {
            for (var f = 0; f < frames; f++) {
                Assert.Equal(written[c][f], read[c][f], 1e-6f);
            }
        }

        Assert.Equal(ChannelFormat.Surround714, back.Beds[0].Format);
        Assert.Equal(scene.Objects[0].Blocks.Count, back.Objects[0].Blocks.Count);
    }

    [Fact]
    public void The_file_has_chna_and_axml_as_bs2076_and_bs2088_ask() {
        const long frames = 4_800;
        var scene = scenario.GoldenPath(frames);
        var path = scenario.PathOf("golden.wav");
        AdmWriter.Write(scene, path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        var chunks = Chunks(bytes);
        Assert.Equal(["JUNK", "fmt ", "chna", "axml", "data"], chunks.Select(static c => c.Id));
        var fmt = chunks[1];
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(fmt.Offset)));
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(fmt.Offset + 2)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(fmt.Offset + 14)));

        var chna = chunks[2];
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chna.Offset)));
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chna.Offset + 2)));
        string Row(int i, int at, int length) => Encoding.ASCII.GetString(bytes, chna.Offset + 4 + 40 * i + at, length);
        Assert.Equal(("ATU_00000001", "AT_00010001_01", "AP_00010017"), (Row(0, 2, 12), Row(0, 14, 14), Row(0, 28, 11)));
        Assert.Equal(("ATU_0000000d", "AT_00031001_01", "AP_00031001"), (Row(12, 2, 12), Row(12, 14, 14), Row(12, 28, 11)));

        var axml = XDocument.Parse(Encoding.UTF8.GetString(bytes, chunks[3].Offset, chunks[3].Size));
        XNamespace ns = "urn:ebu:metadata-schema:ebuCore_2017";
        var afx = axml.Descendants(ns + "audioFormatExtended").Single();
        Assert.Equal("ITU-R_BS.2076-2", (string?)afx.Attribute("version"));
        Assert.Equal("Golden path", (string?)afx.Element(ns + "audioProgramme")!.Attribute("audioProgrammeName"));
        Assert.Equal(2, afx.Elements(ns + "audioObject").Count());
        var channel = afx.Elements(ns + "audioChannelFormat").Single();
        Assert.Equal("Objects", (string?)channel.Attribute("typeDefinition"));
        var blocks = channel.Elements(ns + "audioBlockFormat").ToList();
        Assert.Equal(scene.Objects[0].Blocks.Count, blocks.Count);
        Assert.Equal("00:00:00.00001S48000", (string?)blocks[1].Attribute("rtime"));
        Assert.Equal("00:00:00.00960S48000", (string?)blocks[1].Attribute("duration"));
        Assert.Equal("1", blocks[0].Element(ns + "jumpPosition")!.Value);
        Assert.Null(blocks[1].Element(ns + "jumpPosition"));
        Assert.Equal(13, afx.Elements(ns + "audioTrackUID").Count());
    }

    static List<(string Id, int Offset, int Size)> Chunks(byte[] bytes) {
        var list = new List<(string, int, int)>();
        for (var at = 12; at + 8 <= bytes.Length;) {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            list.Add((id, at + 8, size));
            at += 8 + size + (size & 1);
        }

        return list;
    }

    [Fact]
    public void A_large_file_is_bw64_with_ds64_and_reads_back() {
        const long frames = 4_800;
        var scene = scenario.GoldenPath(frames);
        var path = scenario.PathOf("large.wav");
        AdmWriter.Write(scene, path, 24, true);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("BW64", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("ds64", Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(frames * 13 * 3, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(frames, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(36)));
        var back = AdmReader.Read(path, scenario.PathOf("media-large"));
        Assert.Equal(frames, back.Frames);
        Assert.Equal(ChannelFormat.Surround714, back.Beds[0].Format);
    }

    [Fact]
    public void Every_layout_writes_and_reads_as_a_bed() {
        foreach (var layout in SpeakerLayouts.All) {
            var count = layout.ChannelCount;
            var audio = scenario.Noise($"bed-{layout.Name}.wav", count, 480, [..Enumerable.Repeat(0.1f, count)], count);
            var path = scenario.PathOf($"adm-{layout.Name}.wav");
            AdmWriter.Write(new(layout.Name, AdmScenario.Rate, 480, [new("Bed", layout.Format, audio)], []), path);
            var back = AdmReader.Read(path, scenario.PathOf($"media-{layout.Name}"));
            Assert.Equal(layout.Format, Assert.Single(back.Beds).Format);
            var written = Channels(audio);
            var read = Channels(back.Beds[0].AudioPath);
            for (var c = 0; c < count; c++) {
                for (var f = 0; f < 480; f++) {
                    Assert.Equal(written[c][f], read[c][f], 1e-6f);
                }
            }
        }
    }

    [Fact]
    public void Sources_must_match_the_scene() {
        var scene = scenario.GoldenPath(4_800);
        Assert.Throws<ArgumentException>(() => AdmWriter.Write(scene with { Frames = 4_801 }, scenario.PathOf("x.wav")));
        Assert.Throws<ArgumentException>(() => AdmWriter.Write(scene with { SampleRate = 44_100 }, scenario.PathOf("x.wav")));
        Assert.Throws<ArgumentException>(() => AdmWriter.Write(
            scene with { Objects = [scene.Objects[0] with { Blocks = [new(10, 4_790, 0, 0, 1, 0, false)] }] },
            scenario.PathOf("x.wav")
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdmWriter.Write(scene, scenario.PathOf("x.wav"), 16));
    }

    [Fact]
    public void Malformed_files_are_refused_with_a_format_exception() {
        var good = scenario.PathOf("good.wav");
        AdmWriter.Write(scenario.GoldenPath(960), good);
        var bytes = File.ReadAllBytes(good);
        var media = scenario.PathOf("media-bad");

        void Refused(string name, byte[] content) {
            var path = scenario.PathOf(name);
            File.WriteAllBytes(path, content);
            Assert.Throws<FormatException>(() => AdmReader.Read(path, media));
        }

        Refused("empty.wav", []);
        Refused("text.wav", "not a wave file at all"u8.ToArray());
        Refused("short.wav", bytes[..(bytes.Length / 2)]);
        var bw64 = (byte[])bytes.Clone();
        "BW64"u8.CopyTo(bw64);
        Refused("no-ds64.wav", bw64);
        var plain = scenario.PathOf("plain.wav");
        using (var writer = new WavWriter(plain, 2, 48_000, WavEncoding.Pcm24)) {
            writer.Write(new float[200]);
        }

        Refused("plain-copy.wav", File.ReadAllBytes(plain));
        var badXml = (byte[])bytes.Clone();
        var axml = Chunks(bytes).Single(static c => c.Id == "axml");
        badXml[axml.Offset] = (byte)'#';
        Refused("bad-xml.wav", badXml);
        var badChna = (byte[])bytes.Clone();
        var chna = Chunks(bytes).Single(static c => c.Id == "chna");
        BinaryPrimitives.WriteUInt16LittleEndian(badChna.AsSpan(chna.Offset + 4), 99);
        Refused("bad-track.wav", badChna);
    }

    [Fact]
    public void Blocks_from_points_ramp_in_20_ms_steps_and_hold_as_jumps() {
        var blocks = AdmBlocks.FromPoints(
            [(0, 0, 0, 1, 0, false), (4_800, 0, 0, 1, 0, true), (7_200, 90, 30, 1, 0.5, false)],
            9_600,
            48_000
        );
        Assert.Equal(0, blocks[0].StartFrame);
        for (var b = 1; b < blocks.Count; b++) {
            Assert.Equal(blocks[b - 1].StartFrame + blocks[b - 1].DurationFrames, blocks[b].StartFrame);
        }

        Assert.Equal(9_600, blocks[^1].StartFrame + blocks[^1].DurationFrames);
        Assert.Equal((0L, 4_800L, false), (blocks[0].StartFrame, blocks[0].DurationFrames, blocks[0].Interpolate));
        var ramp = blocks.Where(static b => b.Interpolate).ToList();
        Assert.Equal(3, ramp.Count);
        Assert.All(ramp, static b => Assert.True(b.DurationFrames <= 960));
        Assert.Equal(90, ramp[^1].Azimuth, 9);
        Assert.Equal(36, ramp[0].Azimuth, 9);
        Assert.Equal((7_200L, 2_400L, false, 90.0), (blocks[^1].StartFrame, blocks[^1].DurationFrames, blocks[^1].Interpolate, blocks[^1].Azimuth));
    }
}
