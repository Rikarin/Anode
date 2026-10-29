#!/usr/bin/env bash
# Builds the native libraries into engine/artifacts/native. Re-run after changing native code.
# Third-party sources pinned in the CMakeLists (Signalsmith Stretch and Linear in an_stretch, QuickJS-ng in an_script,
# the CLAP headers in an_testclap, the VST3 SDK in an_plug) are fetched once into engine/artifacts/deps, with retries,
# and reused from there, so CI can cache that folder.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
deps="$here/../artifacts/deps"

# fetch <FetchContent name> <repository> <CMakeLists.txt>: clones the commit pinned there; prints its folder.
fetch() {
  local name="$1" url="$2" pins="$3" tag dir attempt
  tag="$(sed -n "/FetchContent_Declare($name/,/)/s/.*GIT_TAG \([0-9a-f]\{40\}\).*/\1/p" "$pins")"
  [ -n "$tag" ] || { echo "no pinned GIT_TAG for $name in $pins" >&2; exit 1; }
  dir="$deps/$name-$tag"
  if [ ! -f "$dir/.complete" ]; then
    for attempt in 1 2 3 4 5; do
      rm -rf "$dir"
      if git clone --quiet "$url" "$dir" && git -C "$dir" checkout --quiet "$tag"; then
        touch "$dir/.complete"
        break
      fi
      echo "fetching $name failed (attempt $attempt); retrying in $((attempt * 20)) s" >&2
      sleep $((attempt * 20))
    done
    [ -f "$dir/.complete" ] || { echo "could not fetch $name" >&2; exit 1; }
  fi
  echo "$dir"
}

stretch="$(fetch signalsmith_stretch https://github.com/Signalsmith-Audio/signalsmith-stretch.git "$here/an_stretch/CMakeLists.txt")"
linear="$(fetch signalsmith_linear https://github.com/Signalsmith-Audio/linear.git "$here/an_stretch/CMakeLists.txt")"
quickjs="$(fetch quickjs_ng https://github.com/quickjs-ng/quickjs.git "$here/an_script/CMakeLists.txt")"
clap="$(fetch clap https://github.com/free-audio/clap.git "$here/an_testclap/CMakeLists.txt")"
vst3_base="$(fetch vst3_base https://github.com/steinbergmedia/vst3_base.git "$here/an_plug/CMakeLists.txt")"
vst3_pluginterfaces="$(fetch vst3_pluginterfaces https://github.com/steinbergmedia/vst3_pluginterfaces.git \
  "$here/an_plug/CMakeLists.txt")"
vst3_public_sdk="$(fetch vst3_public_sdk https://github.com/steinbergmedia/vst3_public_sdk.git \
  "$here/an_plug/CMakeLists.txt")"

cmake -S "$here" -B "$here/../artifacts/obj/native" -DCMAKE_BUILD_TYPE=Release \
  -DFETCHCONTENT_SOURCE_DIR_SIGNALSMITH_STRETCH="$stretch" -DFETCHCONTENT_SOURCE_DIR_SIGNALSMITH_LINEAR="$linear" \
  -DFETCHCONTENT_SOURCE_DIR_QUICKJS_NG="$quickjs" -DFETCHCONTENT_SOURCE_DIR_CLAP="$clap" \
  -DFETCHCONTENT_SOURCE_DIR_VST3_BASE="$vst3_base" \
  -DFETCHCONTENT_SOURCE_DIR_VST3_PLUGINTERFACES="$vst3_pluginterfaces" \
  -DFETCHCONTENT_SOURCE_DIR_VST3_PUBLIC_SDK="$vst3_public_sdk" "$@"
cmake --build "$here/../artifacts/obj/native" --config Release --parallel
