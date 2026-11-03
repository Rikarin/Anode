using Anode.Contracts;

namespace Anode.Interchange.Adm;

/// <summary>
///     What an ADM BWF holds, as Anode writes and reads it (docs/11-phase-10.md, ITU-R BS.2076, the EBU ADM production
///     profile): one programme and content of beds and objects over <c>Frames</c> samples at <c>SampleRate</c>.
/// </summary>
public sealed record AdmScene(
    string Name,
    int SampleRate,
    long Frames,
    IReadOnlyList<AdmBed> Beds,
    IReadOnlyList<AdmObject> Objects
);

/// <summary>
///     A bed: <c>DirectSpeakers</c> channels in <c>Format</c>'s layout order (ADM speaker labels as the layout's), from
///     the interleaved WAV <c>AudioPath</c> of that many channels (the writer's input, the reader's extracted file).
/// </summary>
public sealed record AdmBed(string Name, ChannelFormat Format, string AudioPath);

/// <summary>
///     An object: <c>Objects</c> channels (one, or two for a stereo object, each with the same blocks) from the WAV
///     <c>AudioPath</c>, with its position over time as <c>audioBlockFormat</c>s.
/// </summary>
public sealed record AdmObject(string Name, string AudioPath, int Channels, IReadOnlyList<AdmBlock> Blocks);

/// <summary>
///     An <c>audioBlockFormat</c> of an object: from <c>StartFrame</c> for <c>DurationFrames</c>, at a polar position
///     (degrees as the object panner's, distance and size 0…1); with <c>Interpolate</c>, the position moves linearly from
///     the previous block's over the whole block (<c>jumpPosition</c> off), else it jumps at the start. Blocks are
///     contiguous from 0 and at most 20 ms long where the position moves.
/// </summary>
public sealed record AdmBlock(
    long StartFrame,
    long DurationFrames,
    double Azimuth,
    double Elevation,
    double Distance,
    double Size,
    bool Interpolate
);
