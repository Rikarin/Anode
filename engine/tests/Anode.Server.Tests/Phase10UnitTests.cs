using Anode.Contracts;
using Anode.Server.Control;
using Anode.Server.Engine;
using Anode.Server.Remote;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 10's server parts without a server: the master tap ring's reader (segment format 5) against its writer, and
///     the DNS-SD advertisement of <c>_anode._tcp</c> with its TXT record (macOS).
/// </summary>
public sealed class Phase10UnitTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static float[] Block(int frames, float value) => [..Enumerable.Repeat(value, frames * 2)];

    [Fact]
    public void The_tap_reader_starts_at_the_writer_and_splits_at_the_stamps() {
        using var writer = MasterTapWriter.InMemory(48_000, 1 << 10, 1 << 4);
        using var reader = MasterTapReader.Over(writer.Region);
        var audio = new float[4096];
        var runs = new List<TapRun>();
        writer.Write(Block(64, 0.1f), 500, true, false);
        // The first read starts where the writer is: nothing yet.
        Assert.Equal(0, reader.Read(audio, runs));
        writer.Write(Block(64, 0.2f), 1000, true, false);
        writer.Write(Block(32, 0.3f), 0, true, true);
        writer.Write(Block(32, 0f), -1, false, false);
        Assert.Equal(128, reader.Read(audio, runs));
        Assert.Equal(
            [(0, 64, 1000L, true, true), (64, 32, 0, true, true), (96, 32, -1, false, false)],
            runs.Select(static r => (r.Offset, r.Frames, r.TransportSample, r.Playing, r.Discontinuity))
        );
        Assert.Equal((0.2f, 0.3f, 0f), (audio[0], audio[64 * 2], audio[96 * 2]));
        Assert.Equal(0, reader.Behind);

        // A block read in two parts maps both through its stamp.
        writer.Write(Block(100, 0.4f), 2000, true, false);
        Assert.Equal(40, reader.Read(audio.AsSpan(0, 80), runs));
        Assert.Equal((2000L, false), (runs[0].TransportSample, runs[0].Discontinuity));
        Assert.Equal(60, reader.Read(audio, runs));
        Assert.Equal(2040, runs[0].TransportSample);
    }

    [Fact]
    public void A_reader_more_than_a_ring_behind_skips_to_half_a_ring_and_marks_a_discontinuity() {
        using var writer = MasterTapWriter.InMemory(48_000, 1 << 8, 1 << 6);
        using var reader = MasterTapReader.Over(writer.Region);
        var audio = new float[4096];
        var runs = new List<TapRun>();
        reader.Read(audio, runs);
        for (var i = 0; i < 10; i++) {
            writer.Write(Block(64, i), 64L * i, true, false);
        }

        // 640 frames written into a ring of 256: from frame 640 − 128.
        Assert.Equal(128, reader.Read(audio, runs));
        Assert.True(runs[0].Discontinuity);
        Assert.Equal(512, runs[0].TransportSample);
        Assert.Equal(8f, audio[0]);
        writer.Write(Block(64, 1), 640, true, false);
        Assert.Equal(64, reader.Read(audio, runs));
        Assert.False(runs[0].Discontinuity);
    }

    [Fact]
    public void A_TXT_record_is_length_prefixed_key_value_pairs() {
        Assert.Equal([0], Bonjour.Txt([]));
        Assert.Equal(
            "\u0009version=1\u0009pairing=0"u8.ToArray(),
            Bonjour.Txt([new("version", "1"), new("pairing", "0")])
        );
        Assert.Equal(255, Bonjour.Txt([new("project", new string('x', 400))])[0]);
    }

    [Fact]
    public async Task The_server_is_advertised_as_anode_tcp_with_its_TXT_keys() {
        Assert.SkipUnless(
            OperatingSystem.IsMacOS() && File.Exists("/usr/bin/dns-sd"),
            "DNS-SD through mDNSResponder is macOS's."
        );
        var name = $"Anode test {Guid.NewGuid():n}"[..24];
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var info = new DiscoveryInfo(name, "localhost", port, "Score Cue", true, "1.2.3");
        using var advertised = Bonjour.Advertise(
            name,
            Discovery.ServiceType,
            port,
            NullLogger.Instance,
            DiscoveryService.Txt(info)
        );
        Assert.NotNull(advertised);

        // dns-sd -L resolves the instance: its port and TXT record.
        using var lookup = Process.Start(
            new ProcessStartInfo("/usr/bin/dns-sd", ["-L", name, Discovery.ServiceType, "local."]) {
                RedirectStandardOutput = true, UseShellExecute = false
            }
        )!;
        var output = new System.Text.StringBuilder();
        lookup.OutputDataReceived += (_, e) => {
            lock (output) {
                output.AppendLine(e.Data);
            }
        };
        lookup.BeginOutputReadLine();
        try {
            await ServerFixture.Eventually(
                () => {
                    lock (output) {
                        return Task.FromResult(output.ToString().Contains("pairing=1", StringComparison.Ordinal));
                    }
                },
                10
            );
        } finally {
            lookup.Kill();
            await lookup.WaitForExitAsync(Token);
        }

        var text = output.ToString();
        Assert.Contains($":{port}", text, StringComparison.Ordinal);
        Assert.Contains("version=1.2.3", text, StringComparison.Ordinal);
        // dns-sd prints a space in a value escaped.
        Assert.Matches(@"project=Score(\\)? Cue", text);
        Assert.Contains("path=/api/v1", text, StringComparison.Ordinal);
    }
}
