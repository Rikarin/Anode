using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Anode.Ipc.Segments;

/// <summary>
///     The segment's optional checksum: four interleaved CRC32C lanes over 64-bit words (hardware CRC on arm64 and
///     x64), folded into 64 bits. It detects a torn or stale segment, not tampering. Lengths are multiples of 64.
/// </summary>
public static unsafe class Checksum {
    public static ulong Compute(byte* data, long length) {
        var words = (ulong*)data;
        var count = length / 8;
        uint a = 0xFFFFFFFF, b = 0xFFFFFFFF, c = 0xFFFFFFFF, d = 0xFFFFFFFF;
        long i = 0;
        if (Crc32.Arm64.IsSupported) {
            for (; i + 4 <= count; i += 4) {
                a = Crc32.Arm64.ComputeCrc32C(a, words[i]);
                b = Crc32.Arm64.ComputeCrc32C(b, words[i + 1]);
                c = Crc32.Arm64.ComputeCrc32C(c, words[i + 2]);
                d = Crc32.Arm64.ComputeCrc32C(d, words[i + 3]);
            }
        } else if (Sse42.X64.IsSupported) {
            for (; i + 4 <= count; i += 4) {
                a = (uint)Sse42.X64.Crc32(a, words[i]);
                b = (uint)Sse42.X64.Crc32(b, words[i + 1]);
                c = (uint)Sse42.X64.Crc32(c, words[i + 2]);
                d = (uint)Sse42.X64.Crc32(d, words[i + 3]);
            }
        }

        // No hardware CRC (or a tail): a multiply-xor mix, still deterministic on both sides.
        ulong rest = 0;
        for (; i < count; i++) {
            rest = (rest ^ words[i]) * 0x9E3779B97F4A7C15UL;
        }

        return ((ulong)(a ^ c) << 32 | (b ^ d)) ^ rest;
    }
}
