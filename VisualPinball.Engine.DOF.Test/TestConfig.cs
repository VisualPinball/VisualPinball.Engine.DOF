// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VisualPinball.Engine.DOF.Test
{
	/// <summary>An Art-Net controller of a test cabinet: its name, universe and destination.</summary>
	public sealed class ArtNetUnit
	{
		public string Name;
		public int Universe;
		public string Address = "127.0.0.1";
	}

	/// <summary>
	/// A DOF configuration folder in a temporary directory: <c>directoutputconfig/</c> with a global config, an
	/// explicit cabinet (never auto-configuration, so no test touches real hardware) and ini files.
	/// </summary>
	public sealed class TestConfig : IDisposable
	{
		public string Root { get; }
		public string ConfigDir { get; }
		/// <summary>The table file passed to init: inside the configuration, and not a file.</summary>
		public string TableFile => Path.Combine(Root, "tables", "test.vpx");

		/// <param name="folderName">The configuration's folder name under the temporary directory; unique if null.</param>
		public TestConfig(string folderName = null)
		{
			Root = Path.Combine(Path.GetTempPath(), "vpe-dof-test", folderName ?? Guid.NewGuid().ToString("N"));
			ConfigDir = Path.Combine(Root, "directoutputconfig");
			Directory.CreateDirectory(ConfigDir);
		}

		public TestConfig GlobalConfig(string tableConfigPattern = null, string logPattern = null, bool clearLog = false, string iniFilesPath = "{GlobalConfigDir}")
		{
			var xml = new StringBuilder();
			xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
			xml.AppendLine("<GlobalConfig>");
			xml.AppendLine($"  <IniFilesPath>{iniFilesPath}</IniFilesPath>");
			xml.AppendLine("  <CabinetConfigFilePattern>{GlobalConfigDir}/Cabinet.xml</CabinetConfigFilePattern>");
			if (tableConfigPattern == null) {
				xml.AppendLine("  <TableConfigFilePatterns />");
			} else {
				xml.AppendLine($"  <TableConfigFilePatterns><FilePattern>{tableConfigPattern}</FilePattern></TableConfigFilePatterns>");
			}
			if (logPattern != null) {
				xml.AppendLine("  <EnableLogging>true</EnableLogging>");
				xml.AppendLine($"  <ClearLogOnSessionStart>{(clearLog ? "true" : "false")}</ClearLogOnSessionStart>");
				xml.AppendLine($"  <LogFilePattern>{logPattern}</LogFilePattern>");
			}
			xml.AppendLine("</GlobalConfig>");
			return Write("GlobalConfig_B2SServer.xml", xml.ToString());
		}

		/// <summary>
		/// A cabinet with the given Art-Net controllers and LedWiz-equivalent device 1, whose ports map to the
		/// controllers' channels in order (ports 1..perUnit to the first controller, and so on).
		/// </summary>
		public TestConfig Cabinet(int perUnit, params ArtNetUnit[] units) => Cabinet(perUnit, null, units);

		public TestConfig Cabinet(int perUnit, IDictionary<int, string> extraPorts, params ArtNetUnit[] units)
		{
			var xml = new StringBuilder();
			xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
			xml.AppendLine("<Cabinet>");
			xml.AppendLine("  <Name>Test cabinet</Name>");
			xml.AppendLine("  <AutoConfigEnabled>false</AutoConfigEnabled>");
			xml.AppendLine("  <OutputControllers>");
			foreach (var unit in units) {
				xml.AppendLine($"    <ArtNet><Name>{unit.Name}</Name><Universe>{unit.Universe}</Universe><BroadcastAddress>{unit.Address}</BroadcastAddress></ArtNet>");
			}
			xml.AppendLine("  </OutputControllers>");
			xml.AppendLine("  <Toys>");
			xml.AppendLine("    <LedWizEquivalent>");
			xml.AppendLine("      <Name>LedWizEquivalent 1</Name>");
			xml.AppendLine("      <LedWizNumber>1</LedWizNumber>");
			xml.AppendLine("      <Outputs>");
			var port = 1;
			foreach (var unit in units) {
				for (var channel = 1; channel <= perUnit; channel++, port++) {
					xml.AppendLine($"        <LedWizEquivalentOutput><OutputName>{unit.Name}.{channel:00}</OutputName><LedWizEquivalentOutputNumber>{port}</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>");
				}
			}
			if (extraPorts != null) {
				foreach (var extra in extraPorts) {
					xml.AppendLine($"        <LedWizEquivalentOutput><OutputName>{extra.Value}</OutputName><LedWizEquivalentOutputNumber>{extra.Key}</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>");
				}
			}
			xml.AppendLine("      </Outputs>");
			xml.AppendLine("    </LedWizEquivalent>");
			xml.AppendLine("  </Toys>");
			xml.AppendLine("</Cabinet>");
			return Write("Cabinet.xml", xml.ToString());
		}

		/// <summary>An ini file (directoutputconfig.ini for device 1, directoutputconfigN.ini otherwise) with table rows.</summary>
		public TestConfig Ini(int device, params string[] rows) => Write(device == 1 ? "directoutputconfig.ini" : $"directoutputconfig{device}.ini", IniText(rows));

		/// <summary>The content of an ini file with the given table rows.</summary>
		public static string IniText(params string[] rows)
		{
			var ini = new StringBuilder();
			ini.Append("[version]\r\nversion=1\r\n\r\n[Colors DOF]\r\nWhite=#FFFFFFFF\r\n\r\n[Variables DOF]\r\n\r\n[Config DOF]\r\n");
			foreach (var row in rows) {
				ini.Append(row).Append("\r\n");
			}
			return ini.ToString();
		}

		public TestConfig Write(string name, string content)
		{
			File.WriteAllText(Path.Combine(ConfigDir, name), content, new UTF8Encoding(false));
			return this;
		}

		public void Dispose()
		{
			try {
				Directory.Delete(Root, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}
}
