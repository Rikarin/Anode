using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Anode.Engine.Plugins.Clap;

/// <summary>A plugin a CLAP file offers, as its descriptor tells.</summary>
public sealed record ClapDescriptor(
    string Id,
    string Name,
    string Vendor,
    string Version,
    IReadOnlyList<string> Features
) {
    public bool IsInstrument => Features.Contains("instrument");
}

/// <summary>
///     A loaded CLAP file: the library, its <c>clap_entry</c> (initialised once) and its plugin factory. Modules stay
///     loaded for the life of the process (unloading plugin libraries is a classic source of crashes), one per path.
///     Main-thread code: only the plugin thread calls it.
/// </summary>
sealed unsafe partial class ClapModule {
    static readonly Dictionary<string, ClapModule> Loaded = new(StringComparer.Ordinal);

    readonly ClapPluginEntry* entry;

    ClapModule(string path, ClapPluginEntry* entry, ClapPluginFactory* factory) {
        Path = path;
        this.entry = entry;
        Factory = factory;
        var list = new List<ClapDescriptor>();
        var count = factory->GetPluginCount(factory);
        for (var i = 0u; i < count; i++) {
            var d = factory->GetPluginDescriptor(factory, i);
            if (d is null || d->Id is null) {
                continue;
            }

            var features = new List<string>();
            for (var f = d->Features; f is not null && *f is not null; f++) {
                features.Add(Text(*f));
            }

            list.Add(new(Text(d->Id), Text(d->Name), Text(d->Vendor), Text(d->PluginVersion), features));
        }

        Descriptors = list;
    }

    /// <summary>The <c>.clap</c> file or bundle.</summary>
    public string Path { get; }

    public ClapPluginFactory* Factory { get; }

    public IReadOnlyList<ClapDescriptor> Descriptors { get; }

    /// <summary>
    ///     Loads (or returns the already loaded) module at <paramref name="path" />: a <c>.clap</c> shared library, or on
    ///     macOS a <c>.clap</c> bundle. Throws <see cref="IOException" /> when it is not a usable CLAP.
    /// </summary>
    public static ClapModule Load(string path) {
        path = System.IO.Path.GetFullPath(path);
        if (Loaded.TryGetValue(path, out var known)) {
            return known;
        }

        var binary = BinaryOf(path);
        if (!NativeLibrary.TryLoad(binary, out var library)) {
            throw new IOException($"{path}: cannot load {binary}.");
        }

        if (!NativeLibrary.TryGetExport(library, "clap_entry", out var symbol)) {
            throw new IOException($"{path}: no clap_entry.");
        }

        var entry = (ClapPluginEntry*)symbol;
        if (entry->Version.Major < 1) {
            throw new IOException($"{path}: CLAP {entry->Version.Major}.{entry->Version.Minor} is not supported.");
        }

        var utf8 = Utf8(path);
        try {
            if (entry->Init(utf8) == 0) {
                throw new IOException($"{path}: clap_entry.init failed.");
            }
        } finally {
            NativeMemory.Free(utf8);
        }

        var id = Utf8("clap.plugin-factory");
        ClapPluginFactory* factory;
        try {
            factory = (ClapPluginFactory*)entry->GetFactory(id);
        } finally {
            NativeMemory.Free(id);
        }

        if (factory is null) {
            entry->Deinit();
            throw new IOException($"{path}: no plugin factory.");
        }

        var module = new ClapModule(path, entry, factory);
        Loaded[path] = module;
        return module;
    }

    /// <summary>The shared library inside a macOS bundle (its <c>CFBundleExecutable</c>), else the path itself.</summary>
    static string BinaryOf(string path) {
        if (!Directory.Exists(path)) {
            return path;
        }

        var contents = System.IO.Path.Combine(path, "Contents");
        var plist = System.IO.Path.Combine(contents, "Info.plist");
        if (File.Exists(plist) && ExecutableKey().Match(File.ReadAllText(plist)) is { Success: true } match) {
            var named = System.IO.Path.Combine(contents, "MacOS", match.Groups[1].Value.Trim());
            if (File.Exists(named)) {
                return named;
            }
        }

        var folder = System.IO.Path.Combine(contents, "MacOS");
        var guess = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(path));
        if (File.Exists(guess)) {
            return guess;
        }

        return Directory.Exists(folder) && Directory.GetFiles(folder) is [var only, ..]
            ? only
            : throw new IOException($"{path}: a bundle without a binary.");
    }

    [GeneratedRegex(@"<key>CFBundleExecutable</key>\s*<string>([^<]+)</string>")]
    private static partial Regex ExecutableKey();

    public static string Text(byte* text) =>
        text is null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    /// <summary>A NUL-terminated UTF-8 copy in native memory (free with <see cref="NativeMemory.Free" />).</summary>
    public static byte* Utf8(string text) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var copy = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(copy, bytes.Length));
        copy[bytes.Length] = 0;
        return copy;
    }
}
