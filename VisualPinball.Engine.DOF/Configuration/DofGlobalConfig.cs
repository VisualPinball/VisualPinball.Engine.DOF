// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>
	/// Writes the <c>GlobalConfig_B2SServer.xml</c> libdof loads for a revision. Every file pattern points into the
	/// revision with an absolute path, so neither libdof's placeholders (<c>{DllDir}</c>, <c>{InstallDir}</c>, ...) nor
	/// its fallback lookups depend on where the player is installed or where the configuration came from.
	/// </summary>
	/// <remarks>
	/// Settings of an imported global config that are not file patterns are kept. The cabinet file and the shapes file
	/// are the imported ones the original patterns name. No table XML config is loaded: the list of table config
	/// patterns is empty, and a game session passes libdof no table file, which also covers revisions imported with a
	/// pattern. libdof reads a table config's effects but not the effects it assigns to the table elements,
	/// so loading one would leave the table's effects without a trigger and, without <c>AddLedControlConfig</c>, drop
	/// the ini rows as well. Logging goes to the host: no log file is configured.
	/// </remarks>
	public static class DofGlobalConfig
	{
		public const string FileName = "GlobalConfig_B2SServer.xml";

		private static readonly string[] PatternElements = {
			"IniFilesPath", "CabinetConfigFilePattern", "TableConfigFilePatterns", "ShapeDefinitionFilePattern", "LogFilePattern",
		};

		/// <summary>The file a pattern names: its last path segment, or null if that holds a placeholder or a wildcard.</summary>
		internal static string FileNameOf(string pattern)
		{
			var name = FilePartOf(pattern);
			return name == null || name.IndexOfAny(new[] { '{', '}', '*', '?' }) >= 0 ? null : name;
		}

		/// <summary>The last path segment of a pattern, wildcards and placeholders included, or null if it has none.</summary>
		internal static string FilePartOf(string pattern)
		{
			if (string.IsNullOrWhiteSpace(pattern)) {
				return null;
			}
			var name = pattern.Trim();
			var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
			if (slash >= 0) {
				name = name.Substring(slash + 1);
			}
			return name.Length == 0 ? null : name;
		}

		/// <summary>Whether a file name matches the last segment of a pattern as libdof matches it: <c>*</c> and <c>?</c>, ignoring case.</summary>
		internal static bool Matches(string filePart, string name)
		{
			var regex = "^" + Regex.Escape(filePart).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
			return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		/// <summary>
		/// Builds the final global config.
		/// </summary>
		/// <param name="imported">The imported global config, or null for the defaults.</param>
		/// <param name="configDir">The revision's <c>directoutputconfig</c> folder (absolute).</param>
		/// <param name="tablesDir">The revision's <c>tables</c> folder (absolute).</param>
		/// <param name="cabinetFile">The cabinet file name in <paramref name="configDir"/>, or null.</param>
		/// <param name="shapesFile">The shapes XML file name in <paramref name="configDir"/>, or null.</param>
		/// <remarks>
		/// The folders are written as libdof opens them (<see cref="DofPaths.ToNative"/>): on Windows a folder whose
		/// path is not plain ASCII is written in its short form, so they have to exist.
		/// </remarks>
		/// <exception cref="DofException">A folder cannot be represented for libdof.</exception>
		public static XDocument Build(XDocument imported, string configDir, string tablesDir, string cabinetFile, string shapesFile)
		{
			configDir = DofPaths.ToNative(configDir);
			tablesDir = DofPaths.ToNative(tablesDir);
			var root = new XElement("GlobalConfig");
			if (imported?.Root != null) {
				foreach (var element in imported.Root.Elements()) {
					if (!PatternElements.Contains(element.Name.LocalName) && element.Name.LocalName != "ClearLogOnSessionStart") {
						root.Add(new XElement(element));
					}
				}
			}
			root.AddFirst(
				new XElement("IniFilesPath", configDir),
				new XElement("CabinetConfigFilePattern", cabinetFile == null ? string.Empty : Path.Combine(configDir, cabinetFile)),
				new XElement("TableConfigFilePatterns"),
				new XElement("ShapeDefinitionFilePattern", shapesFile == null ? string.Empty : Path.Combine(configDir, shapesFile)),
				new XElement("LogFilePattern", string.Empty),
				new XElement("ClearLogOnSessionStart", "false"));
			return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
		}

		/// <summary>The file patterns of an imported global config, for the import report.</summary>
		internal static IEnumerable<(string element, string pattern)> Patterns(XDocument imported)
		{
			var root = imported?.Root;
			if (root == null) {
				yield break;
			}
			foreach (var name in new[] { "IniFilesPath", "CabinetConfigFilePattern", "ShapeDefinitionFilePattern", "LogFilePattern" }) {
				var value = DofXml.Value(root, name);
				if (!string.IsNullOrEmpty(value)) {
					yield return (name, value);
				}
			}
			var tables = root.Element("TableConfigFilePatterns");
			if (tables != null) {
				foreach (var pattern in tables.Elements("FilePattern")) {
					var value = pattern.Value.Trim();
					if (value.Length > 0) {
						yield return ("TableConfigFilePatterns", value);
					}
				}
			}
		}
	}
}
