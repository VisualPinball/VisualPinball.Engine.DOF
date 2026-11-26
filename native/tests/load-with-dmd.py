#!/usr/bin/env python3
# Visual Pinball Engine
# Copyright (C) 2026 freezy and VPE Team
#
# SPDX-License-Identifier: GPL-3.0-or-later

"""Loads the DMD package's libusb and libserialport and VpeDof into one process, in both orders, and calls into each.

The DMD package ships its own libusb and libserialport, the libraries VpeDof also contains. VpeDof links its copies
statically and exports only its C ABI, so the two can be loaded together in either order: neither resolves a symbol in
the other's copy. The libraries are loaded with RTLD_GLOBAL, the worst case for symbol interposition on Linux and macOS.
Each order runs in a process of its own.

Only those two are loaded: they are what VpeDof shares with DMD. Whether the rest of DMD's libraries load is DMD's to
check, not VpeDof's.

The calls do not depend on hardware: DMD's libusb and libserialport report their versions, VpeDof enumerates the
connected devices (which succeeds with none), and VpeDof must not resolve any of the hardware libraries' functions.

Usage: load-with-dmd.py <VpeDof library> <the DMD package's Plugins/<rid> folder>
Exits with 1 when a library does not load, a call fails or VpeDof resolves a hardware library function.
"""

import argparse
import ctypes
import os
import re
import struct
import subprocess
import sys

# the DMD libraries VpeDof also contains
SHARED = ["usb", "serialport"]

# what VpeDof links statically and must not export
HIDDEN = ["libusb_init", "libusb_get_version", "hid_init", "hid_enumerate", "sp_list_ports", "sp_get_lib_version_string", "ftdi_new"]

VPE_DOF_OK = 0

STRING_CALLBACK = ctypes.CFUNCTYPE(None, ctypes.c_char_p, ctypes.c_void_p)


class LibusbVersion(ctypes.Structure):
	_fields_ = [("major", ctypes.c_uint16), ("minor", ctypes.c_uint16), ("micro", ctypes.c_uint16),
		("nano", ctypes.c_uint16), ("rc", ctypes.c_char_p), ("describe", ctypes.c_char_p)]


def is_native(name):
	if sys.platform == "win32":
		return name.lower().endswith(".dll")
	if sys.platform == "darwin":
		return name.endswith(".dylib")
	return re.search(r"\.so(\.\d+)*$", name) is not None


def component(name):
	"""The library a file belongs to: "libzedmd.so.0.11.0" and "zedmd64.dll" are "zedmd"."""
	base = name.split(".")[0].lower()
	if base.startswith("lib"):
		base = base[3:]
	return re.sub(r"(64)?(-\d+)?$", "", base)


def dmd_libraries(folder):
	return sorted(name for name in os.listdir(folder)
		if is_native(name) and os.path.isfile(os.path.join(folder, name)) and component(name) in SHARED)


def pe_imports(path):
	"""The DLLs a PE file imports (not the delay-loaded ones), from its import directory."""
	with open(path, "rb") as file:
		data = file.read()
	pe = struct.unpack_from("<I", data, 0x3C)[0]
	sections, optional_size = struct.unpack_from("<H", data, pe + 6)[0], struct.unpack_from("<H", data, pe + 20)[0]
	optional = pe + 24
	directories = optional + (112 if struct.unpack_from("<H", data, optional)[0] == 0x20B else 96)
	import_rva = struct.unpack_from("<I", data, directories + 8)[0]
	table = [struct.unpack_from("<IIII", data, optional + optional_size + 40 * i + 8) for i in range(sections)]

	def offset(rva):
		for size, address, raw_size, raw in table:
			if address <= rva < address + max(size, raw_size):
				return raw + rva - address
		raise ValueError("RVA {:x} is in no section".format(rva))

	names = []
	position = offset(import_rva) if import_rva else None
	while position is not None:
		name_rva = struct.unpack_from("<IIIII", data, position)[3]
		if not name_rva:
			break
		start = offset(name_rva)
		names.append(data[start:data.index(b"\0", start)].decode("ascii"))
		position += 20
	return names


def missing_dependencies(path, folder):
	"""Windows: the DLLs a library imports that are neither in the DMD folder nor loadable from the system."""
	present = {name.lower() for name in os.listdir(folder)}
	missing = []
	for name in pe_imports(path):
		if name.lower() in present:
			continue
		try:
			ctypes.WinDLL(name)
		except OSError:
			missing.append(name)
	return missing


def load(path):
	if sys.platform == "win32":
		return ctypes.CDLL(path)
	return ctypes.CDLL(path, mode=ctypes.RTLD_GLOBAL)


def load_dmd(folder):
	names = dmd_libraries(folder)
	for prefix in SHARED:
		if not any(component(name) == prefix for name in names):
			raise RuntimeError("the DMD folder has no {} library".format(prefix))
	libraries = {}
	failed = []
	for name in names:
		path = os.path.join(folder, name)
		try:
			libraries[name] = load(path)
			print("  loaded DMD's " + name)
		except OSError as error:
			reason = str(error)
			if sys.platform == "win32":
				missing = missing_dependencies(path, folder)
				if missing:
					reason = "it imports " + ", ".join(missing) + ", which the folder does not have"
			print("  FAIL: DMD's {} does not load: {}".format(name, reason))
			failed.append(name)
	if failed:
		raise RuntimeError("{} of DMD's {} libraries do not load".format(len(failed), len(names)))
	return libraries


def find(libraries, prefix):
	for name in sorted(libraries):
		if component(name) == prefix:
			return libraries[name]
	raise RuntimeError("the DMD folder has no {} library".format(prefix))


def call_dmd(libraries):
	libusb = find(libraries, "usb")
	libusb.libusb_get_version.restype = ctypes.POINTER(LibusbVersion)
	version = libusb.libusb_get_version().contents
	if version.major != 1:
		raise RuntimeError("DMD's libusb_get_version returned {}.{}".format(version.major, version.minor))
	serialport = find(libraries, "serialport")
	serialport.sp_get_lib_version_string.restype = ctypes.c_char_p
	serial_version = serialport.sp_get_lib_version_string()
	if not serial_version:
		raise RuntimeError("DMD's sp_get_lib_version_string returned nothing")
	print("  DMD's libusb {}.{}.{}.{}, libserialport {}".format(version.major, version.minor, version.micro, version.nano,
		serial_version.decode()))


def load_dof(path):
	dof = load(path)
	print("  loaded " + os.path.basename(path))
	return dof


def call_dof(dof):
	dof.vpe_dof_libdof_version.restype = ctypes.c_char_p
	lines = []
	callback = STRING_CALLBACK(lambda value, user: lines.append(value.decode("utf-8", "replace")))
	dof.vpe_dof_enumerate_devices.argtypes = [STRING_CALLBACK, ctypes.c_void_p]
	dof.vpe_dof_enumerate_devices.restype = ctypes.c_int32
	status = dof.vpe_dof_enumerate_devices(callback, None)
	if status != VPE_DOF_OK:
		raise RuntimeError("vpe_dof_enumerate_devices returned {}".format(status))
	kinds = {}
	for line in lines:
		kind = line.split("|", 1)[0]
		kinds[kind] = kinds.get(kind, 0) + 1
	print("  VpeDof ABI {}, libdof {}: enumerated {} devices ({})".format(dof.vpe_dof_abi_version(),
		dof.vpe_dof_libdof_version().decode(), len(lines), ", ".join("{} {}".format(n, k) for k, n in sorted(kinds.items())) or "none"))
	for name in HIDDEN:
		if hasattr(dof, name):
			raise RuntimeError("VpeDof resolves {}".format(name))
	print("  VpeDof resolves none of " + ", ".join(HIDDEN))


def child(order, dof_path, dmd_folder):
	if sys.platform == "win32":
		# as the player loads plugin natives: their own dependencies come from their folder
		os.add_dll_directory(os.path.abspath(dmd_folder))
	if order == "dmd-first":
		dmd = load_dmd(dmd_folder)
		dof = load_dof(dof_path)
	else:
		dof = load_dof(dof_path)
		dmd = load_dmd(dmd_folder)
	call_dmd(dmd)
	call_dof(dof)
	call_dmd(dmd)


def main():
	parser = argparse.ArgumentParser(description="Loads DMD's libusb and libserialport and VpeDof into one process, in both orders.")
	parser.add_argument("dof", help="the VpeDof library")
	parser.add_argument("dmd", help="the DMD package's Plugins/<rid> folder")
	parser.add_argument("--order", choices=["dmd-first", "dof-first"], help=argparse.SUPPRESS)
	args = parser.parse_args()
	if args.order:
		try:
			child(args.order, args.dof, args.dmd)
		except (OSError, RuntimeError) as error:
			print("  FAIL: {}".format(error))
			return 1
		return 0

	# the dynamic loader finds the DMD libraries' own dependencies in their folder, as it does for the player's plugins
	env = dict(os.environ)
	variable = "DYLD_LIBRARY_PATH" if sys.platform == "darwin" else "LD_LIBRARY_PATH"
	if sys.platform != "win32":
		env[variable] = os.path.abspath(args.dmd) + (os.pathsep + env[variable] if env.get(variable) else "")
	failed = False
	for order in ("dmd-first", "dof-first"):
		print(order + ":")
		sys.stdout.flush()
		command = [sys.executable, os.path.abspath(__file__), args.dof, args.dmd, "--order", order]
		result = subprocess.call(command, env=env)
		print("  " + ("OK" if result == 0 else "FAILED"))
		failed = failed or result != 0
	return 1 if failed else 0


if __name__ == "__main__":
	sys.exit(main())
