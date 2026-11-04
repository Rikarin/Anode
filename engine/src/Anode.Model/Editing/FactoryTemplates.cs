using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>A template Anode comes with: made by commands, so it is what a person could have made.</summary>
public sealed record FactoryTemplate(
    string Id,
    string Name,
    string Category,
    string Description,
    Action<FactoryTemplates.Builder> Make
);

/// <summary>
///     The project templates Anode comes with (docs/04-phase-3.md, "Templates"). They hold tracks and routing, no audio,
///     so a new project can be at any rate.
/// </summary>
public static class FactoryTemplates {
    const string Compressor = "anode.compressor";
    const string Reverb = "anode.reverb";
    const string Delay = "anode.delay-designer";
    const string Eq = "anode.channel-eq";
    const double SendDb = -12;

    public static IReadOnlyList<FactoryTemplate> All { get; } = [
        new("factory-empty", "Empty Project", "General", "No tracks.", static _ => { }),
        new(
            "factory-songwriter",
            "Songwriter",
            "Songwriting",
            "Vocal, guitar, keys and bass, sharing a reverb.",
            static b => {
                var bus = b.Aux("Reverb", Reverb);
                var vocal = b.Audio("Vocal", false, Compressor);
                b.Send(vocal, bus);
                b.Send(b.Audio("Guitar", true), bus);
                b.Send(b.Instrument("Keys", PluginCatalog.DefaultInstrumentId), bus);
                b.Instrument("Bass", PluginCatalog.DefaultInstrumentId);
            }
        ),
        new(
            "factory-band",
            "Band Recording",
            "Recording",
            "A stack of drum tracks, bass, guitars and vocals, sharing a reverb.",
            static b => {
                var bus = b.Aux("Room", Reverb);
                b.Stack(
                    "Drums",
                    b.Audio("Kick", false),
                    b.Audio("Snare", false),
                    b.Audio("Overheads", true)
                );
                b.Audio("Bass", false, Compressor);
                b.Send(b.Audio("Guitar L", false), bus);
                b.Send(b.Audio("Guitar R", false), bus);
                b.Send(b.Audio("Lead Vocal", false, Compressor), bus);
            }
        ),
        new(
            "factory-electronic",
            "Electronic Production",
            "Production",
            "Instrument tracks with a delay and a reverb on sends.",
            static b => {
                var reverb = b.Aux("Reverb", Reverb);
                var delay = b.Aux("Delay", Delay);
                b.Instrument("Drums", PluginCatalog.DefaultInstrumentId);
                b.Instrument("Bass", PluginCatalog.DefaultInstrumentId);
                var lead = b.Instrument("Lead", PluginCatalog.DefaultInstrumentId);
                b.Send(lead, reverb);
                b.Send(lead, delay);
                b.Send(b.Instrument("Pad", PluginCatalog.DefaultInstrumentId), reverb);
            }
        ),
        new(
            "factory-voiceover",
            "Voiceover",
            "Recording",
            "A voice track with EQ and compression, and room tone.",
            static b => {
                b.Audio("Voice", false, Eq, Compressor);
                b.Audio("Room Tone", true);
            }
        )
    ];

    public static FactoryTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary><paramref name="template" />'s tracks on a new project. A plugin Anode lacks refuses it.</summary>
    public static ProjectState Build(
        FactoryTemplate template,
        ProjectState state,
        IIdSource ids,
        PluginCatalog plugins
    ) {
        var builder = new Builder(state, ids, plugins);
        template.Make(builder);
        return builder.State;
    }

    /// <summary>A template being made, a command at a time.</summary>
    public sealed class Builder(ProjectState state, IIdSource ids, PluginCatalog plugins) {
        int nextBus = 1;

        public ProjectState State { get; private set; } = state;

        void Do(EditCommand command) => State = Edits.Apply(State, command, ids, plugins).State;

        string Last => State.Tracks[^1].Id;

        public string Audio(string name, bool stereo, params string[] inserts) {
            Do(new AddTrackCommand(name, Stereo: stereo));
            var id = Last;
            foreach (var insert in inserts) {
                Do(new AddInsertCommand(insert, id));
            }

            return id;
        }

        public string Instrument(string name, string plugin) {
            Do(new AddTrackCommand(name, Kind: TrackKind.Instrument, PluginId: plugin));
            return Last;
        }

        /// <summary>An aux on the next bus with an effect on it; the bus.</summary>
        public int Aux(string name, string plugin) {
            var bus = nextBus++;
            Do(new AddAuxCommand(bus, name));
            Do(new AddInsertCommand(plugin, Last));
            return bus;
        }

        public void Send(string track, int bus) => Do(new AddSendCommand(track, bus, SendDb));

        public void Stack(string name, params string[] tracks) => Do(new CreateStackCommand(tracks, name));
    }
}
