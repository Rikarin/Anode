using Anode.Plugins.Native;
using System.Diagnostics;
using static Anode.Plugins.Tests.PluginFixtures;

namespace Anode.Plugins.Tests;

/// <summary>
///     Spike S6 (docs/spikes/S6-plugin-windows.md): the sandbox opens plugin editors in top-level windows. These put
///     real windows on the screen for a moment, so they run only with <c>ANODE_TEST_WINDOWS=1</c> (macOS).
/// </summary>
public sealed class EditorWindowTests : IDisposable {
    readonly TempDirectory data = new();

    public void Dispose() => data.Dispose();

    static void RequireWindows() =>
        Assert.SkipUnless(
            OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("ANODE_TEST_WINDOWS") == "1",
            "opens windows on the screen: set ANODE_TEST_WINDOWS=1 (macOS)"
        );

    static (int Pid, int X, int Y, int Width, int Height, int Layer, bool OnScreen)? Info(long window) =>
        PlugNative.WindowInfo(
            window,
            out var pid,
            out var x,
            out var y,
            out var w,
            out var h,
            out var layer,
            out var on
        )
        == 0
            ? (pid, x, y, w, h, layer, on == 1)
            : null;

    static void Settle() => Thread.Sleep(300); // the window server applies orders asynchronously

    [Fact]
    public void A_VST3_editor_opens_in_a_floating_panel_owned_by_the_sandbox() {
        RequireWindows();
        using var host = Host(data);
        using var gain = host.CreateEffect(GainId, new(1, "a"))!;
        gain.Prepare(48000, 512);
        var hosted = (IHostedPlugin)gain;
        Assert.True(hosted.HasEditor);

        var watch = Stopwatch.StartNew();
        var window = hosted.OpenEditor(true, new(0, 120, 140));
        var opened = watch.Elapsed;
        Assert.True(window > 0);
        Settle();
        var info = Info(window)!.Value;
        Log($"S6 floating VST3 editor: window {window}, opened in {opened.TotalMilliseconds:0} ms, {info}");
        Assert.Equal(hosted.Stats.SandboxPid, info.Pid);
        Assert.True(info.OnScreen);
        Assert.Equal(3, info.Layer); // kCGFloatingWindowLevel
        Assert.Equal(320, info.Width);
        Assert.InRange(info.Height, 110, 110 + 40); // the content plus a utility title bar
        Assert.Equal((120, 140), (info.X, info.Y));
        Assert.Equal(window, hosted.EditorWindow);

        // Opening it again moves it rather than opening a second one.
        Assert.Equal(window, hosted.OpenEditor(true, new(0, 220, 240)));
        Settle();
        Assert.Equal((220, 240), (Info(window)!.Value.X, Info(window)!.Value.Y));

        Assert.Equal(0, hosted.OpenEditor(false));
        Settle();
        Assert.False(Info(window) is { OnScreen: true });
    }

    [Fact]
    public void An_editor_is_ordered_directly_above_a_window_of_another_process() {
        RequireWindows();
        // Two processes, as the desktop app (Electron) and a sandbox are: B's editor goes over A's window.
        using var host = Host(data, SandboxMode.PerInstance);
        using var a = host.CreateEffect(GainId, new(1, "a"))!;
        using var b = host.CreateEffect(GainId, new(2, "b"))!;
        a.Prepare(48000, 512);
        b.Prepare(48000, 512);
        var parent = ((IHostedPlugin)a).OpenEditor(true, new(0, 300, 200, Floating: false));
        Settle();
        var child = ((IHostedPlugin)b).OpenEditor(true, new(parent, Floating: false));
        Settle();
        var p = Info(parent)!.Value;
        var c = Info(child)!.Value;
        var zParent = PlugNative.WindowZ(parent);
        var zChild = PlugNative.WindowZ(child);
        Log($"S6 cross-process order: parent {parent} {p} z={zParent}; child {child} {c} z={zChild}");
        Assert.NotEqual(p.Pid, c.Pid);
        Assert.Equal(0, c.Layer);
        Assert.Equal(zParent - 1, zChild); // directly above
        Assert.Equal((p.X + 48, p.Y + 72), (c.X, c.Y)); // over the parent's top-left corner

        // The app's window by process: the sandbox finds the parent itself (here: A's sandbox as "the app").
        Assert.Equal(parent, PlugNative.AppWindow(p.Pid));
        ((IHostedPlugin)b).OpenEditor(false);
        ((IHostedPlugin)a).OpenEditor(false);
    }

    [Theory]
    [InlineData("au:aufx.dely.appl")] // its own Cocoa view
    [InlineData("au:aufx.rvb2.appl")] // no view of its own: Apple's generic view
    public void An_Audio_Unit_editor_opens(string pluginId) {
        RequireWindows();
        var options = Options(data.Path);
        options.ScanAudioUnits = true;
        options.Vst3Folders = [];
        using var host = new PluginHost(options);
        host.Catalog.Scan(false, TestContext.Current.CancellationToken);
        using var au = host.CreateEffect(pluginId, new(1, "a"))!;
        au.Prepare(48000, 512);
        var watch = Stopwatch.StartNew();
        var window = ((IHostedPlugin)au).OpenEditor(true, new(0, 160, 160));
        var opened = watch.Elapsed;
        Assert.True(window > 0);
        Settle();
        var info = Info(window)!.Value;
        Log($"S6 AU editor {pluginId}: window {window}, opened in {opened.TotalMilliseconds:0} ms, {info}");
        Assert.True(info.OnScreen);
        Assert.True(info.Width > 50 && info.Height > 50);
        ((IHostedPlugin)au).OpenEditor(false);
    }

    [Fact]
    public void A_sandbox_crash_takes_its_editor_windows_with_it() {
        RequireWindows();
        using var host = Host(data);
        using var gain = host.CreateEffect(GainId, new(1, "a"))!;
        using var crasher = host.CreateEffect(MisbehaveId, new(1, "b"))!;
        gain.Prepare(48000, 512);
        crasher.Prepare(48000, 512);
        var window = ((IHostedPlugin)gain).OpenEditor(true, new(0, 100, 100));
        Settle();
        Assert.True(Info(window)!.Value.OnScreen);
        crasher.SetParameter(0, 1f);
        var (l, r) = Signal(64);
        host.Processing = ProcessingMode.Offline;
        crasher.Process(l, r, [], []);
        SpinWait.SpinUntil(() => ((IHostedPlugin)gain).IsFailed, TimeSpan.FromSeconds(5));
        Settle();
        Assert.False(Info(window) is { OnScreen: true });
    }
}
