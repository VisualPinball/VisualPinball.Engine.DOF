#!/usr/bin/env python3
# Visual Pinball Engine
# Copyright (C) 2026 freezy and VPE Team
#
# SPDX-License-Identifier: GPL-3.0-or-later

"""Release checks for a built VpeDof library.

- It exports the C ABI and nothing else: every function native/include/vpe_dof.h declares, and no other symbol, so its
  statically linked copies of libusb, hidapi, libserialport, libftdi, tinyxml2 and the C++ runtime can never clash with
  another package's copies. On macOS each architecture is checked.
- It imports only libraries of the operating system: no C/C++ runtime DLLs on Windows (static CRT), no libstdc++ or
  libgcc_s on Linux (linked statically), only libSystem, libc++, libobjc and system frameworks on macOS.
- Its vpe_dof_abi_version returns the VPE_DOF_ABI_VERSION of the header, so a library built from older sources with the
  same exports does not pass. The library is loaded for that: run the check on the platform it is built for.
- Linux: the newest glibc symbol version it needs (--max-glibc fails above that). macOS: the architectures it
  contains (--archs) and the minimum macOS version of each (--min-macos fails above that).
- --unity-meta: the library has its Unity import settings next to it, and its folder too, as native/unity has them
  (the build copies them from there; without them Unity imports the library with other settings and GUIDs).

Usage: release-check.py <library> [--header vpe_dof.h] [--max-glibc 2.35] [--archs arm64,x86_64] [--min-macos 12.0]
       [--unity-meta]
Uses dumpbin on Windows (found through vswhere when it is not on the PATH), nm and readelf on Linux, nm, otool and
lipo on macOS. Exits with 1 when a check fails.
"""

import argparse
import ctypes
import fnmatch
import os
import re
import shutil
import subprocess
import sys

HEADER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "include", "vpe_dof.h")
UNITY_META = os.path.join(os.path.dirname(os.path.abspath(__file__)), "unity")

WINDOWS_IMPORTS = {
	"kernel32.dll", "user32.dll", "advapi32.dll", "setupapi.dll", "cfgmgr32.dll", "hid.dll", "ws2_32.dll",
	"ole32.dll", "shell32.dll", "bcrypt.dll", "ntdll.dll",
}

LINUX_IMPORTS = {
	"libc.so.6", "libm.so.6", "libpthread.so.0", "libdl.so.2", "librt.so.1", "libudev.so.1",
	"ld-linux-x86-64.so.2",
}

MACOS_IMPORTS = ["/usr/lib/libSystem.B.dylib", "/usr/lib/libc++.1.dylib", "/usr/lib/libobjc.A.dylib",
	"/System/Library/Frameworks/*.framework/*"]


def run(command):
	result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, universal_newlines=True)
	if result.returncode != 0:
		raise RuntimeError("{} failed ({}):\n{}".format(" ".join(command), result.returncode, result.stdout))
	return result.stdout


def declared_functions(header):
	"""The functions the header exports (VPE_DOF_API ... name(...))."""
	with open(header, encoding="utf-8") as file:
		text = file.read()
	return set(re.findall(r"^VPE_DOF_API\b[^;(]*?\b(vpe_dof_\w+)\s*\(", text, re.MULTILINE))


def header_abi_version(header):
	with open(header, encoding="utf-8") as file:
		match = re.search(r"^#define\s+VPE_DOF_ABI_VERSION\s+(\d+)", file.read(), re.MULTILINE)
	return int(match.group(1)) if match else None


# The platform that can load a library of each kind.
LOADERS = {"PE": "win32", "ELF": "linux", "Mach-O": "darwin"}


def check_abi_version(library, kind, header):
	expected = header_abi_version(header)
	if expected is None:
		return [("{} defines no VPE_DOF_ABI_VERSION".format(header), False)]
	if not sys.platform.startswith(LOADERS[kind]):
		return [("cannot load a {} library on {} to check its ABI version".format(kind, sys.platform), False)]
	try:
		function = ctypes.CDLL(os.path.abspath(library)).vpe_dof_abi_version
	except (OSError, AttributeError) as error:
		return [("cannot call vpe_dof_abi_version: {}".format(error), False)]
	function.restype = ctypes.c_int32
	function.argtypes = []
	version = function()
	return [("ABI version {} (the header's is {})".format(version, expected), version == expected)]


def nm_symbol(line):
	"""The name of a symbol line of nm ("<address> <type> <name>"); None for headings and other lines."""
	fields = line.split()
	if len(fields) == 3 and re.fullmatch(r"[0-9a-fA-F]+", fields[0]) and len(fields[1]) == 1:
		return fields[2]
	return None


def check_unity_meta(library):
	"""The library's and its folder's .meta, which must be native/unity's: (text, ok) notes."""
	folder = os.path.dirname(os.path.abspath(library))
	rid = os.path.basename(folder)
	notes = []
	for found, template in ((library + ".meta", os.path.join(UNITY_META, rid, os.path.basename(library) + ".meta")),
			(folder + ".meta", os.path.join(UNITY_META, rid + ".meta"))):
		name = os.path.basename(found)
		if not os.path.exists(template):
			notes.append(("native/unity has no {} for {}".format(os.path.basename(template), rid), False))
		elif not os.path.exists(found):
			notes.append(("{} is missing next to the library".format(name), False))
		else:
			with open(found, "rb") as a, open(template, "rb") as b:
				same = a.read() == b.read()
			notes.append(("{} {}".format(name, "matches native/unity" if same else "differs from native/unity's"), same))
	return notes


def version_key(version):
	return tuple(int(part) for part in version.split("."))


def find_dumpbin():
	found = shutil.which("dumpbin")
	if found:
		return found
	vswhere = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
		"Microsoft Visual Studio", "Installer", "vswhere.exe")
	if os.path.exists(vswhere):
		paths = run([vswhere, "-latest", "-products", "*", "-find", r"VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe"])
		for line in paths.splitlines():
			if line.strip():
				return line.strip()
	raise RuntimeError("dumpbin was not found (neither on the PATH nor through vswhere)")


def check_pe(library, args):
	dumpbin = find_dumpbin()
	exports = []
	in_table = False
	for line in run([dumpbin, "/nologo", "/exports", library]).splitlines():
		if re.match(r"\s*ordinal\s+hint\s+RVA\s+name", line):
			in_table = True
			continue
		if in_table:
			if line.strip().startswith("Summary"):
				break
			fields = line.split()
			if len(fields) >= 4:
				exports.append(fields[3])
	imports = []
	in_list = False
	for line in run([dumpbin, "/nologo", "/dependents", library]).splitlines():
		if "has the following dependencies" in line:
			in_list = True
			continue
		if in_list:
			if line.strip().startswith("Summary"):
				break
			if line.strip():
				imports.append(line.strip())
	bad_imports = [name for name in imports if name.lower() not in WINDOWS_IMPORTS]
	return exports, imports, bad_imports, []


def check_elf(library, args):
	exports = []
	for line in run(["nm", "-D", "--defined-only", library]).splitlines():
		name = nm_symbol(line)
		if name:
			exports.append(name.split("@")[0])
	imports = re.findall(r"\(NEEDED\)\s+Shared library: \[([^\]]+)\]", run(["readelf", "-d", library]))
	bad_imports = [name for name in imports if name not in LINUX_IMPORTS]
	notes = []
	glibc = sorted(set(re.findall(r"GLIBC_([0-9.]+)", run(["readelf", "-V", library]))), key=version_key)
	newest = glibc[-1] if glibc else "none"
	notes.append(("newest glibc symbol version: " + newest, True))
	if args.max_glibc and glibc and version_key(newest) > version_key(args.max_glibc):
		notes.append(("needs glibc {}, newer than {}".format(newest, args.max_glibc), False))
	cxx = sorted(set(re.findall(r"(GLIBCXX_[0-9.]+|CXXABI_[0-9.]+|GCC_[0-9.]+)", run(["readelf", "-V", library]))))
	if cxx:
		notes.append(("needs libstdc++/libgcc symbol versions: " + ", ".join(cxx), False))
	return exports, imports, bad_imports, notes


def check_macho(library, args):
	archs = run(["lipo", "-archs", library]).split()
	exports = set()
	per_arch = {}
	for arch in archs:
		names = set()
		for line in run(["nm", "-gU", "-arch", arch, library]).splitlines():
			name = nm_symbol(line)
			if name:
				names.add(name[1:] if name.startswith("_") else name)
		per_arch[arch] = names
		exports |= names
	install_name = run(["otool", "-D", library]).splitlines()
	own = {line.strip() for line in install_name[1:] if line.strip()}
	imports = []
	for line in run(["otool", "-L", "-arch", "all", library]).splitlines():
		match = re.match(r"\s+(\S+) \(compatibility version", line)
		if match and match.group(1) not in own and match.group(1) not in imports:
			imports.append(match.group(1))
	bad_imports = [name for name in imports if not any(fnmatch.fnmatchcase(name, pattern) for pattern in MACOS_IMPORTS)]
	notes = []
	notes.append(("architectures: " + " ".join(archs), True))
	for arch, names in per_arch.items():
		if names != exports:
			notes.append(("{} exports a different set than the other architectures: {}".format(arch,
				", ".join(sorted(names ^ exports))), False))
	if args.archs:
		missing = [arch for arch in args.archs.split(",") if arch not in archs]
		if missing:
			notes.append(("missing architectures: " + " ".join(missing), False))
	for arch in archs:
		load_commands = run(["otool", "-l", "-arch", arch, library])
		versions = re.findall(r"\bminos ([0-9.]+)", load_commands) + re.findall(r"cmd LC_VERSION_MIN_MACOSX\s+cmdsize \d+\s+version ([0-9.]+)", load_commands)
		minimum = versions[0] if versions else "unknown"
		ok = bool(versions) and (not args.min_macos or version_key(minimum) <= version_key(args.min_macos))
		notes.append(("{} minimum macOS: {}".format(arch, minimum), ok))
	return sorted(exports), imports, bad_imports, notes


def main():
	parser = argparse.ArgumentParser(description="Release checks for a built VpeDof library.")
	parser.add_argument("library")
	parser.add_argument("--header", default=HEADER, help="the C ABI header (default native/include/vpe_dof.h)")
	parser.add_argument("--max-glibc", help="Linux: fail if the library needs a newer glibc than this")
	parser.add_argument("--archs", help="macOS: comma-separated architectures the library must contain")
	parser.add_argument("--min-macos", help="macOS: fail if an architecture needs a newer macOS than this")
	parser.add_argument("--unity-meta", action="store_true", help="fail if the library or its folder has no .meta as native/unity has it")
	args = parser.parse_args()

	with open(args.library, "rb") as file:
		magic = file.read(4)
	if magic[:2] == b"MZ":
		kind, check = "PE", check_pe
	elif magic == b"\x7fELF":
		kind, check = "ELF", check_elf
	elif magic in (b"\xca\xfe\xba\xbe", b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
		kind, check = "Mach-O", check_macho
	else:
		print("{}: not a PE, ELF or Mach-O file".format(args.library))
		return 1

	exports, imports, bad_imports, notes = check(args.library, args)
	notes += check_abi_version(args.library, kind, args.header)
	if args.unity_meta:
		notes += check_unity_meta(args.library)
	declared = declared_functions(args.header)
	if not declared:
		print("{}: declares no VPE_DOF_API function".format(args.header))
		return 1
	bad_exports = sorted(name for name in set(exports) if name not in declared)
	missing = sorted(declared - set(exports))
	failed = False

	print("{} ({})".format(args.library, kind))
	print("  exports: {} symbols, the header declares {} functions".format(len(set(exports)), len(declared)))
	for name in bad_exports:
		print("  FAIL: exports " + name + ", which the header does not declare")
		failed = True
	for name in missing:
		print("  FAIL: does not export " + name)
		failed = True
	print("  imports: " + (", ".join(imports) if imports else "nothing"))
	for name in bad_imports:
		print("  FAIL: imports " + name + ", which is not a system library")
		failed = True
	for text, ok in notes:
		print("  " + ("" if ok else "FAIL: ") + text)
		failed = failed or not ok
	print("  " + ("FAILED" if failed else "OK"))
	return 1 if failed else 0


if __name__ == "__main__":
	try:
		sys.exit(main())
	except RuntimeError as error:
		print(error)
		sys.exit(1)
