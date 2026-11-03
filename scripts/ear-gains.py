#!/usr/bin/env python3
# The EBU ADM Renderer's object gains (ITU-R BS.2127: the point-source panner and the polar extent panner) for a list
# of directions, for Anode's opt-in comparison tests (engine/tests/Anode.Dsp.Tests/Spatial/EarPannerTests.cs). Runs in
# the environment scripts/setup-ear.py makes.
#
# Usage: python -I scripts/ear-gains.py <BS.2051 system, e.g. 4+7+0>  < [[azimuth, elevation, extent], ...]
# Prints {"channels": [names without the LFE], "gains": [[...], ...]} as JSON.
import json
import sys

import numpy as np
from ear.core import bs2051, point_source
from ear.core.geom import cart
from ear.core.objectbased.extent import PolarExtentPanner


def main():
    layout = bs2051.get_layout(sys.argv[1]).without_lfe
    panner = point_source.configure(layout)
    extent = PolarExtentPanner(panner.handle)
    gains = []
    for azimuth, elevation, width in json.load(sys.stdin):
        position = cart(azimuth, elevation, 1.0)
        gains.append([float(g) for g in extent.calc_pv_spread(position, width, width)])
    json.dump({"channels": layout.channel_names, "gains": gains}, sys.stdout)


main()
