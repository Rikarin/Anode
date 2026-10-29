using Anode.ContentBuilder;
using Anode.Contracts;
using Anode.Dsp.Instruments;

// anode-content-builder [--test] [--out <file.anodepack>] [--t1]
// Renders Anode Essentials (or, with --test, the small e2e test pack) from Anode's own instruments. Drums use the Drum
// Synth (anode.drum-synth) when this build has it, else (or with --t1) the T1 synth (docs/07-phase-6.md).
var test = args.Contains("--test");
var t1 = args.Contains("--t1") || BuiltinInstruments.Create(Content.DrumSynthId) is null;
var outIndex = Array.IndexOf(args, "--out");
var output = outIndex >= 0 && outIndex + 1 < args.Length
    ? args[outIndex + 1]
    : Path.Combine("artifacts", "content", test ? "TestPack.anodepack" : "AnodeEssentials.anodepack");
var staging = Path.Combine(Path.GetTempPath(), $"anode-content-{Guid.NewGuid():n}");
try {
    var content = new Content(t1);
    var pack = test ? content.TestPack(staging) : content.Essentials(staging);
    pack.Finish(output);
    Console.WriteLine(
        $"Wrote {Path.GetFullPath(output)}: {pack.Written.Count} files, drums from "
        + (t1 ? "the T1 synth" : "the Drum Synth")
        + "."
    );
} finally {
    if (Directory.Exists(staging)) {
        Directory.Delete(staging, true);
    }
}
