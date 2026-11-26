// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>One ini device of a configuration.</summary>
	public sealed class DofCatalogDevice
	{
		/// <summary>The N of directoutputconfigN.ini.</summary>
		public int Number { get; }
		public string FileName { get; }
		/// <summary>Port names from the ini header (index 0 is port 1); empty if the file has none.</summary>
		public IReadOnlyList<string> Ports { get; }
		/// <summary>The ROM names of its rows, in file order.</summary>
		public IReadOnlyList<string> Roms { get; }

		internal DofCatalogDevice(int number, string fileName, IReadOnlyList<string> ports, IReadOnlyList<string> roms)
		{
			Number = number;
			FileName = fileName;
			Ports = ports;
			Roms = roms;
		}
	}

	/// <summary>An output controller of the cabinet file.</summary>
	public sealed class DofCatalogController
	{
		/// <summary>The XML element name, such as <c>LedWiz</c> or <c>TeensyStripController</c>.</summary>
		public string Type { get; }
		/// <summary>The name libdof gives it (<see cref="DofXml.ControllerName"/>), or the type when it names it itself.</summary>
		public string Name { get; }
		/// <summary>Whether the cabinet names it; otherwise libdof names it after its number or type.</summary>
		public bool Named { get; }
		/// <summary>
		/// The serial port the controller names (<c>ComPort</c> for PinControl and PinOne, <c>ComPortName</c> for the
		/// Teensy and Wemos strip controllers), or null.
		/// </summary>
		public string SerialPort { get; }

		internal DofCatalogController(string type, string name, string serialPort)
		{
			Type = type;
			Named = name != null;
			Name = name ?? type;
			SerialPort = serialPort;
		}
	}

	/// <summary>What a revision contains, for the settings UI and the per-game bindings.</summary>
	public sealed class DofCatalog
	{
		public DofRevision Revision { get; }
		public IReadOnlyList<DofCatalogDevice> Devices { get; }
		/// <summary>
		/// The table rows libdof uses, with their device, in file order per device: what libdof read when the revision
		/// was imported (it skips a device file without colours, for one), or VPE's reading of the ini files when the
		/// import was not checked by libdof (<see cref="RowsFromLibdof"/>).
		/// </summary>
		public IReadOnlyList<DofRomRow> Rows { get; }
		/// <summary>Whether <see cref="Rows"/> are libdof's reading of the ini files.</summary>
		public bool RowsFromLibdof { get; }
		/// <summary><c>tablemappings.xml</c>: table name to ROM name.</summary>
		public IReadOnlyList<KeyValuePair<string, string>> TableMappings { get; }
		/// <summary>Table XML configs that a game can bind (see <see cref="DofRevision.TableFileFor"/>).</summary>
		public IReadOnlyList<string> TableConfigs { get; }
		public IReadOnlyList<DofCatalogController> Controllers { get; }

		/// <summary>
		/// Serial ports the cabinet file names: a game leases them before DOF opens them. libdof's auto-configurators
		/// can still probe other ports (PinOne's falls back to probing every port); the native layer has no allowlist.
		/// </summary>
		public IReadOnlyList<string> SerialPorts => Controllers.Where(c => c.SerialPort != null).Select(c => c.SerialPort)
			.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		private DofCatalog(DofRevision revision, IReadOnlyList<DofCatalogDevice> devices, IReadOnlyList<DofRomRow> rows, bool rowsFromLibdof,
			IReadOnlyList<KeyValuePair<string, string>> mappings, IReadOnlyList<string> tableConfigs, IReadOnlyList<DofCatalogController> controllers)
		{
			Revision = revision;
			Devices = devices;
			Rows = rows;
			RowsFromLibdof = rowsFromLibdof;
			TableMappings = mappings;
			TableConfigs = tableConfigs;
			Controllers = controllers;
		}

		/// <summary>
		/// Reads the catalog of a revision. Uses no native code: the rows are those libdof read at the import, or VPE's
		/// reading of the ini files.
		/// </summary>
		public static DofCatalog Load(DofRevision revision)
		{
			var devices = new List<DofCatalogDevice>();
			var cabinetControllers = new List<DofCatalogController>();
			var mappings = new List<KeyValuePair<string, string>>();
			var legacy = new List<DofIniFile>();
			var current = new List<DofIniFile>();
			foreach (var path in Directory.GetFiles(revision.ConfigDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
				var name = Path.GetFileName(path);
				if (DofIniFile.IsIniName(name)) {
					if (DofIniFile.DeviceOf(name, out var isLegacy) >= 0) {
						(isLegacy ? legacy : current).Add(DofIniFile.Load(path));
					}
					continue;
				}
				if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.Equals(DofGlobalConfig.FileName, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				var xml = DofXml.Load(path);
				switch (DofXml.KindOf(xml)) {
					case DofXmlKind.Cabinet:
						// The elements as libdof finds them, by the names as written: a default namespace changes nothing.
						foreach (var controller in CabinetControllers(xml)) {
							var type = DofXml.LiteralName(controller);
							cabinetControllers.Add(new DofCatalogController(type, DofXml.ControllerName(controller), DofXml.SerialPortOf(controller)));
						}
						break;
					case DofXmlKind.TableMappings:
						foreach (var mapping in DofXml.Children(xml.Root, "Mapping")) {
							var table = DofXml.Value(mapping, "TableName");
							var rom = DofXml.Value(mapping, "RomName");
							if (!string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(rom)) {
								mappings.Add(new KeyValuePair<string, string>(table, rom));
							}
						}
						break;
				}
			}
			// libdof reads ledcontrol files only without directoutputconfig files.
			var inis = current.Count > 0 ? current : legacy;
			var rows = new List<DofRomRow>();
			foreach (var ini in inis.OrderBy(i => i.Device)) {
				devices.Add(new DofCatalogDevice(ini.Device, ini.Name, ini.PortNames, ini.Roms));
				rows.AddRange(ini.Roms.Select(r => new DofRomRow(ini.Device, r)));
			}
			var effective = revision.ReadLibdofRows();
			return new DofCatalog(revision, devices, effective ?? rows, effective != null, mappings, revision.TableConfigs, cabinetControllers);
		}

		/// <summary>
		/// The controller elements of a cabinet file, as libdof reads them: the children of the root's
		/// <c>OutputControllers</c>, when the root is <c>Cabinet</c> as written (a prefixed root is no cabinet to libdof).
		/// </summary>
		private static IEnumerable<System.Xml.Linq.XElement> CabinetControllers(System.Xml.Linq.XDocument xml)
			=> xml.Root == null || DofXml.LiteralName(xml.Root) != "Cabinet"
				? Enumerable.Empty<System.Xml.Linq.XElement>()
				: DofXml.FirstChild(xml.Root, "OutputControllers")?.Elements() ?? Enumerable.Empty<System.Xml.Linq.XElement>();

		/// <summary>The serial ports the cabinet file of a <c>directoutputconfig</c> folder names.</summary>
		internal static IReadOnlyList<string> SerialPortsOf(string configDir)
		{
			var ports = new List<string>();
			if (!Directory.Exists(configDir)) {
				return ports;
			}
			// Every extension's case, as in Load: outside Windows "*.xml" would not find Cabinet.XML, whose ports DOF opens.
			foreach (var path in Directory.GetFiles(configDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
				var name = Path.GetFileName(path);
				if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.Equals(DofGlobalConfig.FileName, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				var xml = DofXml.Load(path);
				if (DofXml.KindOf(xml) != DofXmlKind.Cabinet) {
					continue;
				}
				foreach (var controller in CabinetControllers(xml)) {
					var port = DofXml.SerialPortOf(controller);
					if (port != null && !ports.Contains(port, StringComparer.OrdinalIgnoreCase)) {
						ports.Add(port);
					}
				}
			}
			return ports;
		}

		/// <summary>The rows libdof uses for a ROM, one per device at most.</summary>
		public IReadOnlyList<DofRomRow> Match(string rom) => DofRomMatcher.Match(Rows, rom);

		/// <summary>
		/// Rows that match the ROM but that libdof does not use, because another device matched it more closely.
		/// </summary>
		public IReadOnlyList<DofRomRow> Shadowed(string rom)
		{
			var matched = Match(rom);
			return DofRomMatcher.Candidates(Rows, rom).Where(r => matched.All(m => m.Device != r.Device)).ToList();
		}
	}
}
