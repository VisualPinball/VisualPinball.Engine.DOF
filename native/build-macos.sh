#!/usr/bin/env bash
# Builds VpeDof for macOS 12.0 and newer as a universal library (arm64 and x86_64) and copies it into the Unity package.
# Usage: native/build-macos.sh [Release|Debug]
# Needs CMake 3.24 or newer, git and the Xcode command-line tools.
# BUILD_DIR overrides the build folder (default build/osx).
set -euo pipefail

CONFIG="${1:-Release}"
NATIVE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$NATIVE/.." && pwd)"
BUILD="${BUILD_DIR:-$ROOT/build/osx}"
OUT="$ROOT/VisualPinball.Engine.DOF.Unity/Plugins/osx"

if [ ! -d "$ROOT/third-party/libdof/src" ]; then
	echo "The submodules are missing. Run: git submodule update --init --recursive"
	exit 1
fi

GENERATOR=()
if [ ! -f "$BUILD/CMakeCache.txt" ] && command -v ninja > /dev/null; then
	GENERATOR=(-G Ninja)
fi

cmake -S "$NATIVE" -B "$BUILD" ${GENERATOR[@]+"${GENERATOR[@]}"} -DCMAKE_BUILD_TYPE="$CONFIG" -DVPE_DOF_OUTPUT_DIR="$OUT" \
	-DCMAKE_OSX_DEPLOYMENT_TARGET=12.0 "-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64"
cmake --build "$BUILD" --parallel

# the smoke test runs natively, and as x86_64 too where Rosetta is installed
ARCHES=("$(uname -m)")
if [ "${ARCHES[0]}" = "arm64" ] && arch -x86_64 /usr/bin/true 2> /dev/null; then
	ARCHES+=(x86_64)
fi
for arch in "${ARCHES[@]}"; do
	if ! arch "-$arch" "$BUILD/vpe_dof_smoke" "$NATIVE/tests/fixtures/artnet" "$NATIVE/tests/fixtures/artnet/tables/smoke.vpx" vpetest > "$BUILD/smoke-$arch.log" 2>&1; then
		cat "$BUILD/smoke-$arch.log"
		echo "The native smoke test failed ($arch)."
		exit 1
	fi
	echo "Smoke test passed ($arch)"
done
echo "VpeDof $CONFIG built and copied to $OUT"
