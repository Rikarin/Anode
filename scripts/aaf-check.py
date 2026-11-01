#!/usr/bin/env python3
# Reads an AAF file with pyaaf2 (an independent reader) and prints what it finds as JSON, for Anode's opt-in AAF tests
# (engine/tests/Anode.Interchange.Tests/Aaf/PyAafTests.cs): the top-level composition's audio slots, each component of
# their sequences with its position (computed here from the lengths, transitions overlapping), length, source offset,
# fades, clip gain (the mono audio gain's amplitude) and the media it resolves to through the master mob (embedded essence
# hashed, or the network locator's URL), and the markers.
#
# Usage: engine/artifacts/pyaaf2/bin/python -I scripts/aaf-check.py <file.aaf>
import hashlib
import json
import sys

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


main(sys.argv[1])
