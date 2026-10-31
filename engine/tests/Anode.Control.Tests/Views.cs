using Anode.Contracts;
using Anode.Control.Surfaces;
using System.Globalization;

namespace Anode.Control.Tests;

/// <summary>Project views for the surface and OSC tests, and the hex notation the vectors use.</summary>
static class Views {
    /// <summary>
    ///     <paramref name="count" /> tracks t1… named "Track n": fader (n mod 9) / 8 (track 1 at 1/8), pan −1 + (n mod 5)
    ///     / 2, track 3 muted, 4 soloed, 5 armed, track 1 selected (automation read) with inserts "Channel EQ" (10
    ///     parameters) and "Compressor" (3); odd tracks have a send "Bus 1" at 0.5, track 1 a second "Bus 2" at 0.25.
    ///     Stopped at 00:00:10:00, bar 6.
    /// </summary>
    public static ProjectView Tracks(int count) {
        var tracks = new List<TrackView>(count);
        for (var n = 1; n <= count; n++) {
            var sends = new List<ParameterView>();
            if (n % 2 != 0) {
                sends.Add(new("send:s1", "Bus 1", 0.5, "-6.0 dB"));
            }

            if (n == 1) {
                sends.Add(new("send:s2", "Bus 2", 0.25, "-18.0 dB"));
            }

            var inserts = n == 1
                ? new List<InsertView> {
                    new(
                        "eq",
                        "Channel EQ",
                        [
                            ..Enumerable.Range(1, 10)
                                .Select(static p => new ParameterView(
                                        Invariant($"insert:eq:p{p}"),
                                        Invariant($"Band {p}"),
                                        p / 10.0,
                                        Invariant($"{p}.0 dB")
                                    )
                                )
                        ]
                    ),
                    new(
                        "comp",
                        "Compressor",
                        [
                            new("insert:comp:threshold", "Threshold", 0.5, "-20 dB"),
                            new("insert:comp:ratio", "Ratio", 0.2, "2.0:1"),
                            new("insert:comp:attack", "Attack", 0.1, "10 ms")
                        ]
                    )
                }
                : [];
            tracks.Add(
                new(
                    Invariant($"t{n}"),
                    Invariant($"Track {n}"),
                    n % 9 / 8.0,
                    -6,
                    -1 + n % 5 / 2.0,
                    n == 3,
                    n == 4,
                    n == 5,
                    n == 1,
                    n == 1 ? AutomationMode.Read : AutomationMode.Off,
                    0,
                    sends,
                    inserts
                )
            );
        }

        return new(
            tracks,
            TransportState.Stopped,
            false,
            120,
            new(0, 0, 10, 0),
            new(6, 1, 1, 0),
            [new("m1", "Intro", 0), new("m2", "Verse", 7680)],
            [new("knob1", "Cutoff", 0.75, "2.4 kHz"), new("knob2", "Drive", 0.1, "10 %")]
        );
    }

    /// <summary>A view with track <paramref name="index" /> (0-based) changed by <paramref name="change" />.</summary>
    public static ProjectView With(ProjectView view, int index, Func<TrackView, TrackView> change) {
        var tracks = view.Tracks.ToList();
        tracks[index] = change(tracks[index]);
        return view with { Tracks = tracks };
    }

    /// <summary>Playing at 00:00:12:05, bar 7 beat 2, with meters: track n at n / 10 (capped at 1).</summary>
    public static ProjectView Playing(ProjectView view) =>
        view with {
            Transport = TransportState.Playing,
            Timecode = new(0, 0, 12, 5),
            Beats = new(7, 2, 3, 120),
            Tracks = [..view.Tracks.Select((t, i) => t with { Meter = Math.Min(1, (i + 1) / 10.0) })]
        };

    public static byte[] Bytes(string hex) => [
        ..hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
    ];

    public static string Hex(byte[] bytes) =>
        string.Join(' ', bytes.Select(static b => b.ToString("X2", CultureInfo.InvariantCulture)));

    public static string[] Hex(IReadOnlyList<byte[]> messages) => [..messages.Select(Hex)];

    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
