namespace Anode.Engine.Recording;

/// <summary>
///     The writer thread: every few milliseconds it drains the active takes' rings into their files, so the audio
///     thread never touches a file. The control thread swaps the set of takes; <see cref="Retire" /> waits until no
///     pass can still be using the previous set.
/// </summary>
public sealed class RecordWriter : IDisposable {
    static readonly RecordTake[] None = [];
    static readonly FlashbackBuffer[] NoMemory = [];

    readonly float[] scratch = new float[64 * 1024];
    readonly Thread thread;
    RecordTake[] active = None;
    FlashbackBuffer[] memories = NoMemory;
    long passesStarted;
    long passesCompleted;
    volatile bool running = true;
    Exception? failure;

    public RecordWriter() {
        thread = new(Run) { IsBackground = true, Name = "anode record writer", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>The first exception a drain threw (a full disk), taken by the control thread.</summary>
    public Exception? TakeFailure() => Interlocked.Exchange(ref failure, null);

    /// <summary>Control thread: starts draining <paramref name="takes" />.</summary>
    public void Activate(RecordTake[] takes) => Volatile.Write(ref active, takes);

    /// <summary>
    ///     Control thread: the Flashback buffers whose pages the writer faults in ahead of the audio thread
    ///     (<see cref="FlashbackBuffer.Touch" />); returns once it has let go of any it no longer lists.
    /// </summary>
    public void Touch(FlashbackBuffer[] buffers) {
        Volatile.Write(ref memories, buffers);
        WaitPass();
    }

    /// <summary>Control thread: stops draining every take and waits until the writer has let go of them.</summary>
    public void Retire() {
        Volatile.Write(ref active, None);
        WaitPass();
    }

    void WaitPass() {
        var grace = Interlocked.Read(ref passesStarted);
        var spin = new SpinWait();
        while (Interlocked.Read(ref passesCompleted) < grace && running) {
            spin.SpinOnce(-1);
        }
    }

    public void Dispose() {
        running = false;
        thread.Join();
    }

    void Run() {
        while (running) {
            Interlocked.Increment(ref passesStarted);
            var takes = Volatile.Read(ref active);
            foreach (var take in takes) {
                try {
                    take.Drain(scratch);
                } catch (IOException e) {
                    Interlocked.CompareExchange(ref failure, e, null);
                }
            }

            foreach (var memory in Volatile.Read(ref memories)) {
                memory.Touch();
            }

            Interlocked.Increment(ref passesCompleted);
            Thread.Sleep(5);
        }
    }
}
