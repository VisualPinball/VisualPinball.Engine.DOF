// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>
	/// What VPE reads from a DOF ini file (<c>directoutputconfigN.ini</c> or the legacy <c>ledcontrolN.ini</c>): the
	/// device number, the version lines, the port names of the header comment, the ROM names of the rows and which ports
	/// RGB toys take. libdof parses the effects itself.
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
		/// <summary>
		/// The ports (from 1) where an RGB toy starts; it takes that port and the next two. As libdof numbers the columns of a
		/// row, its variables replaced: one with a colour takes three ports, any other one. The header names the toy on its
		/// first port only, like the ports nothing is assigned to.
		/// </summary>
		public IReadOnlyList<int> RgbPorts { get; private set; } = Array.Empty<int>();
		/// <summary>The last port (from 1) the header names or a row reaches, counted like <see cref="RgbPorts"/>; 0 without either.</summary>
		public int PortExtent { get; private set; }
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
			// libdof reads the first [Config DOF] section, or else the first [Config outs] one
			List<string> configDof = null, configOuts = null, rows = null;
			IReadOnlyList<string> dofHeader = null, outsHeader = null;
			var variables = new List<string>();
			var tableVariables = new List<string>();
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
						rows = null;
						if (string.Equals(line, ConfigSections[0], StringComparison.OrdinalIgnoreCase) && configDof == null) {
							rows = configDof = new List<string>();
						} else if (string.Equals(line, ConfigSections[1], StringComparison.OrdinalIgnoreCase) && configOuts == null) {
							rows = configOuts = new List<string>();
						}
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
					if (line[0] != '#' && string.Equals(section, "[Variables DOF]", StringComparison.OrdinalIgnoreCase)) {
						variables.Add(line);
						continue;
					}
					if (line[0] != '#' && string.Equals(section, "[TableVariables]", StringComparison.OrdinalIgnoreCase)) {
						tableVariables.Add(line);
						continue;
					}
					if (!Array.Exists(ConfigSections, s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase))) {
						continue;
					}
					if (line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal)) {
						if (expectHeader) {
							// the header of the section the rows are read from, not of a later one libdof ignores
							if (rows != null && rows == configDof) {
								dofHeader = ParseHeader(line.TrimStart('#', '/').Trim());
							} else if (rows != null) {
								outsHeader = ParseHeader(line.TrimStart('#', '/').Trim());
							}
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
					rows?.Add(line);
				}
			}
			PortNames = (configDof != null ? dofHeader : outsHeader) ?? Array.Empty<string>();
			ReadPorts(configDof ?? configOuts ?? new List<string>(), Variables(variables), TableVariables(tableVariables));
			if (!configFound) {
				throw new FormatException($"{name} has no [Config DOF] or [Config outs] section, so it is not a DOF configuration file.");
			}
			Roms = roms;
			UnknownSections = unknown;
		}

		private void ReadPorts(IEnumerable<string> rows, IReadOnlyList<KeyValuePair<string, string>> variables,
			IReadOnlyDictionary<string, List<KeyValuePair<string, string>>> tableVariables)
		{
			var rgb = new SortedSet<int>();
			// without a header, the columns a row writes are all there is to tell the board's size, its 0s too
			var extent = 0;
			var reaches = new List<(int Extent, List<int> Starts)>();
			var roms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var row in rows) {
				var columns = Split(Resolve(row, variables, tableVariables), ',');
				// libdof keeps the first row of a ROM
				if (columns.Count == 0 || string.IsNullOrWhiteSpace(columns[0]) || !roms.Add(columns[0].Trim())) {
					continue;
				}
				// libdof numbers the columns up to the last one with data
				var last = columns.Count - 1;
				while (last > 0 && string.IsNullOrWhiteSpace(columns[last])) {
					last--;
				}
				var port = 1;
				var reached = 0;
				var starts = new List<int>();
				for (var i = 1; i <= last; i++) {
					var settings = Split(columns[i], '/').Where(setting => !string.IsNullOrWhiteSpace(setting)).Select(Colour).ToList();
					var colour = settings.Any(c => c == true);
					if (colour) {
						starts.Add(port);
					}
					port += colour ? 3 : 1;
					// a column of settings libdof drops, such as 0, numbers its port but puts nothing on it: past the header, the
					// Config Tool's 0s say nothing about the board
					if (settings.Any(c => c != null)) {
						reached = port - 1;
					}
				}
				rgb.UnionWith(starts);
				extent = Math.Max(extent, port - 1);
				reaches.Add((reached, starts));
			}
			if (PortNames.Count == 0) {
				RgbPorts = rgb.ToArray();
				PortExtent = extent;
				return;
			}
			// With a header, an RGB toy is a named port and two unnamed ones, as the Config Tool writes them: a row with a colour
			// elsewhere, such as an area effect in a single toy's column, shifts that row's columns but not the cabinet's ports.
			bool Named(int port) => port <= PortNames.Count && !string.IsNullOrWhiteSpace(PortNames[port - 1]);
			var accepted = new HashSet<int>(rgb.Where(p => p <= PortNames.Count && Named(p) && !Named(p + 1) && !Named(p + 2)));
			// the rows that keep to those ports: how far they reach, and their RGB toys past the header's ports, where nothing
			// says otherwise
			var keeping = reaches.Where(r => r.Starts.Where(p => p <= PortNames.Count).All(accepted.Contains)).ToList();
			foreach (var r in keeping) {
				accepted.UnionWith(r.Starts);
			}
			RgbPorts = accepted.OrderBy(p => p).ToArray();
			var reach = keeping.Select(r => r.Extent).DefaultIfEmpty(0).Max();
			PortExtent = Math.Max(Math.Max(PortNames.Count, reach), RgbPorts.Count > 0 ? RgbPorts[RgbPorts.Count - 1] + 2 : 0);
		}

		#region As libdof reads the rows

		/// <summary>The <c>[Variables DOF]</c> section: <c>name=value</c> per line, the last of a name counting.</summary>
		private static IReadOnlyList<KeyValuePair<string, string>> Variables(IEnumerable<string> lines)
		{
			var variables = new List<KeyValuePair<string, string>>();
			foreach (var line in lines) {
				var p = line.IndexOf('=');
				if (p > 0) {
					var key = line.Substring(0, p).Trim();
					variables.RemoveAll(v => v.Key == key);
					variables.Add(new KeyValuePair<string, string>(key, line.Substring(p + 1).Trim()));
				}
			}
			return variables;
		}

		/// <summary>
		/// The <c>[TableVariables]</c> section: <c>rom,name=value/name={value with /}</c> per line, the first of a name counting
		/// (libdof's TableVariablesDictionary).
		/// </summary>
		private static IReadOnlyDictionary<string, List<KeyValuePair<string, string>>> TableVariables(IEnumerable<string> lines)
		{
			var tables = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);
			foreach (var line in lines) {
				var tp = line.IndexOf(',');
				if (tp <= 0) {
					continue;
				}
				var table = line.Substring(0, tp).Trim();
				if (!tables.TryGetValue(table, out var variables)) {
					tables[table] = variables = new List<KeyValuePair<string, string>>();
				}
				var vd = line.Substring(tp + 1).Trim();
				int pos = 0, last = 0;
				while (pos < vd.Length) {
					pos = vd.IndexOf('=', last);
					if (pos < 0 || pos - last < 1) {
						break;
					}
					var name = vd.Substring(last, pos - last).Trim();
					var value = "";
					pos++;
					if (pos < vd.Length) {
						last = pos;
						if (vd[pos] == '{') {
							pos = vd.IndexOf('}', last);
							if (pos < 0) {
								break;
							}
							value = vd.Substring(last + 1, pos - last - 1);
							pos++;
							while (pos < vd.Length && vd[pos] == ' ') {
								pos++;
							}
							if (pos < vd.Length && vd[pos] != '/') {
								break;
							}
						} else {
							pos = vd.IndexOf('/', last);
							if (pos < 0) {
								pos = vd.Length;
							}
							value = vd.Substring(last, pos - last).Trim();
						}
					}
					if (!variables.Exists(v => v.Key == name)) {
						variables.Add(new KeyValuePair<string, string>(name, value));
					}
					last = pos + 1;
				}
			}
			return tables;
		}

		/// <summary>The row with the variables of its ROM replaced, then the global ones, each named with or without its <c>@</c>s.</summary>
		private static string Resolve(string row, IReadOnlyList<KeyValuePair<string, string>> variables,
			IReadOnlyDictionary<string, List<KeyValuePair<string, string>>> tableVariables)
		{
			var d = row.Trim();
			var tp = d.IndexOf(',');
			if (tp > 0 && tableVariables.TryGetValue(d.Substring(0, tp).Trim(), out var table)) {
				d = ReplaceAll(d, table);
			}
			return ReplaceAll(d, variables);
		}

		/// <summary>The longest a row may grow to while its variables are replaced: a variable that uses itself grows it each round.</summary>
		private const int MaxResolvedRow = 64 * 1024;

		/// <summary>
		/// Replaces the variables until none is left, a few rounds at most and while the row stays shorter than
		/// <see cref="MaxResolvedRow"/>: libdof replaces them in its hash map's order, in which a variable that uses another one
		/// is resolved or not by chance.
		/// </summary>
		private static string ReplaceAll(string d, IReadOnlyList<KeyValuePair<string, string>> variables)
		{
			for (var round = 0; round < 8; round++) {
				var before = d;
				foreach (var variable in variables) {
					var name = Delimited(variable.Key);
					var count = Occurrences(d, name);
					if (count == 0) {
						continue;
					}
					if (d.Length + (long)count * (variable.Value.Length - name.Length) > MaxResolvedRow) {
						return d;
					}
					d = d.Replace(name, variable.Value);
				}
				if (d == before) {
					break;
				}
			}
			return d;
		}

		private static int Occurrences(string text, string value)
		{
			var count = 0;
			for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) {
				count++;
			}
			return count;
		}

		private static string Delimited(string name)
			=> (name.StartsWith("@", StringComparison.Ordinal) ? "" : "@") + name + (name.EndsWith("@", StringComparison.Ordinal) ? "" : "@");

		/// <summary>Splits at a separator outside of brackets, as libdof splits a row into columns and a column into settings.</summary>
		private static List<string> Split(string data, char separator)
		{
			var parts = new List<string>();
			int brackets = 0, start = 0;
			for (var p = 0; p < data.Length; p++) {
				if (data[p] == '(') {
					brackets++;
				} else if (data[p] == ')') {
					brackets--;
				}
				if (data[p] == separator && brackets <= 0) {
					parts.Add(data.Substring(start, p - start));
					start = p + 1;
					brackets = 0;
				}
			}
			if (start < data.Length) {
				parts.Add(data.Substring(start));
			}
			return parts;
		}

		/// <summary>
		/// Whether libdof gives a setting a colour, which makes its column an RGB toy (TableConfigSetting.ParseSettingData): a
		/// part after the trigger that is a <c>#RRGGBB</c> or <c>#RRGGBBAA</c> value, or a word of three letters or more that
		/// is none of its keywords, defined in a colour section or not. Null for a setting it drops: one it cannot parse, or
		/// one fixed off.
		/// </summary>
		internal static bool? Colour(string setting)
		{
			var s = setting.Trim().ToUpperInvariant();
			if (s.StartsWith("(", StringComparison.Ordinal)) {
				var depth = 1;
				var close = -1;
				for (var i = 1; i < s.Length; i++) {
					if (s[i] == '(') {
						depth++;
					} else if (s[i] == ')') {
						depth--;
					}
					if (depth == 0) {
						close = i;
						break;
					}
				}
				if (close < 0) {
					return null;
				}
				s = s.Substring(close + 1).Trim();
			} else {
				if (s.Length == 0) {
					return null;
				}
				var end = -1;
				var lastChar = '\0';
				for (var i = 0; i < s.Length - 1; i++) {
					if (s[i] == ' ' && lastChar != '|' && lastChar != '\0') {
						end = i;
						break;
					}
					if (s[i] != ' ') {
						lastChar = s[i];
					}
				}
				var trigger = s.Substring(0, end < 0 ? s.Length : end).Trim();
				if (trigger == "OFF" || trigger == "0") {
					return null;
				}
				if (trigger != "ON" && trigger != "1" && trigger != "B" && trigger != "BLINK" && !IsTrigger(trigger)) {
					return null;
				}
				s = s.Substring(trigger.Length).Trim();
			}
			var colour = false;
			var integers = 0;
			foreach (var part in s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
				if (IsKeyword(part)) {
					continue;
				}
				if (IsInteger(part)) {
					if (++integers > 2) {
						return null;
					}
				} else if (IsHexColour(part) || part.Length > 2 && part.All(c => c >= 'A' && c <= 'Z' || c == '_')) {
					colour = true;
				} else {
					return null;
				}
			}
			return colour;
		}

		/// <summary>Whether libdof takes the trigger: table elements such as <c>S12</c> or <c>$name</c>, joined by <c>|</c>.</summary>
		private static bool IsTrigger(string trigger)
		{
			foreach (var element in trigger.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)) {
				var e = element.Trim();
				if (e.Length <= 1) {
					return false;
				}
				if (e[0] == '$') {
					if (!e.Skip(1).All(c => c < 128 && char.IsLetterOrDigit(c) || c == '_')) {
						return false;
					}
				} else if ("LWSGMNEDCB".IndexOf(e[0]) < 0 || !IsInteger(e.Substring(1))) {
					return false;
				}
			}
			return true;
		}

		/// <summary>The parts of a setting, upper case, that libdof reads as something other than a colour or a plain number.</summary>
		private static bool IsKeyword(string p)
		{
			bool Number(string prefix) => p.Length > prefix.Length && p.StartsWith(prefix, StringComparison.Ordinal) && IsInteger(p.Substring(prefix.Length));
			bool Sized(string prefix, int length) => p.Length == length && p.StartsWith(prefix, StringComparison.Ordinal);
			return p == "BLINK" || p == "INVERT" || p == "NOBOOL"
				|| Number("APS") || Number("APD") || p.Length > 3 && (p.StartsWith("APC", StringComparison.Ordinal) || p.StartsWith("SHP", StringComparison.Ordinal))
				|| Number("ABT") || Number("ABL") || Number("ABW") || Number("ABH") || Number("ABF")
				|| Number("AAS") || Number("AAC") || Number("AAF") || Sized("AAD", 4) || Sized("AAB", 4)
				|| Number("AFDEN") || Number("AFMIN") || Number("AFMAX") || Number("AFFADE")
				|| Number("AT") || Number("AL") || Number("AW") || Number("AH") || Number("AA") || Number("ASA") || Number("AS") || Number("ASS")
				|| p.Length > 5 && p.StartsWith("ASS", StringComparison.Ordinal) && p.EndsWith("MS", StringComparison.Ordinal) && IsInteger(p.Substring(3, p.Length - 5))
				|| Sized("AD", 3) || Sized("ASD", 4)
				|| Number("MAX") || Number("BNP") || Number("BNPW") || Number("BPW")
				|| p.Length > 3 && p.StartsWith("BL#", StringComparison.Ordinal) && IsHex(p.Substring(3)) || Number("BL") || Number("E")
				|| p.Length > 2 && p.StartsWith("I#", StringComparison.Ordinal) && IsHex(p.Substring(2))
				|| Number("I") || Number("L") || Number("W") || Number("M") || Number("F") || Number("FU") || Number("FD");
		}

		/// <summary>libdof's IsInteger: an optional minus, then digits.</summary>
		private static bool IsInteger(string s) => s.Length > 0 && s.Skip(s[0] == '-' ? 1 : 0).All(c => c >= '0' && c <= '9');

		private static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);

		#endregion

		private static bool IsHexColour(string part)
		{
			if (part.Length != 7 && part.Length != 9 || part[0] != '#') {
				return false;
			}
			for (var i = 1; i < part.Length; i++) {
				if (!Uri.IsHexDigit(part[i])) {
					return false;
				}
			}
			return true;
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
			// The header ends with a comma: drop the empty field after it, but not a quoted one, which names a port.
			if (fields.Count > 0 && fields[fields.Count - 1].Length == 0 && header.TrimEnd().EndsWith(",", StringComparison.Ordinal)) {
				fields.RemoveAt(fields.Count - 1);
			}
			return fields;
		}
	}
}
