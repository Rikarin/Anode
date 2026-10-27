using System.Collections.Concurrent;

namespace Anode.Engine.Plugins;

/// <summary>
///     Which thread is the audio thread, for hosted plugins that ask (CLAP's thread-check extension). The device's
///     callback thread marks itself on every callback; a thread-static flag costs nothing to read or write.
/// </summary>
public static class AudioThread {
    [ThreadStatic]
    static bool current;

    public static bool IsCurrent => current;

    /// <summary>Marks the calling thread as an audio thread (or not).</summary>
    public static void Mark(bool value = true) => current = value;
}

/// <summary>
///     The plugins' main thread (CLAP's "main thread"): one dedicated thread on which every main-thread call into a
///     hosted plugin is made (create, init, activate, state, parameters info, destroy, on_main_thread), whichever
///     control thread asked for it. It also services the callbacks plugins request (<see cref="Poll" />).
/// </summary>
public sealed class PluginThread : IDisposable {
    static readonly Lazy<PluginThread> Instance = new(static () => new());

    readonly BlockingCollection<Action> work = new();
    readonly Thread thread;
    readonly List<Action> pollers = [];

    PluginThread() {
        thread = new(Run) { IsBackground = true, Name = "anode plugins" };
        thread.Start();
    }

    /// <summary>The process's plugin main thread, started on first use.</summary>
    public static PluginThread Shared => Instance.Value;

    /// <summary>True on the plugin main thread.</summary>
    public bool IsCurrent => Thread.CurrentThread == thread;

    /// <summary>Runs <paramref name="action" /> on the plugin main thread and waits for it.</summary>
    public void Invoke(Action action) =>
        Invoke(() => {
                action();
                return 0;
            }
        );

    /// <summary>Runs <paramref name="function" /> on the plugin main thread and returns its result (or rethrows).</summary>
    public T Invoke<T>(Func<T> function) {
        if (IsCurrent) {
            return function();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() => {
                try {
                    done.SetResult(function());
                }
#pragma warning disable CA1031 // handed to the caller, which rethrows it on its own thread
                catch (Exception e) {
#pragma warning restore CA1031
                    done.SetException(e);
                }
            }
        );
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Adds work that runs on the plugin main thread about every 10 ms (plugins' requested callbacks).</summary>
    public void AddPoller(Action poll) {
        lock (pollers) {
            pollers.Add(poll);
        }
    }

    public void RemovePoller(Action poll) {
        lock (pollers) {
            pollers.Remove(poll);
        }
    }

    /// <summary>Runs the pollers now (on the plugin main thread).</summary>
    public void Poll() => Invoke(RunPollers);

    void Run() {
        while (true) {
            if (work.TryTake(out var item, 10)) {
                item();
            }

            RunPollers();
        }
    }

    void RunPollers() {
        Action[] snapshot;
        lock (pollers) {
            if (pollers.Count == 0) {
                return;
            }

            snapshot = [..pollers];
        }

        foreach (var poll in snapshot) {
            poll();
        }
    }

    public void Dispose() => work.Dispose();
}
