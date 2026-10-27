using Anode.Spikes.DiskStream;

// Spike S2 harness (docs/spikes/S2-disk-streaming.md). Modes:
//   generate --dir <dir> [--tracks 128] [--min-seconds 120] [--max-seconds 300] [--nocache]
//   cache    --dir <dir> [--tracks 128] [--evict]
//   devices
//   run      --dir <dir> [--tracks 128] [--buffer 64] [--clock null|device] [--device <uid>] [--cycle]
//            [--cycle-start-bar 5] [--cycle-bars 8] [--bpm 120] [--ring-seconds 2] [--read-kb 96] [--threads 1]
//            [--prewarm] [--nocache] [--evict] [--io-hog] [--no-rt] [--warmup 5] [--seconds 40] [--label x] [--results file.jsonl]
//            self-tests: [--naive-cycle] (must report position errors) [--alloc-in-callback] (must report allocation)

var options = Options.Parse(args.Skip(1));
return args.FirstOrDefault() switch {
    "generate" => WavFile.Generate(options),
    "cache" => RunMode.Cache(options),
    "devices" => RunMode.Devices(),
    "run" => RunMode.Run(options),
    _ => Usage()
};

static int Usage() {
    Console.Error.WriteLine("usage: Anode.Spikes.DiskStream generate|cache|devices|run [options] (see Program.cs)");
    return 2;
}
