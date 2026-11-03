using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Anode.Dsp.Tests.Spatial;

/// <summary>
///     The EBU ADM Renderer (<c>ear</c>) for the opt-in comparisons: <c>python3 scripts/setup-ear.py</c> makes its
///     environment at <c>engine/artifacts/ear</c> (or <c>ANODE_EAR</c> names a python with it); tests skip without it.
/// </summary>
static class Ear {
    public static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "..", ".."));

    static string ThisFile([CallerFilePath] string path = "") => path;

    public static string Python() {
        var named = Environment.GetEnvironmentVariable("ANODE_EAR");
        var python = !string.IsNullOrEmpty(named)
            ? named
            : Path.Combine(
                RepositoryRoot(),
                "engine",
                "artifacts",
                "ear",
                OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"
            );
        Assert.SkipUnless(File.Exists(python), "No ear: run python3 scripts/setup-ear.py (or set ANODE_EAR).");
        return python;
    }

    /// <summary>Runs a script of <c>scripts/</c> with <paramref name="input" /> on its standard input; its output.</summary>
    public static string Run(string script, string input, params string[] arguments) {
        var start = new ProcessStartInfo(Python()) {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", script));
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => error.AppendLine(e.Data);
        process.BeginErrorReadLine();
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{script} failed:\n{error}");
        return output;
    }
}
