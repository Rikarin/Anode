namespace Anode.Interchange.Aaf.Cfb;

/// <summary>
///     The constants of the Microsoft Compound File Binary format ([MS-CFB]): the header's fields, the special sector
///     numbers, the directory entry layout and the ordering of sibling names.
/// </summary>
static class CfbFormat {
    public const uint MaxRegularSector = 0xFFFFFFFA;
    public const uint DifatSector = 0xFFFFFFFC;
    public const uint FatSector = 0xFFFFFFFD;
    public const uint EndOfChain = 0xFFFFFFFE;
    public const uint FreeSector = 0xFFFFFFFF;
    public const uint NoStream = 0xFFFFFFFF;
    public const int HeaderSize = 512;
    public const int HeaderDifatCount = 109;
    public const int DirectoryEntrySize = 128;
    public const int MiniSectorSize = 64;
    public const int MiniStreamCutoff = 4096;
    public const int MaxNameLength = 31;

    public static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>
    ///     The order of siblings in a storage's red-black tree: a shorter name first, then by code unit after simple
    ///     upper-casing ([MS-CFB] 2.6.4).
    /// </summary>
    public static int Compare(string a, string b) {
        if (a.Length != b.Length) {
            return a.Length.CompareTo(b.Length);
        }

        for (var i = 0; i < a.Length; i++) {
            var x = char.ToUpperInvariant(a[i]);
            var y = char.ToUpperInvariant(b[i]);
            if (x != y) {
                return x.CompareTo(y);
            }
        }

        return 0;
    }
}

/// <summary>The kind of a directory entry.</summary>
enum CfbEntryType : byte {
    Unused = 0,
    Storage = 1,
    Stream = 2,
    Root = 5
}
