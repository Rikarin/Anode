using Anode.Plugins.Sandbox;

// anode-plugin: the plugin sandbox (docs/adr/0003-plugin-hosting.md). Started by Anode.Plugins' PluginHost, never by
// hand:
//   anode-plugin host --shm NAME   hosts plugins in the shared segment NAME, controlled over stdin/stdout
//   anode-plugin scan ITEM...      describes plugins for the catalogue (vst3:PATH, au:ID, au:*, clap:PATH)
if (args.Length >= 3 && args[0] == "host" && args[1] == "--shm") {
    return SandboxServer.Run(args[2]);
}

if (args.Length >= 1 && args[0] == "scan") {
    return SandboxServer.Scan(args[1..]);
}

Console.Error.WriteLine("usage: anode-plugin host --shm NAME | anode-plugin scan ITEM...");
return 2;
