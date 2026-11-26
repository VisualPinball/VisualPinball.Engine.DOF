// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VisualPinball.Engine.DOF
{
	/// <summary>
	/// Converts paths for libdof. On Windows libdof opens files through the narrow (ANSI code page) API, so a path that is
	/// not plain ASCII is replaced by its 8.3 short form; the part of the path that does not exist yet (a table file name)
	/// must be ASCII. Elsewhere paths pass unchanged, as UTF-8.
	/// </summary>
	public static class DofPaths
	{
		private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

		/// <exception cref="DofException">The path cannot be represented for libdof.</exception>
		public static string ToNative(string path)
		{
			if (string.IsNullOrEmpty(path) || !IsWindows || IsAscii(path)) {
				return path;
			}
			var full = Path.GetFullPath(path);
			// Shorten the longest part that exists and keep the rest, which has to be ASCII already.
			var existing = full;
			var rest = string.Empty;
			while (!string.IsNullOrEmpty(existing) && !File.Exists(existing) && !Directory.Exists(existing)) {
				var name = Path.GetFileName(existing);
				rest = rest.Length == 0 ? name : Path.Combine(name, rest);
				existing = Path.GetDirectoryName(existing);
			}
			var shortened = string.IsNullOrEmpty(existing) ? string.Empty : ShortPath(existing);
			var result = rest.Length == 0 ? shortened : Path.Combine(shortened, rest);
			if (!IsAscii(result)) {
				throw new DofException(DofStatus.InvalidArgument,
					$"DOF cannot open \"{path}\": the path has characters outside ASCII and no short (8.3) form. Move the configuration to a folder with a plain name.");
			}
			return result;
		}

		internal static bool IsAscii(string value)
		{
			foreach (var c in value) {
				if (c > 0x7F) {
					return false;
				}
			}
			return true;
		}

		private static string ShortPath(string path)
		{
			var buffer = new StringBuilder(1024);
			var length = GetShortPathNameW(path, buffer, buffer.Capacity);
			if (length > buffer.Capacity) {
				buffer.Capacity = (int)length;
				length = GetShortPathNameW(path, buffer, buffer.Capacity);
			}
			return length == 0 ? path : buffer.ToString();
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferSize);
	}
}
