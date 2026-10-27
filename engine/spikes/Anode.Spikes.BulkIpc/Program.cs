using Anode.Spikes.BulkIpc;

// Spike S3 harness (docs/spikes/S3-bulk-ipc.md). Roles:
//   server [--sizes 10000,100000,1000000,5000000] [--swaps 100] [--warmup 3] [--tracks 128] [--frames 64]
//          [--rate 48000] [--dir <segment directory>]   spawns `host` and prints the results as Markdown
//   host   --socket <path> [--frames 64] [--rate 48000]   (started by `server`)

var options = Options.Parse(args.Skip(1));
return args.FirstOrDefault() switch {
    "server" => ServerRole.Run(options),
    "host" => HostRole.Run(
        options.Text("socket") ?? throw new InvalidOperationException("host needs --socket <path>."),
        options.Int("frames", 64),
        options.Int("rate", 48_000)
    ),
    _ => Usage()
};

static int Usage() {
    Console.Error.WriteLine("usage: Anode.Spikes.BulkIpc server [options] (see Program.cs)");
    return 2;
}
