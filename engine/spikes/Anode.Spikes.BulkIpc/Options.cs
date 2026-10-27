using System.Globalization;

namespace Anode.Spikes.BulkIpc;

/// <summary><c>--name value</c> pairs.</summary>
sealed class Options(Dictionary<string, string> values) {
    public static Options Parse(IEnumerable<string> args) {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var arg in args) {
            if (arg.StartsWith("--", StringComparison.Ordinal)) {
                key = arg[2..];
                values[key] = "true";
            } else if (key is not null) {
                values[key] = arg;
                key = null;
            }
        }

        return new(values);
    }

    public string? Text(string name) => values.GetValueOrDefault(name);

    public int Int(string name, int fallback) =>
        values.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public long[] Longs(string name, long[] fallback) =>
        values.TryGetValue(name, out var v)
            ? v.Split(',').Select(static x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray()
            : fallback;
}
