using Anode.Contracts;
using Anode.Dsp.Instruments;

namespace Anode.Server.Engine;

/// <summary>A parameter's <see cref="ParameterKind" /> from the host's spelling, or by its shape when it sends none.</summary>
static class ParameterKinds {
    public static ParameterKind Parse(string? kind, int steps, double min, double max, IReadOnlyList<string>? names) =>
        kind switch {
            "slider" => ParameterKind.Slider,
            "menu" => ParameterKind.Menu,
            "checkbox" => ParameterKind.Checkbox,
            "momentary" => ParameterKind.Momentary,
            "text" => ParameterKind.Text,
            _ => InstrumentParameterKinds.Of(steps, min, max, names) switch {
                InstrumentParameterKind.Menu => ParameterKind.Menu,
                InstrumentParameterKind.Checkbox => ParameterKind.Checkbox,
                _ => ParameterKind.Slider
            }
        };
}
