using System.Runtime.InteropServices;

namespace Anode.Spikes.DiskStream;

/// <summary>
///     Competing disk load: another thread reading the test files back to back in 1 MiB uncached reads, as fast as the
///     disk allows (a sample library loading, a backup, a bounce of another project).
/// </summary>
sealed unsafe class IoHog : IDisposable {
    const int Chunk = 1 << 20;
    readonly string[] paths;
    readonly Thread thread;
    volatile bool running = true;
    long bytes;

    public IoHog(string[] paths) {
        this.paths = paths;
        thread = new(Run) { IsBackground = true, Name = "io hog" };
        thread.Start();
    }

    public long Bytes => Interlocked.Read(ref bytes);

    void Run() {
        var buffer = (byte*)NativeMemory.AlignedAlloc(Chunk, 4096);
        try {
            for (var i = paths.Length - 1; running; i = i == 0 ? paths.Length - 1 : i - 1) {
                using var handle = File.OpenHandle(paths[i]);
                Posix.SetNoCache(handle);
                long offset = 0;
                int n;
                while (running && (n = RandomAccess.Read(handle, new Span<byte>(buffer, Chunk), offset)) > 0) {
                    offset += n;
                    Interlocked.Add(ref bytes, n);
                }
            }
        } finally {
            NativeMemory.AlignedFree(buffer);
        }
    }

    public void Dispose() {
        running = false;
        thread.Join();
    }
}
