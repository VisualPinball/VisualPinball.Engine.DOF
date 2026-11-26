// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>
	/// What VPE reads from a DOF ini file (<c>directoutputconfigN.ini</c> or the legacy <c>ledcontrolN.ini</c>): the
	/// device number, the version lines, the port names of the header comment and the ROM names of the rows. libdof
	/// parses the effects itself.
	/// </summary>
	public sealed class DofIniFile
	{
		private static readonly string[] ConfigSections = { "[Config DOF]", "[Config outs]" };
		private static readonly string[] KnownSections = {
			"[version]", "[Colors DOF]", "[Colors LedWiz]", "[Config DOF]", "[Config outs]", "[Variables DOF]", "[TableVariables]",
		};

		/// <summary>The file name.</summary>
		public string Name { get; }
		/// <summary>The device, the N of directoutputconfigN.ini; 1 without a number.</summary>
		public int Device { get; }
		/// <summary>True for <c>ledcontrol*.ini</c>, which libdof only reads when there is no <c>directoutputconfig*.ini</c>.</summary>
		public bool Legacy { get; }
		/// <summary>The <c>version</c> line of the <c>[version]</c> section, or null.</summary>
		public string Version { get; private set; }
		/// <summary>The <c>mindofversion</c> line, or null. Information only: libdof does not compare it.</summary>
		public string MinDofVersion { get; private set; }
		/// <summary>The port names from the header comment of the config section (index 0 is port 1), possibly empty.</summary>
		public IReadOnlyList<string> PortNames { get; private set; } = Array.Empty<string>();
		/// <summary>The ROM names of the table rows, in file order.</summary>
		public IReadOnlyList<string> Roms { get; private set; } = Array.Empty<string>();
		/// <summary>Sections libdof does not know; their lines are ignored.</summary>
		public IReadOnlyList<string> UnknownSections { get; private set; } = Array.Empty<string>();
		public bool HasVersionSection { get; private set; }
		/// <summary>Whether the file has a <c>[Colors DOF]</c> or <c>[Colors LedWiz]</c> section. Without one, DOF skips the whole file.</summary>
		public bool HasColorSection { get; private set; }

		private DofIniFile(string name, int device, bool legacy)
		{
			Name = name;
			Device = device;
			Legacy = legacy;
		}

		/// <summary>
		/// Returns the device number libdof gives a file name, or -1 if libdof does not load the file (another name, or a
		/// suffix that is not a number).
		/// </summary>
		public static int DeviceOf(string fileName, out bool legacy)
		{
			legacy = false;
			if (fileName == null || !fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) {
				return -1;
			}
			var stem = fileName.Substring(0, fileName.Length - 4);
			string prefix;
			if (stem.StartsWith("directoutputconfig", StringComparison.OrdinalIgnoreCase)) {
				prefix = "directoutputconfig";
			} else if (stem.StartsWith("ledcontrol", StringComparison.OrdinalIgnoreCase)) {
				prefix = "ledcontrol";
				legacy = true;
			} else {
				return -1;
			}
			var suffix = stem.Substring(prefix.Length);
			if (suffix.Length == 0) {
				return 1;
			}
			foreach (var c in suffix) {
				if (c < '0' || c > '9') {
					return -1;
				}
			}
			return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var device) ? device : -1;
		}

		/// <summary>True if the name is one libdof would look at for ini files, numbered or not.</summary>
		public static bool IsIniName(string fileName)
		{
			return fileName != null && fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
				&& (fileName.StartsWith("directoutputconfig", StringComparison.OrdinalIgnoreCase)
					|| fileName.StartsWith("ledcontrol", StringComparison.OrdinalIgnoreCase));
		}

		/// <exception cref="FormatException">The file is not a DOF ini file.</exception>
		public static DofIniFile Load(string path)
		{
			var name = Path.GetFileName(path);
			var device = DeviceOf(name, out var legacy);
			var file = new DofIniFile(name, device, legacy);
			file.Parse(ReadText(path), name);
			return file;
		}

		internal static string ReadText(string path)
		{
			var bytes = File.ReadAllBytes(path);
			if (Array.IndexOf(bytes, (byte)0) >= 0) {
				throw new FormatException($"{Path.GetFileName(path)} is not a text file.");
			}
			// libdof reads bytes; the Config Tool writes UTF-8 or ASCII. Invalid UTF-8 only affects names VPE shows.
			return new UTF8Encoding(false, false).GetString(bytes);
		}

		private void Parse(string text, string name)
		{
			var roms = new List<string>();
			var unknown = new List<string>();
			string section = null;
			var configFound = false;
			var expectHeader = false;
			using (var reader = new StringReader(text)) {
				string raw;
				while ((raw = reader.ReadLine()) != null) {
					var line = raw.Trim();
					if (line.Length == 0) {
						continue;
					}
					if (line.Length > 2 && line[0] == '[' && line[line.Length - 1] == ']') {
						section = line;
						var known = Array.Exists(KnownSections, s => string.Equals(s, line, StringComparison.OrdinalIgnoreCase));
						if (!known) {
							unknown.Add(line);
						}
						if (string.Equals(line, "[version]", StringComparison.OrdinalIgnoreCase)) {
							HasVersionSection = true;
						}
						expectHeader = Array.Exists(ConfigSections, s => string.Equals(s, line, StringComparison.OrdinalIgnoreCase));
						configFound |= expectHeader;
						continue;
					}
					if (section == null) {
						continue;
					}
					if (string.Equals(section, "[version]", StringComparison.OrdinalIgnoreCase)) {
						var eq = line.IndexOf('=');
						if (eq > 0) {
							var key = line.Substring(0, eq).Trim();
							var value = line.Substring(eq + 1).Trim();
							if (key.Equals("version", StringComparison.OrdinalIgnoreCase)) {
								Version = value;
							} else if (key.Equals("mindofversion", StringComparison.OrdinalIgnoreCase)) {
								MinDofVersion = value;
							}
						}
						continue;
					}
					// DOF reads the colours only from a section with at least one line, comments not counted.
					if (line[0] != '#' && (string.Equals(section, "[Colors DOF]", StringComparison.OrdinalIgnoreCase) || string.Equals(section, "[Colors LedWiz]", StringComparison.OrdinalIgnoreCase))) {
						HasColorSection = true;
						continue;
					}
					if (!Array.Exists(ConfigSections, s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase))) {
						continue;
					}
					if (line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal)) {
						if (expectHeader) {
							PortNames = ParseHeader(line.TrimStart('#', '/').Trim());
							expectHeader = false;
						}
						continue;
					}
					expectHeader = false;
					var comma = line.IndexOf(',');
					var rom = (comma >= 0 ? line.Substring(0, comma) : line).Trim();
					if (rom.Length > 0) {
						roms.Add(rom);
					}
				}
			}
			if (!configFound) {
				throw new FormatException($"{name} has no [Config DOF] or [Config outs] section, so it is not a DOF configuration file.");
			}
			Roms = roms;
			UnknownSections = unknown;
		}

		/// <summary>The Config Tool's header: <c>"Rom","Port 1 name",...</c>, quoted and comma separated.</summary>
		private static IReadOnlyList<string> ParseHeader(string header)
		{
			var fields = new List<string>();
			var current = new StringBuilder();
			var quoted = false;
			foreach (var c in header) {
				if (c == '"') {
					quoted = !quoted;
				} else if (c == ',' && !quoted) {
					fields.Add(current.ToString().Trim());
					current.Clear();
				} else {
					current.Append(c);
				}
			}
			fields.Add(current.ToString().Trim());
			if (fields.Count < 2 || !fields[0].Equals("Rom", StringComparison.OrdinalIgnoreCase)) {
				return Array.Empty<string>();
			}
			fields.RemoveAt(0);
			// The header ends with a comma: drop the empty field after it.
			if (fields.Count > 0 && fields[fields.Count - 1].Length == 0) {
				fields.RemoveAt(fields.Count - 1);
			}
			return fields;
		}
	}
}
