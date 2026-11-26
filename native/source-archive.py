#!/usr/bin/env python3
"""Writes the complete corresponding source of a release as one .tar.gz.

The archive holds the repository at a commit (default HEAD) and every submodule at the revision that commit pins,
recursively, as git has them (tracked files only), under one folder. That is libdof, hidapi, libusb, libserialport
and libftdi with their licence files, VPE's patches, the CMake project and build scripts, the C ABI shim, the managed
library, the licence (LICENSE) and THIRD-PARTY-NOTICES.md. SOURCES.txt at its root lists the commits and where they
come from. From it, VpeDof can be rebuilt and relinked with modified versions of the libraries it links statically.

The submodules must be checked out (git submodule update --init --recursive) so their pinned commits are present.

Usage: source-archive.py [--ref <commit>] [--output <folder>]
"""

import argparse
import io
import os
import subprocess
import sys
import tarfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NAME = "VisualPinball.Engine.DOF"


def git(repository, *arguments):
	return subprocess.run(["git", "-C", repository] + list(arguments), stdout=subprocess.PIPE, check=True).stdout


def submodules(repository, commit):
	"""(path, commit) of the submodules a commit pins, with their URLs from its .gitmodules."""
	found = []
	for line in git(repository, "ls-tree", "-r", commit).decode().splitlines():
		meta, path = line.split("\t", 1)
		mode, kind, sha = meta.split()
		if kind == "commit":
			found.append((path, sha))
	urls = {}
	if found:
		gitmodules = git(repository, "show", commit + ":.gitmodules").decode()
		config = subprocess.run(["git", "config", "--file", "-", "--get-regexp", r"^submodule\..*\.(path|url)$"],
			input=gitmodules.encode(), stdout=subprocess.PIPE, check=True).stdout.decode()
		paths = {}
		for entry in config.splitlines():
			key, value = entry.split(" ", 1)
			name, field = key[len("submodule."):].rsplit(".", 1)
			paths.setdefault(name, {})[field] = value
		urls = {values.get("path"): values.get("url") for values in paths.values()}
	return [(path, sha, urls.get(path, "")) for path, sha in found]


def add_archive(output, repository, commit, prefix):
	data = git(repository, "archive", "--format=tar", "--prefix=" + prefix, commit)
	with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
		for member in archive:
			if member.isfile():
				output.addfile(member, archive.extractfile(member))
			else:
				output.addfile(member)


def add_tree(output, repository, commit, prefix, sources, top):
	add_archive(output, repository, commit, prefix)
	for path, sha, url in submodules(repository, commit):
		location = os.path.join(repository, path)
		if not os.path.exists(os.path.join(location, ".git")):
			raise RuntimeError("{} is not checked out; run git submodule update --init --recursive".format(location))
		sources.append("{}  {}  {}".format(os.path.relpath(location, top).replace(os.sep, "/"), sha, url))
		add_tree(output, location, sha, prefix + path + "/", sources, top)


def main():
	parser = argparse.ArgumentParser(description="Writes the complete corresponding source of a release.")
	parser.add_argument("--ref", default="HEAD", help="commit of this repository (default HEAD)")
	parser.add_argument("--output", default=os.path.join(ROOT, "build"), help="folder the archive is written to")
	args = parser.parse_args()

	commit = git(ROOT, "rev-parse", args.ref + "^{commit}").decode().strip()
	version = git(ROOT, "describe", "--tags", "--always", commit).decode().strip()
	folder = "{}-{}".format(NAME, version)
	os.makedirs(args.output, exist_ok=True)
	path = os.path.join(args.output, folder + "-source.tar.gz")

	sources = ["{}  {}".format(".", commit)]
	with tarfile.open(path, "w:gz", format=tarfile.PAX_FORMAT) as output:
		add_tree(output, ROOT, commit, folder + "/", sources, ROOT)
		text = ("Source of {} {}, with the submodules at the revisions it pins (path, commit, origin):\n\n{}\n\n"
			"Build: see README.md (native/build-windows.cmd, native/build-linux.sh, native/build-macos.sh). The patches in "
			"native/patches are applied to a copy of third-party/libdof by the CMake project.\n").format(
			NAME, version, "\n".join(sources))
		info = tarfile.TarInfo(folder + "/SOURCES.txt")
		info.size = len(text.encode())
		info.mtime = int(git(ROOT, "show", "-s", "--format=%ct", commit).decode().strip())
		info.mode = 0o644
		output.addfile(info, io.BytesIO(text.encode()))
	print(path)
	print("\n".join(sources))
	return 0


if __name__ == "__main__":
	try:
		sys.exit(main())
	except (RuntimeError, subprocess.CalledProcessError) as error:
		print(error)
		sys.exit(1)
