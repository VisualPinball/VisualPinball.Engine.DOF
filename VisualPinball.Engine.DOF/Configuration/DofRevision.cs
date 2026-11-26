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
	/// One imported configuration, never changed after the import:
	/// <code>
	/// &lt;revision&gt;/directoutputconfig/   ini files, cabinet, global config, table mappings, shapes, bitmaps
	/// &lt;revision&gt;/tables/               table XML configs, as &lt;name&gt;.xml
	/// &lt;revision&gt;/source/               the imported global config as it was, for later imports
	/// &lt;revision&gt;/import-report.txt
	/// &lt;revision&gt;/libdof-rows.txt      the table rows libdof read when the import was checked
	/// </code>
	/// The revision folder is the base path libdof gets, and it reads nothing outside it.
	/// </summary>
	public sealed class DofRevision
	{
		public const string ConfigFolder = "directoutputconfig";
		public const string TablesFolder = "tables";
		public const string SourceFolder = "source";
		public const string ReportFile = "import-report.txt";
		public const string LibdofRowsFile = "libdof-rows.txt";

		public string Id { get; }
		/// <summary>The revision folder, the base path for <see cref="DofInstance.Create"/>.</summary>
		public string BasePath { get; }
		public string ConfigDir => Path.Combine(BasePath, ConfigFolder);
		public string TablesDir => Path.Combine(BasePath, TablesFolder);
		public string GlobalConfigFile => Path.Combine(ConfigDir, DofGlobalConfig.FileName);

		internal DofRevision(string id, string basePath)
		{
			Id = id;
			BasePath = basePath;
		}

		/// <summary>
		/// The table XML configs the import kept, by name (without extension). An inventory only: a game session loads
		/// none of them (see <see cref="DofGlobalConfig"/>).
		/// </summary>
		public string[] TableConfigs
		{
			get {
				if (!Directory.Exists(TablesDir)) {
					return Array.Empty<string>();
				}
				var files = Directory.GetFiles(TablesDir, "*.xml");
				var names = new string[files.Length];
				for (var i = 0; i < files.Length; i++) {
					names[i] = Path.GetFileNameWithoutExtension(files[i]);
				}
				Array.Sort(names, StringComparer.OrdinalIgnoreCase);
				return names;
			}
		}

		/// <summary>
		/// The serial ports the cabinet file names (see <see cref="DofCatalog.SerialPorts"/>), for the device leases a
		/// game takes before DOF opens them.
		/// </summary>
		public IReadOnlyList<string> ReadSerialPorts() => DofCatalog.SerialPortsOf(ConfigDir);

		/// <summary>The table rows libdof read at the import, or null if the import was not checked by libdof.</summary>
		internal IReadOnlyList<DofRomRow> ReadLibdofRows()
		{
			var path = Path.Combine(BasePath, LibdofRowsFile);
			if (!File.Exists(path)) {
				return null;
			}
			var rows = new List<DofRomRow>();
			foreach (var line in File.ReadAllLines(path)) {
				var separator = line.IndexOf('|');
				if (separator > 0 && int.TryParse(line.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var device)) {
					rows.Add(new DofRomRow(device, line.Substring(separator + 1)));
				}
			}
			return rows;
		}

		internal void WriteLibdofRows(IEnumerable<DofRomRow> rows)
		{
			var text = new StringBuilder();
			foreach (var row in rows) {
				text.Append(row.Device.ToString(CultureInfo.InvariantCulture)).Append('|').Append(row.Rom).Append('\n');
			}
			File.WriteAllText(Path.Combine(BasePath, LibdofRowsFile), text.ToString(), new UTF8Encoding(false));
		}

		/// <summary>The import report written with the revision, or null.</summary>
		public string ReadReport()
		{
			var path = Path.Combine(BasePath, ReportFile);
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}

		/// <summary>A file name made of ASCII letters, digits and <c>-_. </c>, which every platform and code page can represent.</summary>
		internal static string SafeName(string name)
		{
			var safe = new StringBuilder(name.Length);
			foreach (var c in name) {
				safe.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.') ? c : '_');
			}
			var result = safe.ToString().Trim(' ', '.');
			return result.Length == 0 ? "table" : result;
		}
	}
}
