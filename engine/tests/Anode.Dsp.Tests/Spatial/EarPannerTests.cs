using Anode.Dsp.Spatial;
using System.Globalization;
using System.Text.Json;

namespace Anode.Dsp.Tests.Spatial;

/// <summary>
///     Anode's object panner against the EBU ADM Renderer's (ITU-R BS.2127's point-source and extent panners, through
///     <c>scripts/ear-gains.py</c>) on a grid of directions, for every layout <c>ear</c> knows. Opt-in (<see cref="Ear" />).
/// </summary>
public sealed class EarPannerTests {
    public static TheoryData<SpeakerFormat, string> Systems() =>
        new() {
            { SpeakerFormat.Stereo, "0+2+0" },
            { SpeakerFormat.Surround51, "0+5+0" },
            { SpeakerFormat.Surround71, "0+7+0" },
            { SpeakerFormat.Surround714, "4+7+0" }
        };

    [Theory]
    [MemberData(nameof(Systems))]
    public void Point_sources_match_ear_on_a_grid_of_directions(SpeakerFormat format, string system) {
        var directions = new List<(float Azimuth, float Elevation)>();
        for (var elevation = -60; elevation <= 90; elevation += 15) {
            for (var azimuth = -180; azimuth < 180; azimuth += 10) {
                directions.Add((azimuth + 2.5f, elevation));
            }
        }

        Compare(format, system, directions, 0, 1e-4);
    }

    [Theory]
    [MemberData(nameof(Systems))]
    public void Sized_sources_match_ear(SpeakerFormat format, string system) {
        List<(float, float)> directions = [(0, 0), (30, 0), (-75, 10), (120, 20), (-160, 45), (45, 70), (10, -30)];
        foreach (var size in new[] { 0.02f, 0.1f, 0.25f, 0.6f }) {
            Compare(format, system, directions, size, 1e-3);
        }
    }

    static void Compare(
        SpeakerFormat format,
        string system,
        List<(float Azimuth, float Elevation)> directions,
        float size,
        double tolerance
    ) {
        var layout = SpeakerLayout.Of(format);
        var vbap = new Vbap(layout);
        var extent = ObjectPosition.ExtentDegrees(size);
        var input = JsonSerializer.Serialize(directions.Select(d => new[] { d.Azimuth, d.Elevation, extent }));
        using var result = JsonDocument.Parse(Ear.Run("ear-gains.py", input, system));
        var names = result.RootElement.GetProperty("channels")
            .EnumerateArray()
            .Select(static n => n.GetString()!)
            .ToArray();
        var expected = result.RootElement.GetProperty("gains").EnumerateArray().ToArray();
        var gains = new float[layout.ChannelCount];
        for (var d = 0; d < directions.Count; d++) {
            vbap.Gains(new(directions[d].Azimuth, directions[d].Elevation, 1, size), gains);
            var reference = expected[d].EnumerateArray().Select(static g => g.GetDouble()).ToArray();
            for (var k = 0; k < names.Length; k++) {
                var channel = Enumerable.Range(0, layout.ChannelCount).Single(c => layout.Label(c) == names[k]);
                Assert.True(
                    Math.Abs(gains[channel] - reference[k]) <= tolerance,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{system} at ({directions[d].Azimuth}, {directions[d].Elevation}) size {size}: {names[k]} {gains[channel]:F5}, ear {reference[k]:F5}"
                    )
                );
            }

            if (layout.LfeIndex >= 0) {
                Assert.Equal(0, gains[layout.LfeIndex]);
            }
        }
    }
}
