using Anode.Model;
using Anode.Model.Editing;
using System.Globalization;

namespace Anode.Server.Control;

/// <summary>
///     The fader law hardware and OSC use for volumes and sends (normalised 0…1), the editor's own
///     (<c>@anode/audio-ui</c>'s <c>taper.ts</c>): the useful range around 0 dB gets most of the travel (0 dB at 0.78),
///     the bottom few percent fall away to silence. Also the meter scale surfaces show (−60…0 dBFS).
/// </summary>
public static class Taper {
    /// <summary>The bottom of the meter scale, in dBFS.</summary>
    public const double MeterFloorDb = -60;

    static readonly (double Position, double Db)[] Points = [
        (0, Units.SilenceDb), (0.02, -60), (0.08, -45), (0.18, -30), (0.3, -20), (0.45, -12), (0.6, -6), (0.78, 0),
        (1, Edits.MaxVolumeDb)
    ];

    /// <summary>Where on the fader's travel (0 bottom … 1 top) a level in dB sits.</summary>
    public static double Position(double db) {
        if (!double.IsFinite(db) || db <= Units.SilenceDb) {
            return 0;
        }

        if (db >= Edits.MaxVolumeDb) {
            return 1;
        }

        for (var i = 1; i < Points.Length; i++) {
            var (p1, d1) = Points[i];
            if (db <= d1) {
                var (p0, d0) = Points[i - 1];
                return p0 + (db - d0) / (d1 - d0) * (p1 - p0);
            }
        }

        return 1;
    }

    /// <summary>The level in dB at a position on the fader's travel (the inverse of <see cref="Position" />).</summary>
    public static double Db(double position) {
        if (!(position > 0)) {
            return Units.SilenceDb;
        }

        if (position >= 1) {
            return Edits.MaxVolumeDb;
        }

        for (var i = 1; i < Points.Length; i++) {
            var (p1, d1) = Points[i];
            if (position <= p1) {
                var (p0, d0) = Points[i - 1];
                return d0 + (position - p0) / (p1 - p0) * (d1 - d0);
            }
        }

        return Edits.MaxVolumeDb;
    }

    /// <summary>A post-fader peak (linear, 1 = 0 dBFS) on the meter scale, 0…1.</summary>
    public static double Meter(double peak) =>
        peak > 0 ? Math.Clamp((20 * Math.Log10(peak) - MeterFloorDb) / -MeterFloorDb, 0, 1) : 0;

    /// <summary>"−6.0 dB", "+2.5 dB", "−∞ dB", as the editor shows a level.</summary>
    public static string FormatDb(double db) {
        if (db <= Units.SilenceDb + 0.05 || !double.IsFinite(db)) {
            return "−∞ dB";
        }

        var rounded = Math.Round(db, 1);
        return rounded switch {
            0 => "0.0 dB",
            > 0 => "+" + rounded.ToString("0.0", CultureInfo.InvariantCulture) + " dB",
            _ => "−" + (-rounded).ToString("0.0", CultureInfo.InvariantCulture) + " dB"
        };
    }

    /// <summary>A pan position as Logic shows it: "L64", "C", "R63".</summary>
    public static string FormatPan(double pan) {
        var steps = (int)Math.Round(Math.Clamp(pan, -1, 1) * 64);
        return steps switch {
            0 => "C",
            < 0 => "L" + (-steps).ToString(CultureInfo.InvariantCulture),
            _ => "R" + Math.Min(63, steps).ToString(CultureInfo.InvariantCulture)
        };
    }
}
