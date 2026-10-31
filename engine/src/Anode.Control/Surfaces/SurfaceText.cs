using System.Globalization;
using System.Text;

namespace Anode.Control.Surfaces;

/// <summary>Text on a surface's displays: printable ASCII, names abbreviated and values fitted to a width.</summary>
static class SurfaceText {
    /// <summary>
    ///     A name in <paramref name="width" /> characters, padded right: spaces go first, then lowercase vowels from the
    ///     end (never the first letter), then it is cut; a trailing number is kept whole ("Track 10" is "Trck10" in 6 and
    ///     "Tr10" in 4).
    /// </summary>
    public static string Name(string? name, int width) {
        var text = Ascii(name ?? string.Empty).Trim();
        if (text.Length > width) {
            text = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        if (text.Length <= width) {
            return text.PadRight(width);
        }

        var digits = text.Length - text.TrimEnd("0123456789".ToCharArray()).Length;
        return digits > 0 && digits < width && digits < text.Length
            ? Squeeze(text[..^digits], width - digits) + text[^digits..]
            : Squeeze(text, width);
    }

    static string Squeeze(string text, int width) {
        var builder = new StringBuilder(text);
        for (var i = builder.Length - 1; i > 0 && builder.Length > width; i--) {
            if ("aeiou".Contains(builder[i], StringComparison.Ordinal)) {
                builder.Remove(i, 1);
            }
        }

        return Fit(builder.ToString(), width).PadRight(width);
    }

    /// <summary>
    ///     A value in <paramref name="width" /> characters, padded left: spaces go first, then a trailing unit ("−12.0
    ///     dB" is "-12.0"), then it is cut.
    /// </summary>
    public static string Value(string? value, int width) {
        var text = Ascii(value ?? string.Empty).Trim();
        if (text.Length > width) {
            text = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        if (text.Length > width) {
            text = text.TrimEnd("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ%".ToCharArray());
        }

        return Fit(text, width).PadLeft(width);
    }

    /// <summary>A pan −1…1 as text: "C", "L50" or "R50".</summary>
    public static string Pan(double pan) {
        var percent = Round(Math.Abs(pan) * 100);
        if (percent == 0) {
            return "C";
        }

        var side = pan < 0 ? "L" : "R";
        return side + percent.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Rounds half away from zero (as JavaScript's Math.round does for the positive values surfaces show).</summary>
    public static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    static string Fit(string text, int width) => text.Length > width ? text[..width] : text;

    /// <summary>Printable ASCII: the minus sign as '-', anything else outside 0x20…0x7E as '?'.</summary>
    static string Ascii(string text) {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) {
            builder.Append(
                c switch {
                    '−' or '–' => '-',
                    >= ' ' and <= '~' => c,
                    _ => '?'
                }
            );
        }

        return builder.ToString();
    }
}

/// <summary>A byte stream from a surface split into whole messages (running status, SysEx; real-time bytes dropped).</summary>
static class MidiStream {
    public static List<byte[]> Split(ReadOnlySpan<byte> data) {
        var messages = new List<byte[]>();
        var status = 0;
        var i = 0;
        while (i >= 0 && i < data.Length) {
            i = Step(data, i, ref status, messages);
        }

        return messages;
    }

    /// <summary>Reads at <paramref name="i" />: the index after what it read, or −1 at a message the data cuts off.</summary>
    static int Step(ReadOnlySpan<byte> data, int i, ref int status, List<byte[]> messages) {
        var b = data[i];
        if (b >= 0xF8) {
            return i + 1; // Real time.
        }

        if (b == 0xF0) {
            status = 0;
            var end = data[i..].IndexOf((byte)0xF7);
            if (end < 0) {
                return -1;
            }

            messages.Add([..data.Slice(i, end + 1)]);
            return i + end + 1;
        }

        if (b >= 0x80) {
            status = b < 0xF0 ? b : 0; // System common is skipped, its data bytes with it.
            return i + 1;
        }

        return status == 0 ? i + 1 : ChannelData(data, i, status, messages);
    }

    static int ChannelData(ReadOnlySpan<byte> data, int i, int status, List<byte[]> messages) {
        var count = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
        if (i + count > data.Length) {
            return -1;
        }

        var bytes = data.Slice(i, count);
        var cut = bytes.IndexOfAnyInRange((byte)0x80, (byte)0xFF);
        if (cut >= 0) {
            return i + cut; // Cut short by the next status: dropped, and the status is read next.
        }

        var message = new byte[count + 1];
        message[0] = (byte)status;
        bytes.CopyTo(message.AsSpan(1));
        messages.Add(message);
        return i + count;
    }
}
