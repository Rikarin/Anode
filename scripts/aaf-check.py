#!/usr/bin/env python3
# Reads and writes AAF files with pyaaf2 (MIT, an independent implementation) for Anode's AAF checks.
#
#   aaf-check.py <file.aaf>
#       Everything Anode's opt-in AAF tests compare (engine/tests/Anode.Interchange.Tests/Aaf/PyAafTests.cs): the
#       top-level composition's audio slots, each component of their sequences with its position (computed here from
#       the lengths, transitions overlapping), length, source offset, fades, clip gain (the mono audio gain's
#       amplitude) and the media it resolves to through the master mob (embedded essence hashed, or the network
#       locator's URL), and the markers.
#   aaf-check.py read <file.aaf>
#       The summary the Phase 9 golden path checks (editor/apps/desktop/e2e/phase9/), in samples:
#       { sampleRate, tracks: [{ name, clips: [{ name, position, sourceOffset, length, fadeIn, fadeOut, gainDb,
#       media }] }], transitions: [{ track, position, length }] }, media being "embedded" or the linked file's path.
#   aaf-check.py make <out.aaf> <media folder>
#       Writes the golden path's import fixture (48 kHz, media embedded, their WAVs left in the media folder): track
#       "Dialog" with two clips joined by a crossfade, track "Music" with one clip with gain and fades; then prints
#       its summary as `read` does.
#
# Usage: engine/artifacts/pyaaf2/bin/python -I scripts/aaf-check.py [read|make] ... (scripts/setup-pyaaf2.py makes it)
import hashlib
import json
import math
import struct
import sys
import urllib.parse
import urllib.request
import wave
from pathlib import Path

import aaf2

GAIN_IDS = {"9d2ea894-0968-11d3-8a38-0050040ef7d2", "9d2ea895-0968-11d3-8a38-0050040ef7d2"}


def rational(value):
    return [value.numerator, value.denominator]


def media_of(f, clip):
    master = clip.mob
    slot = next(s for s in master.slots if s.slot_id == clip.slot_id)
    file_clip = slot.segment
    source = file_clip.mob
    descriptor = source.descriptor
    media = {
        "master": master.name,
        "masterStart": file_clip.start,
        "class": descriptor.classdef.class_name,
        "channels": descriptor["Channels"].value,
        "bits": descriptor["QuantizationBits"].value,
        "rate": rational(descriptor["SampleRate"].value),
        "frames": descriptor["Length"].value,
        "container": descriptor["ContainerFormat"].value.name if "ContainerFormat" in descriptor else None,
    }
    essence = f.content.essencedata.get(source.mob_id)
    if essence is not None:
        data = essence.open("r").read()
        media["essenceBytes"] = len(data)
        media["essenceSha256"] = hashlib.sha256(data).hexdigest()
    if "Locator" in descriptor:
        media["urls"] = [loc["URLString"].value for loc in descriptor["Locator"]]
    return media


def clip_of(f, component):
    gain = None
    while component.classdef.class_name == "OperationGroup":
        operation = component.operation
        if str(operation.auid) in GAIN_IDS:
            for parameter in component.parameters:
                if parameter.name == "ConstantValue" or parameter.classdef.class_name == "ConstantValue":
                    value = parameter.value
                    gain = (gain or 1.0) * (float(value.numerator) / float(value.denominator))
        component = component.segments[0]
    if component.classdef.class_name != "SourceClip":
        return None
    result = {
        "start": component.start,
        "fadeIn": component["FadeInLength"].value if "FadeInLength" in component else None,
        "fadeOut": component["FadeOutLength"].value if "FadeOutLength" in component else None,
        "gain": gain,
        "media": media_of(f, component),
    }
    return result


def main(path):
    out = {"tracks": [], "markers": []}
    with aaf2.open(path, "r") as f:
        compositions = list(f.content.toplevel())
        if not compositions:
            compositions = list(f.content.compositionmobs())
        comp = compositions[0]
        out["name"] = comp.name
        for slot in comp.slots:
            segment = slot.segment
            kind = segment.media_kind
            if slot.classdef.class_name == "EventMobSlot":
                for marker in segment.components:
                    out["markers"].append({
                        "class": marker.classdef.class_name,
                        "position": marker["Position"].value,
                        "length": marker["Length"].value if "Length" in marker else None,
                        "comment": marker["Comment"].value if "Comment" in marker else None,
                        "userComments": {t.name: t.value for t in marker["UserComments"]}
                        if "UserComments" in marker else {},
                        "editRate": rational(slot["EditRate"].value),
                    })
                continue
            if kind != "Sound":
                continue
            track = {"name": slot.name, "slotId": slot.slot_id, "editRate": rational(slot.edit_rate),
                     "length": segment.length, "components": []}
            position = 0
            components = segment.components if segment.classdef.class_name == "Sequence" else [segment]
            for component in components:
                name = component.classdef.class_name
                item = {"kind": name, "length": component.length}
                if name == "Transition":
                    position -= component.length
                    item["position"] = position
                    item["cutPoint"] = component["CutPoint"].value
                    item["operation"] = component["OperationGroup"].value.operation.name
                else:
                    item["position"] = position
                    clip = clip_of(f, component)
                    if clip is not None:
                        item.update(clip)
                    position += component.length
                track["components"].append(item)
            out["tracks"].append(track)
    json.dump(out, sys.stdout, indent=1)


def summary(path):
    """The golden path's view of an AAF: clips and transitions per audio slot, in samples of the composition's rate."""
    out = {"sampleRate": None, "tracks": [], "transitions": []}
    with aaf2.open(path, "r") as f:
        compositions = list(f.content.toplevel()) or list(f.content.compositionmobs())
        comp = compositions[0]
        for slot in comp.slots:
            if slot.classdef.class_name == "EventMobSlot" or slot.segment.media_kind != "Sound":
                continue
            rate = slot.edit_rate
            if out["sampleRate"] is None and rate.denominator == 1:
                out["sampleRate"] = rate.numerator
            track = {"name": slot.name, "clips": []}
            segment = slot.segment
            components = segment.components if segment.classdef.class_name == "Sequence" else [segment]
            position = 0
            for component in components:
                if component.classdef.class_name == "Transition":
                    position -= component.length
                    out["transitions"].append({"track": slot.name, "position": position, "length": component.length})
                    continue
                clip = clip_of(f, component)
                if clip is not None:
                    media = clip["media"]
                    gain = clip["gain"]
                    track["clips"].append({
                        "name": media["master"],
                        "position": position,
                        "sourceOffset": clip["start"] + media["masterStart"],
                        "length": component.length,
                        "fadeIn": clip["fadeIn"] or 0,
                        "fadeOut": clip["fadeOut"] or 0,
                        "gainDb": round(20 * math.log10(gain), 6) if gain else 0.0,
                        "media": "embedded" if "essenceSha256" in media else local_path(media.get("urls", [""])[0]),
                    })
                position += component.length
            out["tracks"].append(track)
    return out


def local_path(url):
    parsed = urllib.parse.urlparse(url)
    return urllib.request.url2pathname(parsed.path) if parsed.scheme == "file" else url


def write_wav(path, frames, period):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * i / period))) for i in range(frames)))


RATE = 48000
DISSOLVE = "0c3bea41-fc05-11d2-8a29-0050040ef7d2"
GAIN = "9d2ea894-0968-11d3-8a38-0050040ef7d2"
AMPLITUDE = "e4962321-2267-11d3-8a4c-0050040ef7d2"


def make(path, media_folder):
    """The import fixture. Positions are whole ticks at 120 BPM (25 samples), so regions land on them exactly."""
    from aaf2.rational import AAFRational

    folder = Path(media_folder)
    folder.mkdir(parents=True, exist_ok=True)
    tone, hum = folder / "fixture-tone.wav", folder / "fixture-hum.wav"
    write_wav(tone, 3 * RATE, 109.0)
    write_wav(hum, 2 * RATE, 218.0)
    with aaf2.open(str(path), "w", extensions=False) as f:
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
        masters = {}
        for name, wav in (("fixture-tone", tone), ("fixture-hum", hum)):
            master = f.create.MasterMob(name)
            f.content.mobs.append(master)
            master.import_audio_essence(str(wav), RATE)
            masters[name] = master

        comp = f.create.CompositionMob("Golden fixture")
        comp.usage = "Usage_TopLevel"
        f.content.mobs.append(comp)

        dialog = f.create.Sequence(media_kind="sound")
        slot = comp.create_timeline_slot(RATE)
        slot.name = "Dialog"
        slot.segment = dialog
        dialog.components.append(f.create.Filler("sound", 4800))
        first = masters["fixture-tone"].create_source_clip(1, start=1200, length=48000)
        first["FadeInLength"].value = 600
        dialog.components.append(first)
        transition = f.create.Transition("sound", 2400)
        transition["CutPoint"].value = 1200
        group = f.create.OperationGroup(dissolve, 2400)
        group.media_kind = "sound"
        transition["OperationGroup"].value = group
        dialog.components.append(transition)
        dialog.components.append(masters["fixture-tone"].create_source_clip(1, start=60000, length=36000))

        music = f.create.Sequence(media_kind="sound")
        slot = comp.create_timeline_slot(RATE)
        slot.name = "Music"
        slot.segment = music
        music.components.append(f.create.Filler("sound", 9600))
        gained = f.create.OperationGroup(gain, 72000)
        gained.media_kind = "sound"
        clip = masters["fixture-hum"].create_source_clip(1, start=2000, length=72000)
        clip["FadeInLength"].value = 4800
        clip["FadeOutLength"].value = 9600
        gained.segments.append(clip)
        gained.parameters.append(f.create.ConstantValue(amplitude, AAFRational(1, 2)))
        music.components.append(gained)


if len(sys.argv) >= 3 and sys.argv[1] == "read":
    json.dump(summary(sys.argv[2]), sys.stdout, indent=1)
elif len(sys.argv) >= 4 and sys.argv[1] == "make":
    make(sys.argv[2], sys.argv[3])
    json.dump(summary(sys.argv[2]), sys.stdout, indent=1)
else:
    main(sys.argv[1])
