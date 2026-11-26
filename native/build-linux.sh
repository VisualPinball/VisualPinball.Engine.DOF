#!/usr/bin/env bash
# Builds VpeDof for linux-x64 and copies it into the Unity package.
# Usage: native/build-linux.sh [Release|Debug]
# Needs CMake 3.24 or newer, a C++20 compiler, git, pkg-config and libudev's headers (libudev-dev).
# BUILD_DIR overrides the build folder (default build/linux-x64).
set -euo pipefail

CONFIG="${1:-Release}"
NATIVE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$NATIVE/.." && pwd)"
BUILD="${BUILD_DIR:-$ROOT/build/linux-x64}"
OUT="$ROOT/VisualPinball.Engine.DOF.Unity/Plugins/linux-x64"

if [ ! -d "$ROOT/third-party/libdof/src" ]; then
	echo "The submodules are missing. Run: git submodule update --init --recursive"
	exit 1
fi

GENERATOR=()
if [ ! -f "$BUILD/CMakeCache.txt" ] && command -v ninja > /dev/null; then
	GENERATOR=(-G Ninja)
fi

cmake -S "$NATIVE" -B "$BUILD" ${GENERATOR[@]+"${GENERATOR[@]}"} -DCMAKE_BUILD_TYPE="$CONFIG" -DVPE_DOF_OUTPUT_DIR="$OUT"
cmake --build "$BUILD" --parallel

if ! "$BUILD/vpe_dof_smoke" "$NATIVE/tests/fixtures/artnet" "$NATIVE/tests/fixtures/artnet/tables/smoke.vpx" vpetest > "$BUILD/smoke.log" 2>&1; then
	cat "$BUILD/smoke.log"
	echo "The native smoke test failed."
	exit 1
fi
echo "VpeDof $CONFIG built and copied to $OUT"
