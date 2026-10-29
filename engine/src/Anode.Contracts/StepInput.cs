namespace Anode.Contracts;

/// <summary>
///     Step input (docs/08-phase-7.md, "Step input"), held by the server outside the project: it is not saved and not
///     undoable. While <c>on</c>, steps go into MIDI region <c>regionId</c>, or, when it is null, into a new region
///     the first step makes on instrument or external MIDI track <c>trackId</c> (from the step position, a bar long,
///     lengthened as steps pass its end). <c>positionTick</c> is the next step's project tick. A step lasts
///     <c>noteValueTicks</c> (a 1/16 is 240), × 1.5 when <c>dotted</c>, × 2/3 when <c>triplet</c>, at velocity
///     <c>velocity</c> (1…127). With <c>chord</c> on, steps stack at the position without advancing; turning it off
///     advances by one step when notes were stacked. <c>midiInput</c>: the host forwards the MIDI keyboard's notes as
///     steps (held keys make a chord). Changes reach every client as <c>stepInput</c> events on <c>/ws</c>.
/// </summary>
public sealed record StepInputState(
    bool On,
    string? RegionId,
    string? TrackId,
    long PositionTick,
    long NoteValueTicks,
    bool Dotted,
    bool Triplet,
    int Velocity,
    bool Chord,
    bool MidiInput
);

/// <summary>
///     Changes the step input state (<c>PUT /project/step-input</c>); left-out properties stay. <c>regionId</c> empty
///     clears it (steps then make a region on <c>trackId</c>).
/// </summary>
public sealed record StepInputRequest(
    bool? On = null,
    string? RegionId = null,
    string? TrackId = null,
    long? PositionTick = null,
    long? NoteValueTicks = null,
    bool? Dotted = null,
    bool? Triplet = null,
    int? Velocity = null,
    bool? Chord = null,
    bool? MidiInput = null
);
