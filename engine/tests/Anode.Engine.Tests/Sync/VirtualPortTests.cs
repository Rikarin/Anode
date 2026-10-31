using Anode.Audio.Midi;
using Anode.Audio.Native;
using System.Text.Json.Nodes;

namespace Anode.Engine.Tests;

/// <summary>
///     Virtual MIDI ports through cth_audio (docs/09-phase-8.md, "Virtual ports"): this process publishes an input and
///     an output; anode-midiprobe, another process, finds them by their uids and sends to the input (short messages and
///     SysEx arrive in the rings, from the input's index) and hears the output (SysEx and several messages in one
///     send). Skipped where the backend has no virtual ports (WinMM, ALSA).
/// </summary>
public sealed class VirtualPortTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_loopback_through_virtual_ports_carries_short_messages_and_sysex_both_ways() {
        Assert.SkipUnless(ProbeProcess.Supported, "no CoreMIDI here");
        var prefix = $"vt{Environment.ProcessId}x{Random.Shared.Next(1000)}";
        using var midi = new NativeMidiBackend();
        Assert.Equal(0, midi.OpenVirtual("Virtual Test In", $"{prefix}:in", true));
        Assert.Equal(0, midi.OpenVirtual("Virtual Test Out", $"{prefix}:out", false));
        Assert.Equal(-3, midi.OpenVirtual("Again", $"{prefix}:in", true)); // a uid is open once
        Assert.True(midi.Open());
        var ports = midi.Ports();
        var input = ports.ToList().FindIndex(p => p.Uid == $"{prefix}:in");
        Assert.True(input >= 0);
        Assert.Contains(ports, p => p is { IsInput: false, Name: "Virtual Test Out" } && p.Uid == $"{prefix}:out");
        Assert.True(ports[input].IsInput);

        await using var probe = await ProbeProcess.StartAsync(Token);
        var opened = await probe.Command(new JsonObject { ["cmd"] = "open", ["in"] = $"{prefix}:out", ["out"] = $"{prefix}:in" });
        Assert.Equal("Virtual Test In", opened["out"]!["name"]!.GetValue<string>());

        // The probe sends to our input: a note, a SysEx of several pieces, and system real-time.
        byte[] sysex = [0xF0, 0x00, 0x00, 0x66, 0x14, 0x12, 0x00, .."Track 1 Track 2"u8, 0xF7];
        await probe.Command(new JsonObject { ["cmd"] = "send", ["hex"] = "903C64" });
        await probe.Command(new JsonObject { ["cmd"] = "send", ["hex"] = Convert.ToHexString(sysex) });
        await probe.Command(new JsonObject { ["cmd"] = "send", ["hex"] = "F8F15A" }); // a clock, then an MTC quarter frame
        var shorts = new List<MidiInputMessage>();
        var buffer = new byte[16384];
        byte[]? got = null;
        var source = -1;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var batch = new MidiInputMessage[16];
        while ((shorts.Count < 3 || got is null) && DateTime.UtcNow < deadline) {
            var n = midi.Read(batch);
            shorts.AddRange(batch.Take(n));
            var length = midi.ReadSystemExclusive(buffer, out _, out source);
            if (length > 0) {
                got = buffer[..length];
            }

            await Task.Delay(5, Token);
        }

        Assert.Equal([(0x90, 60, 100), (0xF8, 0, 0), (0xF1, 0x5A, 0)], shorts.Select(static m => ((int)m.Status, (int)m.Data1, (int)m.Data2)));
        Assert.All(shorts, m => Assert.Equal(input, m.Source));
        Assert.Equal(sysex, got);
        Assert.Equal(input, source);

        // A buffer too small leaves the message queued and says how long it is.
        await probe.Command(new JsonObject { ["cmd"] = "send", ["hex"] = Convert.ToHexString(sysex) });
        var small = 0;
        for (var i = 0; i < 500 && small == 0; i++) {
            small = midi.ReadSystemExclusive(new byte[4], out _, out _);
            await Task.Delay(5, Token);
        }

        Assert.Equal(-sysex.Length, small);
        Assert.Equal(sysex.Length, midi.ReadSystemExclusive(buffer, out _, out _));

        // We send to our output: the probe hears a SysEx and two short messages sent as one.
        Assert.True(midi.Send($"{prefix}:out", [0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7, 0xB0, 0x07, 0x64, 0xFA], 0));
        var heard = await probe.Command(new JsonObject { ["cmd"] = "wait", ["count"] = 3, ["timeoutMs"] = 5000 });
        Assert.Equal(
            ["B00764", "F07E7F0601F7", "FA"],
            heard["messages"]!.AsArray().Select(static m => m!["hex"]!.GetValue<string>()).Order(StringComparer.Ordinal)
        );

        Assert.True(midi.CloseVirtual($"{prefix}:in"));
        Assert.False(midi.CloseVirtual($"{prefix}:in"));
        Assert.DoesNotContain(midi.Ports(), p => p.Uid == $"{prefix}:in");
    }
}
