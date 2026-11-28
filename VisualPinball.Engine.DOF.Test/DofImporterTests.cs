// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using NUnit.Framework;
using VisualPinball.Engine.DOF.Configuration;

namespace VisualPinball.Engine.DOF.Test
{
	/// <summary>Imports into a store in a temporary folder, validated by the real native library.</summary>
	[TestFixture, NonParallelizable]
	public class DofImporterTests
	{
		private const string Header = "[version]\r\nversion=5467\r\n\r\n[Colors DOF]\r\nRed=#FF0000FF\r\n\r\n[Config DOF]\r\n";
		private static readonly string MainIni = Header + "#\"Rom\",\"Flipper Left\",\"Knocker\",\r\nafm,S1,S2\r\nt2,S3,0\r\n";
		private static readonly string SecondIni = Header + "afm,L5\r\n";

		private const string Cabinet = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Cabinet><Name>Test</Name><AutoConfigEnabled>false</AutoConfigEnabled>"
			+ "<OutputControllers><PinControl><Name>PinControl 1</Name><Number>1</Number><ComPort>COM3</ComPort></PinControl>"
			+ "<TeensyStripController><Name>Teensy 1</Name><ComPortName>COM5</ComPortName></TeensyStripController>"
			+ "<ArtNet><Name>ArtNet 1</Name><Universe>1</Universe><BroadcastAddress>127.0.0.1</BroadcastAddress></ArtNet></OutputControllers>"
			+ "<Toys><LedWizEquivalent><Name>LedWizEquivalent 1</Name><LedWizNumber>1</LedWizNumber><Outputs /></LedWizEquivalent><RGBAToy><Name>Rgb</Name></RGBAToy></Toys></Cabinet>\r\n";

		// As DOF's installer writes it on Windows: absolute paths with backslashes, a log file, and a table folder.
		private const string WindowsGlobalConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<GlobalConfig>"
			+ "<LedWizDefaultMinCommandIntervalMs>1</LedWizDefaultMinCommandIntervalMs>"
			+ "<IniFilesPath>C:\\DirectOutput\\Config</IniFilesPath>"
			+ "<CabinetConfigFilePattern>C:\\DirectOutput\\Config\\cabinet.xml</CabinetConfigFilePattern>"
			+ "<TableConfigFilePatterns><FilePattern>C:\\Visual Pinball\\Tables\\{TableName}.xml</FilePattern></TableConfigFilePatterns>"
			+ "<EnableLogging>true</EnableLogging><ClearLogOnSessionStart>true</ClearLogOnSessionStart>"
			+ "<LogFilePattern>C:\\DirectOutput\\DirectOutput.log</LogFilePattern></GlobalConfig>\r\n";

		private const string Mappings = "<?xml version=\"1.0\"?>\r\n<TableNameMappings><Mapping><TableName>Attack From Mars</TableName><RomName>afm</RomName></Mapping></TableNameMappings>\r\n";
		private const string Shapes = "<?xml version=\"1.0\"?>\r\n<ShapeDefinitions><BitmapFilePattern>DirectOutputShapes.png</BitmapFilePattern><Shapes /></ShapeDefinitions>\r\n";
		private const string TableXml = "<?xml version=\"1.0\"?>\r\n<Table><TableName>afm</TableName><AddLedControlConfig>true</AddLedControlConfig></Table>\r\n";

		private string _dir;
		private DofConfigStore _store;
		private DofImporter _importer;

		[SetUp]
		public void SetUp()
		{
			DofLibrary.EnsureCompatible();
			_dir = Path.Combine(Path.GetTempPath(), "vpe-dof-import", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_dir);
			_store = new DofConfigStore(Path.Combine(_dir, "store"));
			_importer = new DofImporter(_store);
		}

		[TearDown]
		public void TearDown()
		{
			try {
				Directory.Delete(_dir, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}

		private string Zip(string name, params (string entry, string content)[] entries)
		{
			var path = Path.Combine(_dir, name);
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) {
				foreach (var (entry, content) in entries) {
					using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
					writer.Write(content);
				}
			}
			return path;
		}

		private string Folder(string name, params (string file, string content)[] files)
		{
			var path = Path.Combine(_dir, name);
			foreach (var (file, content) in files) {
				var target = Path.Combine(path, file.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				File.WriteAllText(target, content, new UTF8Encoding(false));
			}
			return path;
		}

		private static DofImportReport Accepted(DofImportReport report)
		{
			Assert.That(report.Accepted, Is.True, report.ToString());
			return report;
		}

		private static DofImportReport Rejected(DofImportReport report, string reason)
		{
			Assert.That(report.Accepted, Is.False, report.ToString());
			Assert.That(report.RejectionReason, Does.Contain(reason));
			return report;
		}

		private static bool Has(DofImportReport report, DofReportSeverity severity, string text) => report.Entries.Any(e => e.Severity == severity && e.Message.Contains(text));

		[Test]
		public void ImportsAConfigToolZip()
		{
			var report = Accepted(_importer.Import(Zip("config.zip",
				("directoutputconfig.ini", MainIni), ("directoutputconfig51.ini", SecondIni), ("tablemappings.xml", Mappings),
				("DirectOutputShapes.xml", Shapes), ("DirectOutputShapes.png", "png"), ("readme.txt", "hello"))));
			var revision = _store.Active;
			Assert.That(revision.Id, Is.EqualTo(report.Revision));
			Assert.That(Has(report, DofReportSeverity.Info, "libdof reads 3 table rows for 2 devices"), Is.True, report.ToString());
			Assert.That(Has(report, DofReportSeverity.Info, "Skipped readme.txt"), Is.True);
			Assert.That(revision.ReadReport(), Does.Contain("libdof reads"));
			Assert.That(revision.ReadOrigin(), Is.EqualTo("config.zip"));
			Assert.That(revision.CreatedUtc, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)));
			Assert.That(revision.CountWarnings(), Is.EqualTo(report.Entries.Count(e => e.Severity == DofReportSeverity.Warning)));

			var catalog = DofCatalog.Load(revision);
			Assert.That(catalog.Rows, Is.EqualTo(new[] { new DofRomRow(1, "afm"), new DofRomRow(1, "t2"), new DofRomRow(51, "afm") }));
			Assert.That(catalog.Devices[0].Ports, Is.EqualTo(new[] { "Flipper Left", "Knocker" }));
			Assert.That(catalog.TableMappings, Is.EqualTo(new[] { new KeyValuePair<string, string>("Attack From Mars", "afm") }));
			Assert.That(catalog.Match("afm_113b").Select(r => r.Device), Is.EqualTo(new[] { 1, 51 }));
			Assert.That(catalog.HasCabinet, Is.False);

			// Without a cabinet the generated global config leaves the cabinet empty, so DOF configures itself.
			var global = XDocument.Load(revision.GlobalConfigFile).Root;
			Assert.That(global.Element("IniFilesPath").Value, Is.EqualTo(revision.ConfigDir));
			Assert.That(global.Element("CabinetConfigFilePattern").Value, Is.Empty);
			Assert.That(global.Element("ShapeDefinitionFilePattern").Value, Is.EqualTo(Path.Combine(revision.ConfigDir, "DirectOutputShapes.xml")));
			Assert.That(File.Exists(Path.Combine(revision.ConfigDir, "DirectOutputShapes.png")), Is.True);
		}

		[Test]
		public void CatalogKnowsTheOutputsTheCabinetMaps()
		{
			var outputs = string.Concat(new[] { 1, 4, 2 }.Select(n => $"<LedWizEquivalentOutput><OutputName>Out {n}</OutputName><LedWizEquivalentOutputNumber>{n}</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>"));
			var cabinet = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Cabinet><Name>Test</Name><OutputControllers /><Toys>"
				+ $"<LedWizEquivalent><Name>LedWizEquivalent 30</Name><LedWizNumber>30</LedWizNumber><Outputs>{outputs}</Outputs></LedWizEquivalent>"
				+ "<LedWizEquivalent><Name>Broken</Name><LedWizNumber>x</LedWizNumber><Outputs>" + outputs + "</Outputs></LedWizEquivalent>"
				+ "</Toys></Cabinet>\r\n";
			Accepted(_importer.Import(Folder("Mapped", ("Config/cabinet.xml", cabinet), ("Config/directoutputconfig30.ini", Header + "#\"Rom\",\"Knocker\"\r\nrom,S1,0,0,0\r\n"))));
			var catalog = DofCatalog.Load(_store.Active);
			Assert.That(catalog.CabinetOutputs, Is.EqualTo(new Dictionary<int, int> { [30] = 4 }), "the highest output, by the device it stands for");
			Assert.That(catalog.Devices.Single().PortCount, Is.EqualTo(1), "the ini itself puts nothing past the knocker");
		}

		[Test]
		public void RelocatesAWindowsAuthoredConfiguration()
		{
			_importer.WindowsPortNames = false;
			var report = Accepted(_importer.Import(Folder("DirectOutput",
				("Config/GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("Config/cabinet.xml", Cabinet), ("Config/directoutputconfig.ini", MainIni))));
			var revision = _store.Active;
			var global = XDocument.Load(revision.GlobalConfigFile).Root;
			Assert.That(global.Element("IniFilesPath").Value, Is.EqualTo(revision.ConfigDir));
			Assert.That(global.Element("CabinetConfigFilePattern").Value, Is.EqualTo(Path.Combine(revision.ConfigDir, "cabinet.xml")));
			Assert.That(global.Element("TableConfigFilePatterns").Elements(), Is.Empty, "no table config is loaded");
			Assert.That(global.Element("LogFilePattern").Value, Is.Empty);
			Assert.That(global.Element("ClearLogOnSessionStart").Value, Is.EqualTo("false"));
			Assert.That(global.Element("LedWizDefaultMinCommandIntervalMs").Value, Is.EqualTo("1"), "other settings are kept");
			Assert.That(File.ReadAllText(Path.Combine(revision.BasePath, "source", DofGlobalConfig.FileName)), Does.Contain("C:\\DirectOutput"), "the original is kept for merges");

			Assert.That(Has(report, DofReportSeverity.Warning, "COM3, a Windows port name"), Is.True, report.ToString());
			Assert.That(Has(report, DofReportSeverity.Warning, "RGBAToy"), Is.True, "toys libdof skips are named");
			Assert.That(report.Entries.Any(e => e.Topic == DofReportTopic.Relocation && e.Message.Contains("cabinet.xml of this configuration")), Is.True);
			// Each controller's own element: ComPort for PinControl (and PinOne), ComPortName for the strip controllers.
			Assert.That(DofCatalog.Load(revision).SerialPorts, Is.EqualTo(new[] { "COM3", "COM5" }));
			Assert.That(revision.ReadSerialPorts(), Is.EqualTo(new[] { "COM3", "COM5" }));
			Assert.That(revision.ReadOrigin(), Is.EqualTo("the folder DirectOutput"));
			Assert.That(revision.CountWarnings(), Is.EqualTo(report.Entries.Count(e => e.Severity == DofReportSeverity.Warning)).And.GreaterThan(0));
			Assert.That(DofCatalog.Load(revision).HasCabinet, Is.True);
			Assert.That(DofCatalog.Load(revision).CabinetOutputs, Is.Empty, "a LedWiz equivalent without outputs maps none");

			// The serial port gets this computer's name in a new revision; the import itself is unchanged.
			var overridden = Accepted(_importer.ApplySerialOverrides(new Dictionary<string, string> { ["PinControl 1"] = "/dev/ttyACM0", ["Teensy 1"] = "/dev/ttyACM1" }));
			Assert.That(overridden.Revision, Is.Not.EqualTo(revision.Id));
			Assert.That(DofCatalog.Load(_store.Active).SerialPorts, Is.EqualTo(new[] { "/dev/ttyACM0", "/dev/ttyACM1" }));
			Assert.That(XDocument.Load(Path.Combine(_store.Active.ConfigDir, "cabinet.xml")).Root.Descendants("PinControl").Single().Element("ComPort").Value, Is.EqualTo("/dev/ttyACM0"));
			Assert.That(_store.Previous.Id, Is.EqualTo(revision.Id));
			Rejected(_importer.ApplySerialOverrides(new Dictionary<string, string> { ["ArtNet 1"] = "/dev/ttyACM1" }), "has no serial port");
			Rejected(_importer.ApplySerialOverrides(new Dictionary<string, string> { ["Nope"] = "/dev/ttyACM1" }), "no controller named");
		}

		[Test]
		public void SerialPortsGoIntoTheImportSoUndoRestoresTheConfigurationBeforeIt()
		{
			Accepted(_importer.Import(Zip("before.zip", ("directoutputconfig.ini", MainIni))));
			var before = _store.Active.Id;

			var report = Accepted(_importer.Import(Folder("ported",
				("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("cabinet.xml", Cabinet), ("directoutputconfig.ini", MainIni)),
				DofImportMode.Replace, new Dictionary<string, string> { ["PinControl 1"] = "/dev/ttyACM0", ["Nope"] = "/dev/ttyACM1" }));
			Assert.That(XDocument.Load(Path.Combine(_store.Active.ConfigDir, "cabinet.xml")).Root.Descendants("PinControl").Single().Element("ComPort").Value, Is.EqualTo("/dev/ttyACM0"));
			Assert.That(Has(report, DofReportSeverity.Warning, "no controller named \"Nope\""), Is.True, report.ToString());
			Assert.That(_store.Previous.Id, Is.EqualTo(before), "one revision for the import and its ports");

			Assert.That(_store.Rollback(), Is.True);
			Assert.That(_store.Active.Id, Is.EqualTo(before));
		}

		[Test]
		public void ReadsACabinetWithADefaultNamespaceAsLibdofDoes()
		{
			// tinyxml2 matches element names as written, so libdof reads this cabinet as one without the namespace.
			var cabinet = Cabinet.Replace("<Cabinet>", "<Cabinet xmlns=\"http://example.org/dof\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
			Accepted(_importer.Import(Folder("namespaced",
				("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("cabinet.xml", cabinet), ("directoutputconfig.ini", MainIni))));
			var catalog = DofCatalog.Load(_store.Active);
			Assert.That(catalog.Controllers.Select(c => c.Type), Is.EqualTo(new[] { "PinControl", "TeensyStripController", "ArtNet" }));
			Assert.That(catalog.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "PinControl 1", "Teensy 1", "ArtNet 1" }));
			Assert.That(catalog.SerialPorts, Is.EqualTo(new[] { "COM3", "COM5" }));
			Assert.That(_store.Active.ReadSerialPorts(), Is.EqualTo(new[] { "COM3", "COM5" }));

			// A port for this computer replaces the element libdof reads instead of adding one it would not.
			Accepted(_importer.ApplySerialOverrides(new Dictionary<string, string> { ["Teensy 1"] = "/dev/ttyACM1" }));
			Assert.That(DofCatalog.Load(_store.Active).SerialPorts, Is.EqualTo(new[] { "COM3", "/dev/ttyACM1" }));
			var teensy = XDocument.Load(Path.Combine(_store.Active.ConfigDir, "cabinet.xml")).Root.Descendants().Single(e => e.Name.LocalName == "TeensyStripController");
			Assert.That(teensy.Elements().Count(e => e.Name.LocalName == "ComPortName"), Is.EqualTo(1));
		}

		[Test]
		public void APrefixedCabinetRootIsNoCabinet()
		{
			var revision = new DofRevision("prefixed", Path.Combine(_dir, "prefixed"));
			Directory.CreateDirectory(revision.ConfigDir);
			var cabinet = Path.Combine(revision.ConfigDir, "Cabinet.xml");
			// libdof matches the root as written: a default namespace still makes a cabinet, a prefix does not
			File.WriteAllText(cabinet, "<?xml version=\"1.0\"?><Cabinet xmlns=\"http://example.org/dof\"><AutoConfigEnabled>false</AutoConfigEnabled></Cabinet>");
			Assert.That(DofCatalog.Load(revision).HasCabinet, Is.True);
			File.WriteAllText(cabinet, "<?xml version=\"1.0\"?><d:Cabinet xmlns:d=\"http://example.org/dof\"><d:AutoConfigEnabled>false</d:AutoConfigEnabled></d:Cabinet>");
			Assert.That(DofCatalog.Load(revision).HasCabinet, Is.False);
		}

		[Test]
		public void ReadsElementsAsTinyxml2Does()
		{
			var root = XDocument.Parse("<Cabinet xmlns=\"http://example.org/dof\" xmlns:p=\"http://example.org/p\">"
				+ "<A><!-- off -->false</A><B><C /></B><C>text</C><p:D>prefixed</p:D><E><!-- a --><!-- b --> x </E><F /></Cabinet>").Root;
			Assert.That(DofXml.LiteralName(root), Is.EqualTo("Cabinet"), "a default namespace does not change the name");
			Assert.That(DofXml.FirstChild(root, "D"), Is.Null, "a prefix does");
			Assert.That(DofXml.Text(DofXml.FirstChild(root, "p:D")), Is.EqualTo("prefixed"));
			Assert.That(DofXml.Text(DofXml.FirstChild(root, "A")), Is.EqualTo("false"), "comments before the text are skipped");
			Assert.That(DofXml.Text(DofXml.FirstChild(root, "E")), Is.EqualTo(" x "), "the text is as written");
			Assert.That(DofXml.Text(DofXml.FirstChild(root, "B")), Is.Null, "an element first is no text");
			Assert.That(DofXml.Text(DofXml.FirstChild(root, "C")), Is.EqualTo("text"), "only children count, not deeper elements");
			Assert.That(DofXml.Text(DofXml.FirstChild(root, "F")), Is.Null);
		}

		[Test]
		public void NamesControllersAsLibdofDoes()
		{
			// Each type's FromXml: pac boards take only the attribute (libdof writes them so), a LedWiz only the element,
			// the others the attribute before the element. Without a name libdof names the controller itself.
			const string cabinet = "<?xml version=\"1.0\"?>\r\n<Cabinet><AutoConfigEnabled>false</AutoConfigEnabled><OutputControllers>"
				+ "<PacDrive Name=\"Main\" />"
				+ "<LedWiz Name=\"ignored\"><Name>LW</Name><Number>1</Number></LedWiz>"
				+ "<TeensyStripController Name=\"Strip\"><Name>Other</Name><ComPortName>COM5</ComPortName></TeensyStripController>"
				+ "<PinControl Name=\"\"><Name>ignored</Name><Number>1</Number><ComPort>COM3</ComPort></PinControl>"
				+ "<PacUIO><Name>ignored</Name></PacUIO>"
				+ "</OutputControllers></Cabinet>\r\n";
			Accepted(_importer.Import(Folder("named",
				("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("cabinet.xml", cabinet), ("directoutputconfig.ini", MainIni))));
			var catalog = DofCatalog.Load(_store.Active);
			Assert.That(catalog.Controllers.Select(c => (c.Type, c.Name, c.Named)), Is.EqualTo(new[] {
				("PacDrive", "Main", true),
				("LedWiz", "LW", true),
				("TeensyStripController", "Strip", true),
				("PinControl", "PinControl", false),
				("PacUIO", "PacUIO", false),
			}));

			// A port for a controller named by its attribute reaches it.
			Accepted(_importer.ApplySerialOverrides(new Dictionary<string, string> { ["Strip"] = "/dev/ttyACM1", ["PinControl"] = "/dev/ttyACM0" }));
			Assert.That(DofCatalog.Load(_store.Active).SerialPorts, Is.EquivalentTo(new[] { "/dev/ttyACM1", "/dev/ttyACM0" }));
		}

		[Test]
		public void MergeKeepsTheCabinetAndReplacesAllIniFiles()
		{
			Accepted(_importer.Import(Folder("first",
				("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("cabinet.xml", Cabinet), ("directoutputconfig.ini", MainIni), ("directoutputconfig51.ini", SecondIni),
				("afm.xml", TableXml))));
			var report = Accepted(_importer.Import(Zip("update.zip", ("directoutputconfig.ini", Header + "afm,S7\r\nnewrom,S1\r\n"))));
			Assert.That(Has(report, DofReportSeverity.Info, "Kept from the previous configuration"), Is.True);

			var revision = _store.Active;
			var catalog = DofCatalog.Load(revision);
			Assert.That(catalog.Rows, Is.EqualTo(new[] { new DofRomRow(1, "afm"), new DofRomRow(1, "newrom") }), "device 51 is gone with the old ini set");
			Assert.That(catalog.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "PinControl 1", "Teensy 1", "ArtNet 1" }));
			Assert.That(catalog.TableConfigs, Is.EqualTo(new[] { "afm" }));
			Assert.That(XDocument.Load(revision.GlobalConfigFile).Root.Element("LedWizDefaultMinCommandIntervalMs"), Is.Not.Null, "the imported global config is kept");

			Accepted(_importer.Import(Zip("replace.zip", ("directoutputconfig.ini", MainIni)), DofImportMode.Replace));
			Assert.That(DofCatalog.Load(_store.Active).Controllers, Is.Empty, "replace drops the cabinet");
			Assert.That(_store.Active.TableConfigs, Is.Empty);
		}

		[Test]
		public void RgbToysTakeThreePortsAsLibdofCountsThem()
		{
			// Ports 1-2 single, 3 unassigned, 4-6 an RGB flasher whose name is on its first port, 7 single, and an RGB toy on
			// 8-10 that the header ends before naming its other ports. One row writes the flasher as a colour (one column),
			// another as three plain columns, a third through a variable. "shifted" has a colour in the slingshot's column:
			// that shifts its own columns, not the cabinet's ports.
			var head = "[version]\r\nversion=5467\r\n\r\n[Colors DOF]\r\nRed=#FF0000FF\r\nYellow=#FFFF00FF\r\n\r\n[Variables DOF]\r\nflash=E110 Yellow\r\n\r\n[Config DOF]\r\n";
			var ini = head
				+ "#\"Rom\",\"Slingshot\",\"Coin\",\"\",\"Flasher\",\"\",\"\",\"Knocker\",\"Beacon\",\r\n"
				+ "colour,E1,E2,0,E3 Red w400/E4 #00FF00,S5,E9 #ff00ff00 f200\r\n"
				+ "plain,E1,E2,0,0,0,0,S5\r\n"
				+ "variable,E1,0,0,@flash@,0\r\n"
				+ "shifted,S11 Red 50 AH100,E2,0,0,0,0,0\r\n";
			Accepted(_importer.Import(Zip("config.zip", ("directoutputconfig.ini", ini))));
			var device = DofCatalog.Load(_store.Active).Devices.Single();
			Assert.That(device.RgbPorts, Is.EqualTo(new[] { 4, 8 }));
			Assert.That(device.PortCount, Is.EqualTo(10), "the beacon's blue port, past the header's 8");

			// Without a header, every row's colours count, and the ports as far as a row reaches.
			Accepted(_importer.Import(Zip("plain.zip", ("directoutputconfig.ini", head + "colour,E1,E2,0,E3 Red,S5\r\n"))));
			device = DofCatalog.Load(_store.Active).Devices.Single();
			Assert.That(device.RgbPorts, Is.EqualTo(new[] { 4 }));
			Assert.That(device.PortCount, Is.EqualTo(7));
		}

		[Test]
		public void RgbPortsFollowHowLibdofReadsTheRows()
		{
			Ports("rom,(max(S1,S2)>0) Red,E3", new[] { 1 }, 4, "the comma of a condition does not end its column");
			Ports("[Variables DOF]\r\n@flash@=E1 Red\r\n[Config DOF]\r\nrom,@flash@,S2", new[] { 1 }, 4, "a variable defined with its @s");
			Ports("[TableVariables]\r\nrom,flash=E1 Red/dim={S1 I24/S2}\r\n[Config DOF]\r\nrom,@flash@,@dim@,S3\r\nother,@flash@", new[] { 1 }, 5, "a ROM's own variables");
			Ports("[Colors DOF]\r\nWhite=#FFFFFFFF\r\n[Config DOF]\r\nrom,S1 Orange,S2 White", new[] { 1, 4 }, 6, "any word is a colour to libdof");
			Ports("rom,OFF White,S2", new int[0], 2, "a setting fixed off is dropped");
			Ports("rom,S1 Red 1 2 3,S2 APCRED", new int[0], 2, "a setting libdof cannot parse is dropped, and a plasma's second colour is not the toy's");
			Ports("#\"Rom\",\"Knocker\"\r\nrom,S1,S2", new int[0], 2, "a row reaching past the header");
			Ports("#\"Rom\",\"Knocker\"\r\nrom,S1,S2 Red,S3", new[] { 2 }, 5, "an RGB toy past the header");
			Ports("[Variables DOF]\r\nflashcolour=S1 Red\r\nflash=@flashcolour@\r\n[Config DOF]\r\nrom,@flash@,S2", new[] { 1 }, 4, "a variable that uses another");
			Ports("[Config outs]\r\nold,S1 Red,S2\r\n[Config DOF]\r\nrom,S1,S2", new int[0], 2, "only the [Config DOF] section where there is one");
			Ports("rom,S1,S2\r\nROM,S1 Red,S2", new int[0], 2, "the first row of a ROM");
			Ports("#\"Rom\",\"Knocker\"\r\nrom,S1,0,OFF,0", new int[0], 1, "past the header, columns of 0 put nothing on the board");
			Ports("rom,S1,0,OFF,0", new int[0], 4, "without a header, the columns a row writes are the board's ports");
			Ports("#\"Rom\",\"Knocker\",\"\"\r\nrom,S1,0", new int[0], 2, "a quoted empty name at the header's end is a port");
			Ports("[Config DOF]\r\n#\"Rom\",\"Flasher\",\"\",\"\"\r\nrom,S1 Red\r\n[Config outs]\r\n#\"Rom\",\"A\",\"B\",\"C\"\r\nold,S1", new[] { 1 }, 3,
				"the header of the section the rows come from");
			var timer = System.Diagnostics.Stopwatch.StartNew();
			Ports("[Variables DOF]\r\na=" + string.Concat(Enumerable.Repeat("@a@", 100)) + "\r\n[Config DOF]\r\nrom,@a@,S2", new int[0], 2,
				"a variable that uses itself stops growing the row");
			Assert.That(timer.ElapsedMilliseconds, Is.LessThan(2000));
		}

		private void Ports(string config, int[] rgb, int extent, string because)
		{
			var path = Path.Combine(_dir, "directoutputconfig30.ini");
			File.WriteAllText(path, config.Contains("[Config DOF]") ? config : "[Config DOF]\r\n" + config);
			var ini = DofIniFile.Load(path);
			Assert.That(ini.RgbPorts, Is.EqualTo(rgb), because);
			Assert.That(ini.PortExtent, Is.EqualTo(extent), because);
		}

		[Test]
		public void TheConfigToolsCabinetExportIsNamedAsSuch()
		{
			var export = Path.Combine(_dir, "My Cabinet.json");
			File.WriteAllText(export, "{\"type\":\"cabinet\",\"name\":\"My Cabinet\",\"variables\":{\"pwm\":true},\"devices\":[{\"name\":\"Ledwiz 1\",\"outputs\":32}]}");
			Rejected(_importer.Import(export), "download its zip");
			var other = Path.Combine(_dir, "other.json");
			File.WriteAllText(other, "{\"type\":\"table\"}");
			Rejected(_importer.Import(other), "not a DOF configuration file");
			File.WriteAllText(other, "{\"type\":\"table\",\"settings\":{\"type\":\"cabinet\"}}");
			Rejected(_importer.Import(other), "not a DOF configuration file");
		}

		[Test]
		public void RejectedImportKeepsTheActiveConfiguration()
		{
			Accepted(_importer.Import(Zip("good.zip", ("directoutputconfig.ini", MainIni))));
			var active = _store.Active.Id;
			Rejected(_importer.Import(Zip("bad.zip", ("directoutputconfig.ini", MainIni), ("Cabinet.xml", "<Cabinet><Name>"))), "not valid XML");
			Assert.That(_store.Active.Id, Is.EqualTo(active));
			Assert.That(Directory.GetDirectories(Path.Combine(_store.Root, "revisions")), NUnit.Framework.Has.Length.EqualTo(1), "nothing is left of the rejected import");
			Assert.That(Directory.Exists(Path.Combine(_store.Root, "staging")) ? Directory.GetDirectories(Path.Combine(_store.Root, "staging")) : new string[0], Is.Empty);
		}

		[TestCase("../directoutputconfig.ini")]
		[TestCase("/directoutputconfig.ini")]
		[TestCase("C:/directoutputconfig.ini")]
		[TestCase("a/./directoutputconfig.ini")]
		public void RejectsZipEntriesThatLeaveTheFolder(string entry)
		{
			Rejected(_importer.Import(Zip("hostile.zip", ("directoutputconfig51.ini", MainIni), (entry, MainIni))), "leaves the zip's folder");
			Assert.That(_store.Active, Is.Null);
		}

		[Test]
		public void BoundsSizesAndCounts()
		{
			_importer.Limits.MaxFileBytes = 1000;
			Rejected(_importer.Import(Zip("big.zip", ("directoutputconfig.ini", MainIni + new string('#', 2000)))), "more than the 1000");
			_importer.Limits.MaxFileBytes = 64L * 1024 * 1024;

			_importer.Limits.MaxEntries = 2;
			Rejected(_importer.Import(Zip("many.zip", ("directoutputconfig.ini", MainIni), ("a.xml", Mappings), ("b.xml", Mappings))), "entries");
			_importer.Limits.MaxEntries = 5000;

			_importer.Limits.MaxDepth = 2;
			Rejected(_importer.Import(Zip("deep.zip", ("directoutputconfig.ini", MainIni), ("a/b/c/tablemappings.xml", Mappings))), "nested deeper");
			_importer.Limits.MaxTotalBytes = 150;
			_importer.Limits.MaxDepth = 8;
			Rejected(_importer.Import(Zip("total.zip", ("directoutputconfig.ini", MainIni), ("directoutputconfig51.ini", MainIni))), "unpacks to more");
			Assert.That(_store.Active, Is.Null);
		}

		[Test]
		public void RejectsXmlWithADocumentType()
		{
			var entity = "<?xml version=\"1.0\"?>\r\n<!DOCTYPE Cabinet [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>\r\n<Cabinet><Name>&e;</Name></Cabinet>\r\n";
			Rejected(_importer.Import(Zip("dtd.zip", ("directoutputconfig.ini", MainIni), ("Cabinet.xml", entity))), "Cabinet.xml");
		}

		[Test]
		public void RejectsTwoDifferentFilesWithOneName()
		{
			Rejected(_importer.Import(Zip("two.zip", ("a/directoutputconfig.ini", MainIni), ("b/directoutputconfig.ini", SecondIni))), "two different files named");
			// The same file twice is fine.
			Accepted(_importer.Import(Zip("same.zip", ("a/directoutputconfig.ini", MainIni), ("b/directoutputconfig.ini", MainIni))));
		}

		[Test]
		public void RejectsAnImportWithoutIniFiles()
		{
			Rejected(_importer.Import(Zip("none.zip", ("tablemappings.xml", Mappings))), "no DOF ini file");
			Rejected(_importer.Import(Zip("notini.zip", ("directoutputconfig.ini", "just text"))), "[Config DOF]");
			Rejected(_importer.Import(Path.Combine(_dir, "missing.zip")), "does not exist");
		}

		[Test]
		public void ImportsLegacyLedControlFiles()
		{
			var legacy = "[Colors LedWiz]\r\nRed=48,0,0\r\n\r\n[Config outs]\r\nafm,S1\r\n";
			var report = Accepted(_importer.Import(Zip("legacy.zip", ("ledcontrol.ini", legacy), ("ledcontrol2.ini", legacy))));
			Assert.That(Has(report, DofReportSeverity.Warning, "no [version] section"), Is.True);
			Assert.That(Has(report, DofReportSeverity.Info, "libdof reads 2 table rows for 2 devices"), Is.True, report.ToString());
			Assert.That(DofCatalog.Load(_store.Active).Rows, Is.EqualTo(new[] { new DofRomRow(1, "afm"), new DofRomRow(2, "afm") }));

			// Next to current files, legacy ones are skipped as libdof skips them.
			report = Accepted(_importer.Import(Zip("mixed.zip", ("ledcontrol.ini", legacy), ("directoutputconfig30.ini", MainIni)), DofImportMode.Replace));
			Assert.That(Has(report, DofReportSeverity.Info, "libdof reads ledcontrol ini files only when"), Is.True);
			Assert.That(DofCatalog.Load(_store.Active).Devices.Select(d => d.Number), Is.EqualTo(new[] { 30 }));
		}

		[Test]
		public void ReportsControllersLibdofSkips()
		{
			// libdof skips a controller definition it can't read: the import says which and why, in libdof's
			// words. A type libdof does not support is reported once, as before.
			const string cabinet = "<?xml version=\"1.0\"?>\r\n<Cabinet><AutoConfigEnabled>false</AutoConfigEnabled><OutputControllers>"
				+ "<PacUIO Name=\"PacUIO 1\" />"
				+ "<LedWiz><Name>LedWiz 1</Name></LedWiz>"
				+ "<PacLed64 Name=\"PacLed64 1\" Id=\"one\" />"
				+ "<LedWiz><Name>LedWiz 2</Name><Number>17</Number></LedWiz>"
				+ "<PacUIO Id=\"0\" />"
				+ "<ArtNet><Name>ArtNet 1</Name><Universe>1</Universe></ArtNet>"
				+ "<Custom><Name>Custom 1</Name></Custom>"
				+ "</OutputControllers></Cabinet>\r\n";
			var report = Accepted(_importer.Import(Folder("skipped",
				("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("cabinet.xml", cabinet), ("directoutputconfig.ini", MainIni))));
			var skipped = report.Entries.Where(e => e.Message.Contains("libdof can't read")).Select(e => (e.Severity, e.Topic, e.Message)).ToList();
			Assert.That(skipped, Is.EqualTo(new[] {
				(DofReportSeverity.Warning, DofReportTopic.Unsupported, "cabinet.xml: PacUIO 1 is a PacUIO libdof can't read (no Id); this controller definition is skipped."),
				(DofReportSeverity.Warning, DofReportTopic.Unsupported, "cabinet.xml: LedWiz 1 is a LedWiz libdof can't read (no Number); this controller definition is skipped."),
				(DofReportSeverity.Warning, DofReportTopic.Unsupported, "cabinet.xml: PacLed64 1 is a PacLed64 libdof can't read (Id \"one\" is not a whole number); this controller definition is skipped."),
				(DofReportSeverity.Warning, DofReportTopic.Unsupported, "cabinet.xml: LedWiz 2 is a LedWiz libdof can't read (LedWiz Numbers must be between 1-16. The supplied number 17 is out of range); this controller definition is skipped."),
			}), report.ToString());
			Assert.That(report.Entries.Count(e => e.Message.Contains("Custom 1")), Is.EqualTo(1), report.ToString());
			Assert.That(Has(report, DofReportSeverity.Warning, "Custom 1 is a Custom, which libdof does not support; it is skipped."), Is.True, report.ToString());
		}

		[Test]
		public void ReportsIniFilesLibdofSkips()
		{
			// DOF skips an ini file without colours entirely; one is enough to fail the import, one of two is a warning.
			var noColors = "[version]\r\nversion=1\r\n\r\n[Colors DOF]\r\n# none\r\n\r\n[Config DOF]\r\nafm,S1\r\n";
			Rejected(_importer.Import(Zip("alone.zip", ("directoutputconfig.ini", noColors))), "no table rows");
			var report = Accepted(_importer.Import(Zip("pair.zip", ("directoutputconfig.ini", MainIni), ("directoutputconfig51.ini", noColors))));
			Assert.That(Has(report, DofReportSeverity.Warning, "directoutputconfig51.ini has no colours"), Is.True, report.ToString());
			Assert.That(Has(report, DofReportSeverity.Warning, "libdof reads no table rows from directoutputconfig51.ini"), Is.True);
		}

		[Test]
		public void CatalogMatchesTheRowsLibdofUses()
		{
			// libdof skips device 51 (no colours), so its exact row for afm_113b must not hide device 1's prefix row.
			var noColors = "[version]\r\nversion=1\r\n\r\n[Colors DOF]\r\n# none\r\n\r\n[Config DOF]\r\nafm_113b,S1\r\n";
			Accepted(_importer.Import(Zip("pair.zip", ("directoutputconfig.ini", MainIni), ("directoutputconfig51.ini", noColors))));
			var catalog = DofCatalog.Load(_store.Active);
			Assert.That(catalog.RowsFromLibdof, Is.True);
			Assert.That(catalog.Rows, Is.EqualTo(new[] { new DofRomRow(1, "afm"), new DofRomRow(1, "t2") }));
			Assert.That(catalog.Match("afm_113b"), Is.EqualTo(new[] { new DofRomRow(1, "afm") }));
			Assert.That(catalog.Shadowed("afm_113b"), Is.Empty);
			Assert.That(catalog.Devices.Select(d => d.Number), Is.EqualTo(new[] { 1, 51 }), "the files are still listed");
		}

		[Test]
		public void ReportsRomsAnotherDeviceShadows()
		{
			var report = Accepted(_importer.Import(Zip("shadow.zip", ("directoutputconfig.ini", MainIni), ("directoutputconfig51.ini", Header + "afm_113b,L5\r\n"))));
			var entry = report.Entries.Single(e => e.Topic == DofReportTopic.RomMatching);
			Assert.That(entry.Message, Does.Contain("afm_113b (device 1 row afm)"));
			Assert.That(DofCatalog.Load(_store.Active).Shadowed("afm_113b"), Is.EqualTo(new[] { new DofRomRow(1, "afm") }));

			// One device cannot shadow itself.
			report = Accepted(_importer.Import(Zip("one.zip", ("directoutputconfig.ini", Header + "afm,S1\r\nafm_113b,S2\r\n")), DofImportMode.Replace));
			Assert.That(report.Entries.Any(e => e.Topic == DofReportTopic.RomMatching), Is.False);
		}

		[Test]
		public void SkipsFilesLibdofCannotUse()
		{
			var report = Accepted(_importer.Import(Zip("odd.zip", ("directoutputconfig.ini", MainIni), ("directoutputconfigX.ini", MainIni),
				("__MACOSX/._directoutputconfig.ini", "junk"), (".hidden.xml", Mappings), ("Tablé.xml", TableXml), ("other.xml", "<Something />"))));
			Assert.That(Has(report, DofReportSeverity.Warning, "Skipped directoutputconfigX.ini"), Is.True, report.ToString());
			Assert.That(Has(report, DofReportSeverity.Warning, "non-ASCII"), Is.True);
			Assert.That(Has(report, DofReportSeverity.Info, "root element <Something>"), Is.True);
			Assert.That(Directory.GetFiles(_store.Active.ConfigDir).Select(Path.GetFileName), Is.EquivalentTo(new[] { "directoutputconfig.ini", DofGlobalConfig.FileName }));
		}

		[Test]
		public void FolderImportDoesNotFollowLinks()
		{
			var outside = Folder("outside", ("directoutputconfig51.ini", SecondIni));
			var folder = Folder("config", ("directoutputconfig.ini", MainIni));
			try {
				Directory.CreateSymbolicLink(Path.Combine(folder, "linked"), outside);
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
				Assert.Ignore("Symbolic links cannot be created here: " + e.Message);
			}
			var report = Accepted(_importer.Import(folder));
			Assert.That(Has(report, DofReportSeverity.Info, "links are not followed"), Is.True);
			Assert.That(DofCatalog.Load(_store.Active).Devices.Select(d => d.Number), Is.EqualTo(new[] { 1 }));
		}

		[Test]
		public void RollbackAndLeasesKeepRevisionsAlive()
		{
			Accepted(_importer.Import(Zip("1.zip", ("directoutputconfig.ini", MainIni))));
			var first = _store.Active;
			using (var lease = _store.Acquire()) {
				Assert.That(lease.Revision.Id, Is.EqualTo(first.Id));
				Accepted(_importer.Import(Zip("2.zip", ("directoutputconfig.ini", SecondIni))));
				Accepted(_importer.Import(Zip("3.zip", ("directoutputconfig.ini", MainIni))));
				Assert.That(Directory.Exists(first.BasePath), Is.True, "a leased revision is kept");
			}
			Assert.That(Directory.Exists(first.BasePath), Is.False, "released, neither active nor previous: pruned");

			var third = _store.Active.Id;
			var second = _store.Previous.Id;
			Assert.That(_store.Rollback(), Is.True);
			Assert.That((_store.Active.Id, _store.Previous.Id), Is.EqualTo((second, third)));
		}

		[Test]
		public void TableConfigsAreKeptAsAnInventory()
		{
			Accepted(_importer.Import(Folder("tables", ("directoutputconfig.ini", MainIni), ("table.xml", TableXml))));
			Assert.That(_store.Active.TableConfigs, Is.EqualTo(new[] { "table" }));
		}

		[Test]
		public void ACabinetTheGlobalConfigNamesMustBeImported()
		{
			// Neither another cabinet that happens to be there nor auto-configuration stands in for it.
			var other = Cabinet.Replace("<Name>Test</Name>", "<Name>Other</Name>");
			Rejected(_importer.Import(Folder("other", ("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("Other.xml", other), ("directoutputconfig.ini", MainIni))), "cabinet.xml");
			Rejected(_importer.Import(Folder("none", ("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("directoutputconfig.ini", MainIni))), "cabinet.xml");
			Assert.That(_store.Active, Is.Null);

			// A cabinet kept from the previous import is fine (an import that names none configures itself, see above).
			Accepted(_importer.Import(Folder("first", ("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("cabinet.xml", Cabinet), ("directoutputconfig.ini", MainIni))));
			Accepted(_importer.Import(Folder("update", ("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("directoutputconfig.ini", MainIni))));
			Assert.That(DofCatalog.Load(_store.Active).Controllers, Is.Not.Empty);

			// But not when the new global config names another cabinet: the kept one is not it.
			var active = _store.Active.Id;
			var otherGlobal = WindowsGlobalConfig.Replace("cabinet.xml", "Other.xml");
			Rejected(_importer.Import(Folder("switch", ("GlobalConfig_B2SServer.xml", otherGlobal), ("directoutputconfig.ini", MainIni))), "Other.xml");
			Assert.That(_store.Active.Id, Is.EqualTo(active));

			// A wildcard is resolved as libdof resolves it, against the files the import ends with.
			var tournament = WindowsGlobalConfig.Replace("cabinet.xml", "Tournament-*.xml");
			Rejected(_importer.Import(Folder("wildcard", ("GlobalConfig_B2SServer.xml", tournament), ("directoutputconfig.ini", MainIni))), "Tournament-*.xml");
			Assert.That(_store.Active.Id, Is.EqualTo(active));
		}

		[Test]
		public void ACabinetPatternWithWildcardsPicksTheFileItMatches()
		{
			var pattern = WindowsGlobalConfig.Replace("cabinet.xml", "Cab*.xml");
			var other = Cabinet.Replace("<Name>Test</Name>", "<Name>Other</Name>");
			Accepted(_importer.Import(Folder("one", ("GlobalConfig_B2SServer.xml", pattern), ("Cabinet-Home.xml", Cabinet), ("Other.xml", other), ("directoutputconfig.ini", MainIni))));
			Assert.That(Path.GetFileName(XDocument.Load(_store.Active.GlobalConfigFile).Root.Element("CabinetConfigFilePattern").Value), Is.EqualTo("Cabinet-Home.xml"));
			// Two matches: libdof would take whichever its folder listing has first.
			Rejected(_importer.Import(Folder("two", ("GlobalConfig_B2SServer.xml", pattern), ("Cabinet-Home.xml", Cabinet), ("Cabinet-Club.xml", other), ("directoutputconfig.ini", MainIni))), "several cabinet files");
		}

		[Test]
		public void TheKeptGlobalConfigPicksTheCabinetOfAMerge()
		{
			var home = WindowsGlobalConfig.Replace("cabinet.xml", "Home-*.xml");
			var updated = Cabinet.Replace("<Name>Test</Name>", "<Name>Updated</Name>");
			var other = Cabinet.Replace("<Name>Test</Name>", "<Name>Other</Name>");
			Accepted(_importer.Import(Folder("first", ("GlobalConfig_B2SServer.xml", home), ("Home-A.xml", Cabinet), ("directoutputconfig.ini", MainIni))));
			// The update has no global config: the kept one's pattern picks the new Home-A.xml, not the Cabinet.xml next to it.
			var report = Accepted(_importer.Import(Folder("update", ("Home-A.xml", updated), ("Cabinet.xml", other))));
			Assert.That(Has(report, DofReportSeverity.Info, "Skipped Cabinet.xml"), Is.True, report.ToString());
			Assert.That(Path.GetFileName(XDocument.Load(_store.Active.GlobalConfigFile).Root.Element("CabinetConfigFilePattern").Value), Is.EqualTo("Home-A.xml"));
			Assert.That(File.ReadAllText(Path.Combine(_store.Active.ConfigDir, "Home-A.xml")), Does.Contain("<Name>Updated</Name>"));
			Assert.That(File.Exists(Path.Combine(_store.Active.ConfigDir, "Cabinet.xml")), Is.False);
		}

		[Test]
		public void ACabinetNamedLikeTheGlobalConfigIsRejected()
		{
			// The player writes its own global config under that name, which would replace the cabinet.
			Rejected(_importer.Import(Folder("clash", ("GlobalConfig_B2SServer.xml", Cabinet), ("directoutputconfig.ini", MainIni))), "the name of the global config");
			Assert.That(_store.Active, Is.Null);
		}

		[Test]
		public void SerialPortsOfACabinetWithAnUpperCaseExtension()
		{
			// libdof opens the ports of Cabinet.XML as well; a lease list that missed them would let DOF open unleased ports.
			Accepted(_importer.Import(Folder("upper", ("GlobalConfig_B2SServer.xml", WindowsGlobalConfig), ("Cabinet.XML", Cabinet), ("directoutputconfig.ini", MainIni))));
			Assert.That(Directory.GetFiles(_store.Active.ConfigDir).Select(Path.GetFileName), Does.Contain("Cabinet.XML"));
			Assert.That(_store.Active.ReadSerialPorts(), Is.EqualTo(new[] { "COM3", "COM5" }));
		}

		[Test]
		public void RevisionNamesAreNeverReused()
		{
			// A prune that listed a deleted revision must not find a new one under its name, even within the same second.
			var first = _store.CreateRevision();
			_store.Discard(first);
			var second = _store.CreateRevision();
			var third = _store.CreateRevision();
			Assert.That(new[] { first.Id, second.Id, third.Id }.Distinct().Count(), Is.EqualTo(3));
			_store.Discard(second);
			_store.Discard(third);
		}

		[Test]
		public void AnImportInProgressIsNotPruned()
		{
			Accepted(_importer.Import(Zip("1.zip", ("directoutputconfig.ini", MainIni))));
			var lease = _store.Acquire();
			var revision = _store.CreateRevision();
			// A game ends while the import writes and checks its revision.
			lease.Dispose();
			_store.Prune();
			Assert.That(Directory.Exists(revision.BasePath), Is.True);
			_store.Discard(revision);
			Assert.That(Directory.Exists(revision.BasePath), Is.False);
		}

		[Test]
		public void AFailedActivationKeepsTheActiveRevision()
		{
			Accepted(_importer.Import(Zip("1.zip", ("directoutputconfig.ini", MainIni))));
			var first = _store.Active.Id;
			// The pointer file cannot be written.
			Directory.CreateDirectory(Path.Combine(_store.Root, "revisions.txt.tmp"));
			Directory.CreateDirectory(Path.Combine(_store.Root, "previous.tmp"));
			var report = _importer.Import(Zip("2.zip", ("directoutputconfig.ini", SecondIni)));
			Assert.That(report.Accepted, Is.False, report.ToString());
			Assert.That(report.RejectionReason, Does.Contain("failed"));
			Assert.That(_store.Active?.Id, Is.EqualTo(first), "the active revision is untouched and still there");
			Assert.That(Directory.GetDirectories(Path.Combine(_store.Root, "revisions")).Select(Path.GetFileName), Is.EqualTo(new[] { first }));
		}

		[Test]
		public void ReplacingTheShapesBitmapAlone()
		{
			Accepted(_importer.Import(Zip("shapes.zip", ("directoutputconfig.ini", MainIni), ("DirectOutputShapes.xml", Shapes), ("DirectOutputShapes.png", "old"))));
			var png = Path.Combine(_dir, "DirectOutputShapes.png");
			File.WriteAllText(png, "new");
			var report = Accepted(_importer.Import(png));
			Assert.That(File.ReadAllText(Path.Combine(_store.Active.ConfigDir, "DirectOutputShapes.png")), Is.EqualTo("new"));
			Assert.That(Has(report, DofReportSeverity.Warning, "is missing"), Is.False, report.ToString());
		}

		[Test]
		public void ReplacingTheShapesXmlAloneKeepsItsBitmap()
		{
			Accepted(_importer.Import(Zip("shapes.zip", ("directoutputconfig.ini", MainIni), ("DirectOutputShapes.xml", Shapes), ("DirectOutputShapes.png", "old"))));
			var xml = Path.Combine(_dir, "DirectOutputShapes.xml");
			File.WriteAllText(xml, Shapes.Replace("<Shapes />", "<Shapes></Shapes>"));
			var report = Accepted(_importer.Import(xml));
			Assert.That(File.ReadAllText(Path.Combine(_store.Active.ConfigDir, "DirectOutputShapes.png")), Is.EqualTo("old"));
			Assert.That(Has(report, DofReportSeverity.Warning, "is missing"), Is.False, report.ToString());
		}

		[Test]
		public void ImportsTableMappingsWithAnyExtension()
		{
			var report = Accepted(_importer.Import(Zip("mappings.zip", ("directoutputconfig.ini", MainIni), ("tablemappings.txt", Mappings), ("tablemappings.bak", "not xml"))));
			Assert.That(Has(report, DofReportSeverity.Info, "imported as tablemappings.xml"), Is.True, report.ToString());
			Assert.That(DofCatalog.Load(_store.Active).TableMappings, Is.EqualTo(new[] { new KeyValuePair<string, string>("Attack From Mars", "afm") }));
			Assert.That(Directory.GetFiles(_store.Active.ConfigDir, "tablemappings.*").Select(Path.GetFileName), Is.EqualTo(new[] { "tablemappings.xml" }));
		}

		[Test]
		public void StagingStaysInsideItsFolder()
		{
			var root = Path.Combine(_dir, "staging");
			Assert.That(DofImporter.IsInside(root, Path.Combine(root, "a", "b.ini")), Is.True);
			Assert.That(DofImporter.IsInside(root, Path.Combine(root, "..", "b.ini")), Is.False);
			Assert.That(DofImporter.IsInside(root, root + "-other" + Path.DirectorySeparatorChar + "b.ini"), Is.False);
			Assert.That(DofImporter.IsInside(root, root), Is.False);
		}

		[Test, Platform(Exclude = "Win")]
		public void FolderImportKeepsBackslashesInUnixNames()
		{
			var folder = Folder("unix", ("directoutputconfig.ini", MainIni));
			// One file name, not a path: copied by its separators, "..\..\" would leave the staging folder.
			File.WriteAllText(Path.Combine(folder, "..\\..\\..\\directoutputconfig51.ini"), SecondIni);
			var report = Accepted(_importer.Import(folder));
			Assert.That(Has(report, DofReportSeverity.Info, "backslash"), Is.True, report.ToString());
			Assert.That(DofCatalog.Load(_store.Active).Devices.Select(d => d.Number), Is.EqualTo(new[] { 1 }));
			Assert.That(File.Exists(Path.Combine(_store.Root, "directoutputconfig51.ini")), Is.False);
		}

		[Test]
		public void GlobalConfigUsesPathsLibdofCanOpen()
		{
			var store = new DofConfigStore(Path.Combine(_dir, "Jürgen", "store"));
			Directory.CreateDirectory(store.Root);
			if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) {
				try {
					DofPaths.ToNative(store.Root);
				} catch (DofException) {
					Assert.Ignore("This volume has no short (8.3) names.");
				}
			} else {
				Assert.Ignore("Only Windows opens files through a code page.");
			}
			Accepted(new DofImporter(store).Import(Folder("cab", ("cabinet.xml", Cabinet), ("directoutputconfig.ini", MainIni))));
			var global = XDocument.Load(store.Active.GlobalConfigFile).Root;
			foreach (var element in new[] { "IniFilesPath", "CabinetConfigFilePattern" }) {
				Assert.That(DofPaths.IsAscii(global.Element(element).Value), Is.True, global.Element(element).Value);
			}
		}
	}
}
