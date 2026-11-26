// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using VisualPinball.Engine.DOF.Configuration;
using VisualPinball.Plugins;

namespace VisualPinball.Engine.DOF.Player
{
	/// <summary>What a game runs DOF with: the ROM name, the table XML config and the devices to reserve.</summary>
	internal static class DofGameSetup
	{
		internal const string Auto = "auto";
		internal const string None = "none";

		/// <summary>The lease key of the FTDI chip family, which DOF's FT245R boards share with PinDMD 1 (the DMD plugin's key).</summary>
		internal const string FtdiFamily = "usb:pindmd1:auto";

		private const string Ft245Controller = "FT245RBitbangController";

		/// <summary>
		/// The ROM name DOF matches the configuration rows with, and why no rows match it, if none do.
		/// </summary>
		/// <remarks>
		/// An explicit setting is used as is. Automatic takes, in order, the first that has rows: the provider-qualified ROM
		/// (<c>&lt;provider&gt;_&lt;rom&gt;</c>, when a row declares it), the ROM, and the DOF name the table declares. Without
		/// rows for any of them it uses the ROM, or else the table's name, and says so. Table mappings, which go by the
		/// table's title, are only offered as a choice (<see cref="TableMapping"/>), never applied on their own.
		/// </remarks>
		/// <param name="setting">The game's ROM setting: <see cref="Auto"/>, or a name to use as is.</param>
		/// <param name="identity">The game.</param>
		/// <param name="runningRom">The ROM the gamelogic engine plays (PinMAME can play a compatible one), or null.</param>
		/// <param name="gameName">The DOF game name the table sets on its <c>DofTriggerComponent</c>, or null.</param>
		/// <param name="catalog">The configuration, or null.</param>
		/// <param name="tableConfig">
		/// The table config the game runs with (<see cref="ResolveTableConfig"/>), or null. Its effects go by the table's
		/// elements, not by rows, so without rows the game is not left without effects.
		/// </param>
		internal static (string Rom, string Problem) ResolveRom(string setting, GameIdentity identity, string runningRom, string gameName, DofCatalog catalog,
			string tableConfig = null)
		{
			if (!string.IsNullOrWhiteSpace(setting) && !string.Equals(setting, Auto, StringComparison.Ordinal)) {
				var chosen = setting.Trim();
				return (chosen, catalog != null && catalog.Match(chosen).Count == 0 ? NoRows(chosen, tableConfig) : null);
			}
			var rom = !string.IsNullOrWhiteSpace(runningRom) ? runningRom.Trim() : string.IsNullOrWhiteSpace(identity.Key) ? null : identity.Key.Trim();
			var declared = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim();
			if (catalog != null) {
				if (rom != null && !string.IsNullOrWhiteSpace(identity.Provider)) {
					var qualified = identity.Provider.Trim() + "_" + rom;
					if (catalog.Rows.Any(r => string.Equals(r.Rom, qualified, StringComparison.OrdinalIgnoreCase))) {
						return (qualified, null);
					}
				}
				if (rom != null && catalog.Match(rom).Count > 0) {
					return (rom, null);
				}
				if (declared != null && catalog.Match(declared).Count > 0) {
					return (declared, null);
				}
			}
			var fallback = rom ?? declared;
			if (fallback == null) {
				return (null, tableConfig == null
					? "The game has no ROM and the table declares no DOF game name; choose one in the game's DOF settings."
					: $"The game has no ROM and the table declares no DOF game name, so only what the table config {tableConfig} defines responds; "
						+ "choose a game name in the game's DOF settings for the configuration's rows.");
			}
			return (fallback, catalog != null ? NoRows(fallback, tableConfig) : null);
		}

		private static string NoRows(string rom, string tableConfig) => tableConfig == null
			? $"The configuration has no rows for {rom}, so nothing in the cabinet responds to this game."
			: $"The configuration has no rows for {rom}, so only what the table config {tableConfig} defines responds to this game.";

		/// <summary>The table mapping for one of the table's names: the mapped name and the ROM, or null.</summary>
		internal static KeyValuePair<string, string>? TableMapping(DofCatalog catalog, IEnumerable<string> tableNames)
		{
			if (catalog == null) {
				return null;
			}
			foreach (var name in tableNames ?? Enumerable.Empty<string>()) {
				if (string.IsNullOrWhiteSpace(name)) {
					continue;
				}
				foreach (var mapping in catalog.TableMappings) {
					if (string.Equals(mapping.Key.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(mapping.Value)) {
						return new KeyValuePair<string, string>(mapping.Key.Trim(), mapping.Value.Trim());
					}
				}
			}
			return null;
		}

		/// <summary>The table XML config to bind (a name from <paramref name="tableConfigs"/>), or null for none.</summary>
		/// <param name="setting"><see cref="Auto"/>, <see cref="None"/> or a config name.</param>
		/// <param name="tableConfigs">The configs of the revision.</param>
		/// <param name="tableNames">Names of the table automatic binding looks for, in order.</param>
		internal static string ResolveTableConfig(string setting, IReadOnlyList<string> tableConfigs, IEnumerable<string> tableNames)
		{
			if (string.Equals(setting, None, StringComparison.Ordinal) || tableConfigs == null || tableConfigs.Count == 0) {
				return null;
			}
			if (!string.IsNullOrWhiteSpace(setting) && !string.Equals(setting, Auto, StringComparison.Ordinal)) {
				return tableConfigs.FirstOrDefault(c => string.Equals(c, setting, StringComparison.OrdinalIgnoreCase));
			}
			foreach (var name in tableNames ?? Enumerable.Empty<string>()) {
				if (string.IsNullOrWhiteSpace(name)) {
					continue;
				}
				var match = tableConfigs.FirstOrDefault(c => string.Equals(c, name.Trim(), StringComparison.OrdinalIgnoreCase));
				if (match != null) {
					return match;
				}
			}
			return null;
		}

		/// <summary>The names a table goes by for table mappings and table configs: its package file name, then its title.</summary>
		internal static IEnumerable<string> TableNames(string packagePath, string title)
		{
			if (!string.IsNullOrEmpty(packagePath)) {
				yield return Path.GetFileNameWithoutExtension(packagePath);
			}
			if (!string.IsNullOrEmpty(title)) {
				yield return title;
			}
		}

		/// <summary>
		/// The keys a game or a port test reserves before DOF opens its devices: each serial port the cabinet names, and
		/// the FTDI family when DOF may open an FT245R board, because the cabinet names one or because DOF configures
		/// connected boards itself. They are the keys the DMD plugin uses for the same hardware.
		/// </summary>
		/// <param name="serialPorts">Serial ports the configuration names.</param>
		/// <param name="usesFtdi">Whether DOF may open an FT245R board (<see cref="UsesFtdi"/>).</param>
		internal static IReadOnlyList<string> LeaseKeys(IEnumerable<string> serialPorts, bool usesFtdi)
		{
			var keys = new List<string>();
			foreach (var port in serialPorts ?? Enumerable.Empty<string>()) {
				var canonical = CanonicalPort(port);
				if (canonical.Length > 0 && canonical.Length <= 256 && !keys.Contains("serial:" + canonical, StringComparer.OrdinalIgnoreCase)) {
					keys.Add("serial:" + canonical);
				}
			}
			if (usesFtdi) {
				keys.Add(FtdiFamily);
			}
			return keys;
		}

		/// <summary>A lease key as the user knows the device.</summary>
		internal static string Describe(string key) => key == FtdiFamily
			? "The FTDI board (FT245R, also used by PinDMD 1)"
			: key.StartsWith("serial:", StringComparison.Ordinal) ? key.Substring(7) : key;

		/// <summary>The lease keys of a revision (<see cref="LeaseKeys"/>).</summary>
		internal static IReadOnlyList<string> LeaseKeys(DofRevision revision, DofCatalog catalog)
			=> LeaseKeys(revision.ReadSerialPorts(), UsesFtdi(revision, catalog));

		/// <summary>A port name as the DMD plugin writes it: <c>COM3</c> for <c>\\.\COM3</c> or <c>com03</c>.</summary>
		internal static string CanonicalPort(string port)
		{
			port = (port ?? string.Empty).Trim();
			if (port.StartsWith(@"\\.\", StringComparison.Ordinal)) {
				port = port.Substring(4);
			}
			if (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
				&& uint.TryParse(port.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) {
				return "COM" + number.ToString(CultureInfo.InvariantCulture);
			}
			return port;
		}

		/// <summary>
		/// Whether DOF may open an FT245R board, which the DMD plugin's PinDMD 1 driver would also open: the cabinet names
		/// one, or DOF's auto-configuration adds the connected ones.
		/// </summary>
		internal static bool UsesFtdi(DofRevision revision, DofCatalog catalog)
			=> catalog.Controllers.Any(c => string.Equals(c.Type, Ft245Controller, StringComparison.Ordinal)) || AutoConfigEnabled(revision);

		/// <summary>
		/// Whether DOF configures connected devices itself, decided as libdof does (<c>Cabinet::FromXml</c> and
		/// <c>Pinball::Init</c>): when the configuration has no cabinet file or it cannot be read as a cabinet, and when
		/// the cabinet's <c>AutoConfigEnabled</c> is missing or has no text. Otherwise only the text <c>true</c>, in any
		/// case and without surrounding spaces, turns it on: <c>1</c> or <c> true </c> turn it off. Elements are found
		/// and read as libdof's parser does (<see cref="DofXml.FirstChild"/>, <see cref="DofXml.Text"/>): by the names as
		/// written, so a default namespace changes nothing, and with comments before the text skipped.
		/// </summary>
		internal static bool AutoConfigEnabled(DofRevision revision)
		{
			try {
				var global = Load(revision.GlobalConfigFile)?.Root;
				var pattern = global != null && DofXml.LiteralName(global) == "GlobalConfig"
					? DofXml.Text(DofXml.FirstChild(global, "CabinetConfigFilePattern"))
					: null;
				var name = FileNameOf(pattern);
				if (name == null) {
					return true;
				}
				var cabinetFile = Path.Combine(revision.ConfigDir, name);
				if (!File.Exists(cabinetFile)) {
					return true;
				}
				var cabinet = Load(cabinetFile)?.Root;
				if (cabinet == null || DofXml.LiteralName(cabinet) != "Cabinet") {
					return true;
				}
				var text = DofXml.Text(DofXml.FirstChild(cabinet, "AutoConfigEnabled"));
				return string.IsNullOrEmpty(text) || IsTrue(text);
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is XmlException) {
				return true;
			}
		}

		/// <summary>Whether the text is <c>true</c>, ignoring the case of ASCII letters only, as libdof's ToLower does.</summary>
		private static bool IsTrue(string text)
		{
			const string True = "true";
			if (text.Length != True.Length) {
				return false;
			}
			for (var i = 0; i < True.Length; i++) {
				var c = text[i];
				if (c >= 'A' && c <= 'Z') {
					c = (char)(c + ('a' - 'A'));
				}
				if (c != True[i]) {
					return false;
				}
			}
			return true;
		}

		private static string FileNameOf(string pattern)
		{
			if (string.IsNullOrWhiteSpace(pattern)) {
				return null;
			}
			var name = pattern.Trim();
			var slash = System.Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
			if (slash >= 0) {
				name = name.Substring(slash + 1);
			}
			return name.Length == 0 || name.IndexOfAny(new[] { '{', '}', '*', '?' }) >= 0 ? null : name;
		}

		private static XDocument Load(string path)
		{
			if (!File.Exists(path)) {
				return null;
			}
			// Text that is only whitespace is dropped, as tinyxml2 does. Comments stay: DofXml.Text skips them as tinyxml2 does.
			var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
			using (var reader = XmlReader.Create(path, settings)) {
				return XDocument.Load(reader);
			}
		}
	}
}
