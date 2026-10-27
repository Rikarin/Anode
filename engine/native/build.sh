#!/usr/bin/env bash
# Builds the native libraries into engine/artifacts/native. Re-run after changing native code.
# Signalsmith Stretch and Signalsmith Linear (pinned in an_stretch/CMakeLists.txt) are fetched once into
# engine/artifacts/deps, with retries, and reused from there, so CI can cache that folder.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
deps="$here/../artifacts/deps"

# fetch <FetchContent name> <repository>: clones the commit pinned in an_stretch/CMakeLists.txt; prints its folder.
fetch() {
  local name="$1" url="$2" tag dir attempt
  tag="$(sed -n "/FetchContent_Declare($name/,/)/s/.*GIT_TAG \([0-9a-f]\{40\}\).*/\1/p" "$here/an_stretch/CMakeLists.txt")"
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

stretch="$(fetch signalsmith_stretch https://github.com/Signalsmith-Audio/signalsmith-stretch.git)"
linear="$(fetch signalsmith_linear https://github.com/Signalsmith-Audio/linear.git)"

cmake -S "$here" -B "$here/../artifacts/obj/native" -DCMAKE_BUILD_TYPE=Release \
  -DFETCHCONTENT_SOURCE_DIR_SIGNALSMITH_STRETCH="$stretch" -DFETCHCONTENT_SOURCE_DIR_SIGNALSMITH_LINEAR="$linear" "$@"
cmake --build "$here/../artifacts/obj/native" --config Release --parallel
