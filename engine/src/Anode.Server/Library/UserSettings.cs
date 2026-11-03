using Anode.Contracts;
using Anode.Model.Storage;
using Anode.Server.Projects;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Server.Library;

/// <summary>
///     The user's own settings, kept per user rather than per project (docs/04-phase-3.md, "Screensets, key commands,
///     bookmarks"): key bindings and the All Files browser's bookmarks, as JSON in <c>&lt;DataDirectory&gt;/settings/</c>,
///     each written atomically.
/// </summary>
public sealed class UserSettings(IOptions<ServerOptions> options) {
    public const string Folder = "settings";
    public const int MaxCommands = 2_000;
    public const int MaxBindingsPerCommand = 8;
    public const int MaxCommandIdLength = 128;
    public const int MaxBindingLength = 64;
    public const int MaxBookmarks = 200;
    public const int MaxPathLength = 4096;

    readonly Lock gate = new();

    string Path(string name) => System.IO.Path.Combine(options.Value.DataDirectory, Folder, name);

    /// <summary>The user's key bindings (empty: the editor's defaults).</summary>
    public KeyCommandSet KeyCommands() =>
        Read("key-commands.json", ContractsJsonContext.Default.KeyCommandSet) is { Bindings: not null } set
            ? set
            : new(new Dictionary<string, IReadOnlyList<string>>());

    public KeyCommandSet SaveKeyCommands(KeyCommandSet? set) {
        if (set?.Bindings is not { Count: <= MaxCommands } bindings) {
            throw ProjectException.Unprocessable($"Key commands bind at most {MaxCommands} commands.");
        }

        var clean = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (command, keys) in bindings) {
            if (command is not { Length: > 0 and <= MaxCommandIdLength } || string.IsNullOrWhiteSpace(command)) {
                throw ProjectException.Unprocessable($"A command id has 1…{MaxCommandIdLength} characters.");
            }

            if (keys is not { Count: <= MaxBindingsPerCommand }
                || keys.Any(static k => k is not { Length: > 0 and <= MaxBindingLength } || string.IsNullOrWhiteSpace(k)
                )) {
                throw ProjectException.Unprocessable(
                    $"Command '{command}' has at most {MaxBindingsPerCommand} bindings of 1…{MaxBindingLength} characters."
                );
            }

            clean[command] = [..keys.Distinct(StringComparer.Ordinal)];
        }

        var saved = new KeyCommandSet(clean);
        Write("key-commands.json", saved, ContractsJsonContext.Default.KeyCommandSet);
        return saved;
    }

    /// <summary>The folders pinned in the All Files browser.</summary>
    public Bookmarks Bookmarks() =>
        Read("bookmarks.json", ContractsJsonContext.Default.Bookmarks) is { Paths: not null } bookmarks
            ? bookmarks
            : new([]);

    public Bookmarks SaveBookmarks(Bookmarks? bookmarks) {
        if (bookmarks?.Paths is not { Count: <= MaxBookmarks } paths) {
            throw ProjectException.Unprocessable($"Keep at most {MaxBookmarks} bookmarks.");
        }

        var clean = new List<string>();
        foreach (var path in paths) {
            if (path is not { Length: > 0 and <= MaxPathLength } || !System.IO.Path.IsPathFullyQualified(path)) {
                throw ProjectException.Unprocessable(
                    $"A bookmark is an absolute path of at most {MaxPathLength} characters."
                );
            }

            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            if (!clean.Contains(full, StringComparer.Ordinal)) {
                clean.Add(full);
            }
        }

        var saved = new Bookmarks(clean);
        Write("bookmarks.json", saved, ContractsJsonContext.Default.Bookmarks);
        return saved;
    }

    public const int MaxIoNumber = 256;

    /// <summary>The user's names for inputs, output pairs and buses (Logic's I/O Labels; none: the defaults).</summary>
    public IoLabels IoLabels() =>
        Read("io-labels.json", ContractsJsonContext.Default.IoLabels) is {
            Inputs: not null, Outputs: not null, Buses: not null
        } labels
            ? labels
            : new([], [], []);

    /// <summary>
    ///     Saves the I/O labels: numbers 1…256 (buses 1…64), unique per list, names 1…128 characters; an empty name
    ///     drops the label (the default name shows).
    /// </summary>
    public IoLabels SaveIoLabels(IoLabels? labels) {
        if (labels is null) {
            throw ProjectException.Unprocessable("Send the inputs, outputs and buses lists.");
        }

        var saved = new IoLabels(
            Clean(labels.Inputs, MaxIoNumber, "input"),
            Clean(labels.Outputs, MaxIoNumber, "output pair"),
            Clean(labels.Buses, Model.Editing.Edits.MaxBus, "bus")
        );
        Write("io-labels.json", saved, ContractsJsonContext.Default.IoLabels);
        return saved;

        static IReadOnlyList<IoLabel> Clean(IReadOnlyList<IoLabel>? list, int max, string what) {
            if (list is null) {
                throw ProjectException.Unprocessable($"Send the {what} labels (an empty list for none).");
            }

            var clean = new SortedDictionary<int, IoLabel>();
            foreach (var label in list) {
                if (label is null || label.Number < 1 || label.Number > max) {
                    throw ProjectException.Unprocessable($"An {what} label's number is 1…{max}.");
                }

                var name = (label.Name ?? string.Empty).Trim();
                if (name.Length > Model.Editing.Edits.MaxNameLength) {
                    throw ProjectException.Unprocessable(
                        $"A label has at most {Model.Editing.Edits.MaxNameLength} characters."
                    );
                }

                if (!clean.TryAdd(label.Number, label with { Name = name })) {
                    throw ProjectException.Unprocessable($"Two labels for {what} {label.Number}.");
                }
            }

            return [..clean.Values.Where(static l => l.Name.Length > 0)];
        }
    }

    /// <summary>
    ///     Phase 8 (docs/09-phase-8.md): the controller assignments (none: <see cref="ControllerAssignments.Empty" />),
    ///     checked by <see cref="Control.ControlService" /> before they are saved.
    /// </summary>
    public ControllerAssignments ControllerAssignments() =>
        Read("controller-assignments.json", ContractsJsonContext.Default.ControllerAssignments) is { Zones: not null } a
            ? a
            : Contracts.ControllerAssignments.Empty;

    public void SaveControllerAssignments(ControllerAssignments assignments) =>
        Write("controller-assignments.json", assignments, ContractsJsonContext.Default.ControllerAssignments);

    /// <summary>The control surfaces, in order (none by default).</summary>
    public IReadOnlyList<ControlSurface> ControlSurfaces() =>
        Read("control-surfaces.json", ContractsJsonContext.Default.IReadOnlyListControlSurface) is { } surfaces
            ? [..surfaces.Where(static s => s is { Id: not null, InputPort: not null, OutputPort: not null })]
            : [];

    public void SaveControlSurfaces(IReadOnlyList<ControlSurface> surfaces) =>
        Write("control-surfaces.json", surfaces, ContractsJsonContext.Default.IReadOnlyListControlSurface);

    /// <summary>The OSC server's settings (none: <see cref="Contracts.OscSettings.Default" />).</summary>
    public OscSettings OscSettings() =>
        Read("osc.json", ContractsJsonContext.Default.OscSettings) is { Clients: not null } settings
            ? settings
            : Contracts.OscSettings.Default;

    public void SaveOscSettings(OscSettings settings) =>
        Write("osc.json", settings, ContractsJsonContext.Default.OscSettings);

    /// <summary>The longest speaker map: a channel per device output.</summary>
    public const int MaxSpeakerMap = 256;

    public const int MaxPadSets = 64;
    public const int MaxPadsPerSet = 64;
    public const int MaxPadIdLength = 64;

    /// <summary>Phase 10 (docs/11-phase-10.md): monitoring (none: <see cref="MonitoringSettings.Default" />).</summary>
    public MonitoringSettings Monitoring() =>
        Read("monitoring.json", ContractsJsonContext.Default.MonitoringSettings) is { SpeakerMap: not null } m
        && Valid(m)
            ? m
            : MonitoringSettings.Default;

    /// <summary>
    ///     Saves the monitoring settings: a known down-mix format (narrower than <paramref name="master" />, the open
    ///     project's format, when there is one), speaker map entries 0…256 (0: not sent), at most 256 of them.
    /// </summary>
    public MonitoringSettings SaveMonitoring(MonitoringSettings? monitoring, ChannelFormat? master) {
        if (monitoring?.SpeakerMap is null || !Valid(monitoring)) {
            throw ProjectException.Unprocessable(
                $"Monitoring has a known down-mix format and a speaker map of at most {MaxSpeakerMap} outputs 0…{MaxIoNumber}."
            );
        }

        if (monitoring.DownmixTo is { } to
            && master is { } format
            && SpeakerLayouts.ChannelCount(to) >= SpeakerLayouts.ChannelCount(format)) {
            throw ProjectException.Unprocessable(
                $"A down-mix is to a format narrower than the master's ({SpeakerLayouts.Of(format).Name})."
            );
        }

        var saved = monitoring with { SpeakerMap = [..monitoring.SpeakerMap] };
        Write("monitoring.json", saved, ContractsJsonContext.Default.MonitoringSettings);
        return saved;
    }

    static bool Valid(MonitoringSettings m) =>
        (m.DownmixTo is not { } to || Enum.IsDefined(to))
        && m.SpeakerMap.Count <= MaxSpeakerMap
        && m.SpeakerMap.All(static o => o is >= 0 and <= MaxIoNumber);

    /// <summary>Phase 10: the remote's key-command pad sets (none: <see cref="Contracts.RemotePads.Empty" />).</summary>
    public RemotePads RemotePads() =>
        Read("remote-pads.json", ContractsJsonContext.Default.RemotePads) is { Sets: not null } pads
            ? pads
            : Contracts.RemotePads.Empty;

    /// <summary>
    ///     Saves the pad sets: at most 64 of 64 pads, ids unique across sets and pads (1…64 characters), names and labels
    ///     1…128 characters, command ids as key commands name them (1…128 characters; the editor knows the commands).
    /// </summary>
    public RemotePads SaveRemotePads(RemotePads? pads) {
        if (pads?.Sets is not { Count: <= MaxPadSets } sets) {
            throw ProjectException.Unprocessable($"Keep at most {MaxPadSets} pad sets.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<PadSet>();
        foreach (var set in sets) {
            if (set?.Pads is not { Count: <= MaxPadsPerSet } list) {
                throw ProjectException.Unprocessable($"A pad set has at most {MaxPadsPerSet} pads.");
            }

            var padsOfSet = new List<KeyCommandPad>();
            foreach (var pad in list) {
                if (pad is null
                    || pad.CommandId is not { Length: > 0 and <= MaxCommandIdLength }
                    || string.IsNullOrWhiteSpace(pad.CommandId)) {
                    throw ProjectException.Unprocessable(
                        $"A pad runs a key command (1…{MaxCommandIdLength} characters)."
                    );
                }

                if (pad.Color is { Length: > 64 }) {
                    throw ProjectException.Unprocessable("A pad's colour has at most 64 characters.");
                }

                padsOfSet.Add(pad with { Id = Id(pad.Id), Label = Label(pad.Label, "pad label") });
            }

            clean.Add(new(Id(set.Id), Label(set.Name, "pad set name"), padsOfSet));
        }

        var saved = new RemotePads(clean);
        Write("remote-pads.json", saved, ContractsJsonContext.Default.RemotePads);
        return saved;

        string Id(string? id) =>
            id is { Length: > 0 and <= MaxPadIdLength } && !string.IsNullOrWhiteSpace(id) && ids.Add(id)
                ? id
                : throw ProjectException.Unprocessable(
                    $"Pad and set ids are unique, of 1…{MaxPadIdLength} characters ('{id}')."
                );

        static string Label(string? text, string what) {
            var trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length is > 0 and <= Model.Editing.Edits.MaxNameLength
                ? trimmed
                : throw ProjectException.Unprocessable(
                    $"A {what} has 1…{Model.Editing.Edits.MaxNameLength} characters."
                );
        }
    }

    T? Read<T>(string name, JsonTypeInfo<T> type) where T : class {
        lock (gate) {
            try {
                var path = Path(name);
                return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
            } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
                return null; // damaged or unreadable: the defaults
            }
        }
    }

    void Write<T>(string name, T value, JsonTypeInfo<T> type) {
        lock (gate) {
            try {
                Directory.CreateDirectory(System.IO.Path.Combine(options.Value.DataDirectory, Folder));
                AtomicRename.Write(Path(name), JsonSerializer.Serialize(value, type));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not save the settings: {e.Message}");
            }
        }
    }
}
