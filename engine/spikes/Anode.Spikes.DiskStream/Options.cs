using System.Globalization;

namespace Anode.Spikes.DiskStream;

/// <summary><c>--key value</c> pairs and bare <c>--flag</c>s after the mode name.</summary>
sealed class Options {
    readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> args) {
        var result = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++) {
            if (!list[i].StartsWith("--", StringComparison.Ordinal)) {
                continue;
            }

            var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
            result.values[list[i][2..]] = hasValue ? list[++i] : null;
        }

        return result;
    }

    public bool Flag(string name) => values.ContainsKey(name);

    public string? Text(string name) => values.GetValueOrDefault(name);

    public int Int(string name, int fallback) =>
        Text(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

    public double Double(string name, double fallback) =>
        Text(name) is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;
}
