using System.Collections.Frozen;
using System.Globalization;

namespace Anode.Host;

/// <summary>
///     Minimal parser: the first <c>--name</c> is the command; later <c>--key value</c> pairs and bare flags are
///     options.
/// </summary>
sealed class CommandLine {
    /// <summary>Every command <c>Program.cs</c> dispatches on; a name missing here is parsed as an option instead.</summary>
    public static readonly FrozenSet<string> Commands =
        new[] { "version", "devices", "serve", "help" }.ToFrozenSet(StringComparer.Ordinal);

    readonly Dictionary<string, string?> options = new(StringComparer.Ordinal);

    public string? Command { get; private set; }

    public static CommandLine Parse(string[] args) {
        var result = new CommandLine();
        for (var i = 0; i < args.Length; i++) {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) {
                continue;
            }

            var name = args[i][2..];
            if (result.Command is null && Commands.Contains(name)) {
                result.Command = name;
                continue;
            }

            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            result.options[name] = hasValue ? args[++i] : null;
        }

        return result;
    }

    public bool Flag(string name) => options.ContainsKey(name);

    public string? Text(string name) => options.GetValueOrDefault(name);

    /// <summary>An integer option, or <paramref name="fallback" /> when absent; a malformed one is an error.</summary>
    public int Int(string name, int fallback) {
        if (Text(name) is not { } text) {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"--{name} needs a whole number, not '{text}'.");
    }
}
