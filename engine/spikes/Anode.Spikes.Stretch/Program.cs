using Anode.Spikes.Stretch;
using System.Globalization;
using System.Runtime.InteropServices;

// Spike S5 harness (docs/spikes/S5-time-stretch.md):
//   Anode.Spikes.Stretch [--suite all|latency,quality,cpu,blocks,files] [--lib an_stretch_noaccel] [--out dir]
//                        [--no-wav] [file.wav ...]
// Prints Markdown tables and writes them to <out>/results-<lib>.md; renders go to <out>/<signal>/<config>/*.wav.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var suite = "all";
var library = "an_stretch";
var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/spike-stretch"));
var writeWav = true;
var files = new List<string>();
for (var i = 0; i < args.Length; i++) {
    switch (args[i]) {
        case "--suite": suite = args[++i]; break;
        case "--lib": library = args[++i]; break;
        case "--out": output = Path.GetFullPath(args[++i]); break;
        case "--no-wav": writeWav = false; break;
        default: files.Add(args[i]); break;
    }
}

if (library != "an_stretch") {
    Native.UseVariant(library);
}

Thread.CurrentThread.Priority = ThreadPriority.Highest;
var report = new Report();
report.Line($"# S5 results: {Native.VersionText}");
report.Line();
report.Line(
    $"- Library: `{library}`; {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, "
    + $"{Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}"
);
report.Line($"- Sample rate {Signals.Rate} Hz; quality renders stream in 128-frame output blocks.");
report.Line();

var suites = new Suites(report, output, writeWav);
if (Wants("latency")) {
    suites.Latency();
}

if (Wants("quality")) {
    suites.Quality();
}

if (Wants("cpu")) {
    suites.Cpu();
}

if (Wants("blocks")) {
    suites.Blocks();
}

if (files.Count > 0 && Wants("files")) {
    suites.Files(files);
}

Directory.CreateDirectory(output);
var resultsPath = Path.Combine(output, $"results-{library}.md");
File.WriteAllText(resultsPath, report.Text);
Console.Error.WriteLine($"results: {resultsPath}");
return 0;

bool Wants(string name) => suite == "all" || suite.Split(',').Contains(name);
