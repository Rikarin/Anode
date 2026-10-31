using Anode.Contracts;
using Anode.Control.Surfaces;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static Anode.Control.Tests.Views;

namespace Anode.Control.Tests;

/// <summary>
///     The surface vectors workstream B tests against (<c>McuVectors.json</c>, <c>HuiVectors.json</c>, rewritten with
///     <c>ANODE_UPDATE_VECTORS=1</c>): sessions of decode, encode, meter and poll steps on a <see cref="SurfaceGroup" />,
///     with what each step produced. The bytes are checked by hand in <see cref="MackieControlTests" /> and
///     <see cref="HuiTests" />; this file pins every byte of each session.
/// </summary>
public sealed class SurfaceVectorTests {
    const long Second = 1_000_000_000;

    const string About = "Control surface vectors (docs/09-phase-8.md, workstream D; docs/control/README.md): "
        + "Anode.Control computes these, and the server's surface sessions must reproduce them. Each session makes "
        + "fresh units of `units` (ControlSurfaces.Create), left to right in one SurfaceGroup, and runs its steps in "
        + "order. `op` is one of: `encode` (SurfaceGroup.Encode(views[view]); `out` is each unit's messages, in order, "
        + "as hex), `meters` (SurfaceGroup.EncodeMeters(views[view]); `out` likewise), `poll` "
        + "(SurfaceGroup.Poll(timeNs); `out` likewise), `decode` (SurfaceGroup.Decode(unit, bytes of `in`, timeNs); "
        + "`actions` are the SurfaceActions in order: kind as the camelCase SurfaceActionKind name, strip, trackId, "
        + "address, value (compare within 1e-9), automation as the AutomationMode JSON name), `invalidate` (every "
        + "unit's Invalidate()). After each step `bank` is the group's BankBase and `connected` each unit's Connected. "
        + "`views` are ProjectViews in the contracts' JSON (camelCase).";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    static readonly Dictionary<string, ProjectView> ViewTable = new() {
        ["sixteen"] = Tracks(16),
        ["sixteenEdited"] = With(
            With(Tracks(16), 0, t => t with { Fader = 0.75 }),
            1,
            t => t with { Mute = true, Name = "Bass" }
        ),
        ["sixteenPlaying"] = Playing(Tracks(16)),
        ["forty"] = Tracks(40)
    };

    sealed class Session(string name, params ControlSurfaceKind[] units) {
        public string Name { get; } = name;

        public ControlSurfaceKind[] Units { get; } = units;

        public List<(string Op, string? View, int Unit, string? In, long TimeNs)> Steps { get; } = [];

        public Session Encode(string view) => Add("encode", view);

        public Session Meters(string view) => Add("meters", view);

        public Session Poll(long timeNs) => Add("poll", timeNs: timeNs);

        public Session Invalidate() => Add("invalidate");

        public Session Decode(string hex, int unit = 0, long timeNs = 0) =>
            Add("decode", unit: unit, hex: hex, timeNs: timeNs);

        Session Add(string op, string? view = null, int unit = 0, string? hex = null, long timeNs = 0) {
            Steps.Add((op, view, unit, hex, timeNs));
            return this;
        }
    }

    static IEnumerable<Session> McuSessions() {
        const ControlSurfaceKind mc = ControlSurfaceKind.MackieControl, xt = ControlSurfaceKind.MackieControlXt;
        yield return new Session("handshake and full refresh", mc)
            .Poll(0)
            .Decode("F0 00 00 66 14 01 41 42 43 44 45 46 47 01 02 03 04 F7")
            .Poll(0)
            .Encode("sixteen")
            .Encode("sixteen")
            .Poll(5 * Second);
        yield return new Session("change-only encoding", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Encode("sixteenEdited")
            .Encode("sixteenEdited")
            .Invalidate()
            .Encode("sixteenEdited");
        yield return new Session("fader with touch", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Decode("90 68 7F")
            .Decode("E0 00 40")
            .Encode("sixteenEdited")
            .Decode("90 68 00")
            .Encode("sixteenEdited")
            .Decode("E7 7F 7F")
            .Decode("E8 7F 7F");
        yield return new Session("transport and navigation", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Decode("90 5E 7F")
            .Decode("90 5E 00")
            .Decode("90 5D 7F")
            .Decode("90 5F 7F")
            .Decode("90 56 7F")
            .Decode("90 5B 7F")
            .Decode("90 5B 00")
            .Decode("90 5C 7F")
            .Decode("80 5C 00")
            .Decode("B0 3C 01")
            .Decode("B0 3C 45")
            .Decode("90 60 7F")
            .Decode("90 61 7F")
            .Decode("90 62 7F")
            .Decode("90 63 7F")
            .Decode("90 64 7F")
            .Encode("sixteenPlaying")
            .Decode("90 64 7F");
        yield return new Session("strip buttons and automation", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Decode("90 00 7F")
            .Decode("90 0B 7F")
            .Decode("90 12 7F")
            .Decode("90 19 7F")
            .Decode("90 4A 7F")
            .Decode("90 4B 7F")
            .Decode("90 4D 7F")
            .Decode("90 4E 7F");
        yield return new Session("vpot pan and sends", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Decode("B0 10 01")
            .Decode("B0 13 43")
            .Decode("90 21 7F")
            .Decode("90 29 7F")
            .Decode("B0 10 01")
            .Decode("B0 11 01")
            .Decode("B0 12 01")
            .Encode("sixteen")
            .Decode("90 34 7F")
            .Encode("sixteen")
            .Decode("90 29 7F")
            .Decode("B0 10 01")
            .Encode("sixteen")
            .Decode("90 2A 7F")
            .Decode("90 34 7F")
            .Encode("sixteen");
        yield return new Session("plug-in paging", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Decode("90 2B 7F")
            .Encode("sixteen")
            .Decode("90 20 7F")
            .Encode("sixteen")
            .Decode("B0 10 01")
            .Decode("B0 17 41")
            .Decode("90 2F 7F")
            .Encode("sixteen")
            .Decode("B0 10 01")
            .Decode("B0 11 01")
            .Decode("B0 12 01")
            .Decode("90 30 7F")
            .Decode("B0 10 01")
            .Decode("90 2B 7F")
            .Decode("90 21 7F")
            .Encode("sixteen")
            .Decode("B0 12 01");
        yield return new Session("bank right shows tracks 9 to 16", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Decode("90 2F 7F")
            .Encode("sixteen")
            .Decode("E0 00 40")
            .Decode("90 2F 7F")
            .Decode("90 31 7F")
            .Decode("90 30 7F")
            .Decode("90 2E 7F")
            .Decode("90 2E 7F");
        yield return new Session("main unit and two extenders", mc, xt, xt)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Decode("F0 00 00 66 15 03 41 42 43 44 45 46 47 F7", 1)
            .Decode("F0 00 00 66 15 03 41 42 43 44 45 46 47 F7", 2)
            .Encode("forty")
            .Decode("90 2F 7F")
            .Encode("forty")
            .Decode("E0 00 40", 2)
            .Decode("90 18 7F", 1)
            .Decode("90 30 7F")
            .Decode("90 2F 7F", 1)
            .Encode("forty")
            .Decode("90 29 7F")
            .Decode("B0 10 01", 1)
            .Encode("forty");
        yield return new Session("meters and timecode while playing", mc)
            .Decode("F0 00 00 66 14 03 41 42 43 44 45 46 47 F7")
            .Encode("sixteen")
            .Encode("sixteenPlaying")
            .Meters("sixteenPlaying")
            .Meters("sixteenPlaying")
            .Meters("sixteen")
            .Meters("sixteen")
            .Decode("90 35 7F")
            .Encode("sixteenPlaying");
        yield return new Session("not connected", mc)
            .Encode("sixteen")
            .Poll(0)
            .Poll(Second)
            .Poll(2 * Second)
            .Decode("90 5E 7F")
            .Encode("sixteen")
            .Poll(4 * Second)
            .Decode("F0 00 00 66 14 0F F7")
            .Poll(5 * Second);
    }

    static IEnumerable<Session> HuiSessions() {
        const ControlSurfaceKind hui = ControlSurfaceKind.Hui;
        yield return new Session("ping and offline", hui)
            .Poll(0)
            .Decode("90 00 7F", timeNs: Second / 10)
            .Poll(Second / 2)
            .Poll(Second)
            .Decode("90 00 7F", timeNs: Second + 1)
            .Poll(2 * Second)
            .Poll(5 * Second)
            .Decode("90 00 00", timeNs: 5 * Second)
            .Poll(5 * Second);
        yield return new Session("full refresh and change-only", hui)
            .Decode("90 00 7F")
            .Encode("sixteen")
            .Encode("sixteen")
            .Encode("sixteenEdited")
            .Encode("sixteenPlaying");
        yield return new Session("fader with touch", hui)
            .Decode("90 00 7F")
            .Encode("sixteen")
            .Decode("B0 0F 00 B0 2F 40")
            .Decode("B0 00 40 B0 20 00")
            .Encode("sixteenEdited")
            .Decode("B0 0F 00 2F 00")
            .Encode("sixteenEdited");
        yield return new Session("switches", hui)
            .Decode("90 00 7F")
            .Encode("sixteen")
            .Decode("B0 0F 0E 2F 44")
            .Decode("B0 0F 0E 2F 04")
            .Decode("B0 0F 0E 2F 43")
            .Decode("B0 0F 0E 2F 45")
            .Decode("B0 0F 0E 2F 41")
            .Decode("B0 0F 0E 2F 01")
            .Decode("B0 0F 0E 2F 42")
            .Decode("B0 0F 0E 2F 02")
            .Decode("B0 0F 0F 2F 43")
            .Decode("B0 0F 0D 2F 40")
            .Decode("B0 0F 0D 2F 41")
            .Decode("B0 0F 0D 2F 43")
            .Decode("B0 0F 0D 2F 44")
            .Decode("B0 0F 0D 2F 42")
            .Decode("B0 0D 41")
            .Decode("B0 0F 00 2F 41")
            .Decode("B0 0F 01 2F 42")
            .Decode("B0 0F 02 2F 43")
            .Decode("B0 0F 03 2F 47")
            .Decode("B0 0F 18 2F 41")
            .Decode("B0 0F 18 2F 42")
            .Decode("B0 0F 18 2F 43")
            .Decode("B0 0F 18 2F 44")
            .Decode("B0 0F 18 2F 45");
        yield return new Session("banking", hui)
            .Decode("90 00 7F")
            .Encode("sixteen")
            .Decode("B0 0F 0A 2F 43")
            .Encode("sixteen")
            .Decode("B0 00 40 20 00")
            .Decode("B0 0F 0A 2F 42")
            .Decode("B0 0F 0A 2F 43")
            .Decode("B0 0F 0A 2F 41")
            .Decode("B0 0F 0A 2F 40");
        yield return new Session("vpots", hui)
            .Decode("90 00 7F")
            .Encode("sixteen")
            .Decode("B0 40 41")
            .Decode("B0 43 02")
            .Decode("B0 0F 01 2F 45")
            .Decode("B0 0F 0B 2F 47")
            .Decode("B0 40 41")
            .Decode("B0 41 41")
            .Encode("sixteen")
            .Decode("B0 0F 0B 2F 46")
            .Decode("B0 40 01")
            .Encode("sixteen")
            .Decode("B0 0F 0B 2F 42")
            .Encode("sixteen");
        yield return new Session("meters", hui)
            .Decode("90 00 7F")
            .Encode("sixteenPlaying")
            .Meters("sixteenPlaying")
            .Meters("sixteen")
            .Meters("sixteen");
    }

    [Fact]
    public void The_Mackie_Control_vectors_are_up_to_date() => Check("McuVectors.json", McuSessions());

    [Fact]
    public void The_HUI_vectors_are_up_to_date() => Check("HuiVectors.json", HuiSessions());

    static void Check(string file, IEnumerable<Session> sessions) {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var array = new JsonArray();
        foreach (var session in sessions) {
            array.Add(Run(session, used));
        }

        var views = new JsonObject();
        foreach (var name in used) {
            views[name] = JsonSerializer.SerializeToNode(ViewTable[name], Json);
        }

        var document = new JsonObject { ["about"] = About, ["views"] = views, ["sessions"] = array };
        var json = document.ToJsonString(
                new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
            )
            + "\n";
        var path = Path.Combine(Directory(), file);
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_VECTORS") == "1" || !File.Exists(path)) {
            File.WriteAllText(path, json);
        }

        Assert.Equal(json, File.ReadAllText(path));
    }

    static JsonObject Run(Session session, SortedSet<string> used) {
        var group = new SurfaceGroup([..session.Units.Select(ControlSurfaces.Create)]);
        var steps = new JsonArray();
        foreach (var (op, view, unit, hex, timeNs) in session.Steps) {
            var step = new JsonObject { ["op"] = op };
            switch (op) {
                case "encode":
                case "meters":
                    used.Add(view!);
                    step["view"] = view;
                    step["out"] = Out(
                        op == "encode" ? group.Encode(ViewTable[view!]) : group.EncodeMeters(ViewTable[view!])
                    );
                    break;
                case "poll":
                    step["timeNs"] = timeNs;
                    step["out"] = Out(group.Poll(timeNs));
                    break;
                case "invalidate":
                    foreach (var u in group.Units) {
                        u.Invalidate();
                    }

                    break;
                default:
                    step["unit"] = unit;
                    step["in"] = hex;
                    step["timeNs"] = timeNs;
                    step["actions"] = new JsonArray([..group.Decode(unit, Bytes(hex!), timeNs).Select(Action)]);
                    break;
            }

            step["bank"] = group.BankBase;
            step["connected"] = new JsonArray([..group.Units.Select(static u => (JsonNode)u.Connected)]);
            steps.Add(step);
        }

        return new() {
            ["name"] = session.Name,
            ["units"] = new JsonArray([..session.Units.Select(static k => JsonSerializer.SerializeToNode(k, Json))]),
            ["steps"] = steps
        };
    }

    static JsonArray Out(IReadOnlyList<IReadOnlyList<byte[]>> output) =>
        new([..output.Select(static unit => (JsonNode)new JsonArray([..unit.Select(static m => (JsonNode)Hex(m))]))]);

    static JsonNode Action(SurfaceAction action) =>
        new JsonObject {
            ["kind"] = JsonNamingPolicy.CamelCase.ConvertName(action.Kind.ToString()),
            ["strip"] = action.Strip,
            ["trackId"] = action.TrackId,
            ["address"] = action.Address,
            ["value"] = action.Value,
            ["automation"] = JsonSerializer.SerializeToNode(action.Automation, Json)
        };

    static string Directory([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
