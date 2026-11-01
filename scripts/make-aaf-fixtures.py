#!/usr/bin/env python3
# Writes the AAF import fixtures into engine/tests/Anode.Interchange.Tests/Aaf/Fixtures/ with pyaaf2 (an independent
# writer; MIT), for AafFixtureTests.cs, which checks the values below:
#
#   pyaaf2-embedded.aaf  48 kHz, 512-byte sectors (which pyaaf2 labels version 4), media embedded as PCM. Composition "Fixture"
#                        (usage code top level) with two sound slots and an event slot:
#                          "Dialog"  Filler 4800 | tone [1200, +6000) fade in 240 | Transition 960 (mono audio dissolve)
#                                    | gain ½ ( noise [0, +4000) ) | Filler 1000 | tone [0, +2400) fade out 480
#                                    → clips at 4800, 9840 and 14840
#                          "Music"   noise [2000, +6000) at 0
#                          markers   "Hit" at 9600 (a DescriptiveMarker), "Out" at 16000 for 480 (a CommentMarker)
#                        tone: mono 16-bit, 12 000 frames; noise: stereo 24-bit, 8 000 frames.
#   pyaaf2-linked.aaf    44.1 kHz, version 4, no usage code: one slot "Linked" with linked-tone [441, +22050) at 4410;
#                        the file source mob's network locator points at a path that does not exist, and the file,
#                        linked-tone.wav (mono 16-bit, 22 050 frames), lies beside the AAF.
#
# Usage (after scripts/setup-pyaaf2.py): engine/artifacts/pyaaf2/bin/python -I scripts/make-aaf-fixtures.py
import struct
import tempfile
import wave
from pathlib import Path

import aaf2
from aaf2.rational import AAFRational

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "engine" / "tests" / "Anode.Interchange.Tests" / "Aaf" / "Fixtures"
DISSOLVE = "0c3bea41-fc05-11d2-8a29-0050040ef7d2"
GAIN = "9d2ea894-0968-11d3-8a38-0050040ef7d2"
AMPLITUDE = "e4962321-2267-11d3-8a4c-0050040ef7d2"


def tone_sample(i):
    return ((i * 7) % 2000 - 1000) * 16


def noise_sample(i, channel):
    return ((i * (13 + channel * 5)) % 4001 - 2000) * 2048


def write_tone(path, rate, frames):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", tone_sample(i)) for i in range(frames)))


def write_noise(path, rate, frames):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(3)
        w.setframerate(rate)
        data = bytearray()
        for i in range(frames):
            for c in range(2):
                data += struct.pack("<i", noise_sample(i, c))[:3]
        w.writeframes(bytes(data))


def definitions(f):
    dissolve = f.create.OperationDef(DISSOLVE, "Mono Audio Dissolve")
    dissolve.media_kind = "sound"
    dissolve["NumberInputs"].value = 2
    f.dictionary.register_def(dissolve)
    amplitude = f.create.ParameterDef(AMPLITUDE, "Amplitude", "", "Rational")
    f.dictionary.register_def(amplitude)
    gain = f.create.OperationDef(GAIN, "Mono Audio Gain")
    gain.media_kind = "sound"
    gain["NumberInputs"].value = 1
    f.dictionary.register_def(gain)
    return dissolve, gain, amplitude


def embedded(scratch):
    tone, noise = scratch / "tone.wav", scratch / "noise.wav"
    write_tone(tone, 48000, 12000)
    write_noise(noise, 48000, 8000)
    with aaf2.open(str(OUT / "pyaaf2-embedded.aaf"), "w", sector_size=512, extensions=False) as f:
        dissolve, gain, amplitude = definitions(f)
        masters = {}
        for name, path in (("tone", tone), ("noise", noise)):
            master = f.create.MasterMob(name)
            f.content.mobs.append(master)
            master.import_audio_essence(str(path), 48000)
            masters[name] = master

        comp = f.create.CompositionMob("Fixture")
        comp.usage = "Usage_TopLevel"
        f.content.mobs.append(comp)

        dialog = f.create.Sequence(media_kind="sound")
        slot = comp.create_timeline_slot(48000)
        slot.name = "Dialog"
        slot.segment = dialog
        dialog.components.append(f.create.Filler("sound", 4800))
        first = masters["tone"].create_source_clip(1, start=1200, length=6000)
        first["FadeInLength"].value = 240
        dialog.components.append(first)
        transition = f.create.Transition("sound", 960)
        transition["CutPoint"].value = 480
        group = f.create.OperationGroup(dissolve, 960)
        group.media_kind = "sound"
        transition["OperationGroup"].value = group
        dialog.components.append(transition)
        gained = f.create.OperationGroup(gain, 4000)
        gained.media_kind = "sound"
        gained.segments.append(masters["noise"].create_source_clip(1, start=0, length=4000))
        gained.parameters.append(f.create.ConstantValue(amplitude, AAFRational(1, 2)))
        dialog.components.append(gained)
        dialog.components.append(f.create.Filler("sound", 1000))
        last = masters["tone"].create_source_clip(1, start=0, length=2400)
        last["FadeOutLength"].value = 480
        dialog.components.append(last)

        music = comp.create_timeline_slot(48000)
        music.name = "Music"
        music.segment = masters["noise"].create_source_clip(1, start=2000, length=6000)

        events = f.create.EventMobSlot()
        events["EditRate"].value = 48000
        events["SlotID"].value = 10
        markers = f.create.Sequence(media_kind="DescriptiveMetadata")
        hit = f.create.DescriptiveMarker()
        hit.media_kind = "DescriptiveMetadata"
        hit["Position"].value = 9600
        hit["Comment"].value = "Hit"
        markers.components.append(hit)
        out = f.create.CommentMarker()
        out.media_kind = "DescriptiveMetadata"
        out["Position"].value = 16000
        out["Length"].value = 480
        out["Comment"].value = "Out"
        markers.components.append(out)
        events.segment = markers
        comp.slots.append(events)


def linked():
    wav = OUT / "linked-tone.wav"
    write_tone(wav, 44100, 22050)
    with aaf2.open(str(OUT / "pyaaf2-linked.aaf"), "w", extensions=False) as f:
        master = f.create.MasterMob("linked-tone")
        f.content.mobs.append(master)
        master.import_audio_essence(str(wav), 44100, offline=True)
        source = next(m for m in f.content.mobs if m.name == "linked-tone.PHYS")
        locator = f.create.NetworkLocator()
        locator["URLString"].value = "file:///Volumes/Elsewhere/Media/linked-tone.wav"
        source.descriptor["Locator"].append(locator)
        comp = f.create.CompositionMob("Linked fixture")
        f.content.mobs.append(comp)
        sequence = f.create.Sequence(media_kind="sound")
        slot = comp.create_timeline_slot(44100)
        slot.name = "Linked"
        slot.segment = sequence
        sequence.components.append(f.create.Filler("sound", 4410))
        sequence.components.append(master.create_source_clip(1, start=441, length=22050 - 441))


OUT.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory() as directory:
    embedded(Path(directory))
linked()
for path in sorted(OUT.iterdir()):
    print(f"{path.relative_to(ROOT)}: {path.stat().st_size} bytes")
