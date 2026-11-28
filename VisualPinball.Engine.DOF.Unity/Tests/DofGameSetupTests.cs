// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using VisualPinball.Engine.DOF.Configuration;
using VisualPinball.Engine.DOF.Player;
using VisualPinball.Plugins;

namespace VisualPinball.Engine.DOF.Unity.Tests
{
	/// <summary>What a game runs DOF with, and what it reserves.</summary>
	public class DofGameSetupTests
	{
		private static readonly GameIdentity Rom = new GameIdentity("pinmame", "abc_l8");
		private static readonly GameIdentity Original = new GameIdentity("vpe", "my-original");

		private const string Mappings = "<?xml version=\"1.0\"?><TableNameMappings><Mapping><TableName>My Mapped Table</TableName><RomName>mapped</RomName></Mapping></TableNameMappings>";

		[Test]
		public void PrefersTheSettingThenTheRunningRom()
		{
			Assert.That(DofGameSetup.ResolveRom(" abc_p2 ", Rom, "abc_l6", "ignored", null).Rom, Is.EqualTo("abc_p2"));
			Assert.That(DofGameSetup.ResolveRom("auto", Rom, "abc_l6", null, null).Rom, Is.EqualTo("abc_l6"), "PinMAME may play a compatible ROM");
			Assert.That(DofGameSetup.ResolveRom("auto", Rom, null, null, null).Rom, Is.EqualTo("abc_l8"));
			Assert.That(DofGameSetup.ResolveRom("", Rom, null, null, null), Is.EqualTo(("abc_l8", (string)null)));
		}

		[Test]
		public void TakesTheQualifiedRomThenTheRomThenTheDeclaredName()
		{
			using (var host = new DofTestHost()) {
				host.Import(340, "vpe_my-original,S1\r\nabc_l8,S1\r\ndeclared,S1\r\npinmame_xyz_109,S1\r\nxyz_109,S2");
				var revision = host.Factory.Store.Active;
				File.WriteAllText(Path.Combine(revision.ConfigDir, "tablemappings.xml"), Mappings);
				var catalog = DofCatalog.Load(revision);

				Assert.That(DofGameSetup.ResolveRom("auto", Original, null, "declared", catalog), Is.EqualTo(("vpe_my-original", (string)null)),
					"a row for the provider's own name of the game");
				Assert.That(DofGameSetup.ResolveRom("auto", new GameIdentity("pinmame", "xyz_109"), null, null, catalog).Rom, Is.EqualTo("pinmame_xyz_109"));
				Assert.That(DofGameSetup.ResolveRom("auto", Rom, null, "declared", catalog), Is.EqualTo(("abc_l8", (string)null)));
				Assert.That(DofGameSetup.ResolveRom("auto", new GameIdentity("vpe", "other"), null, " declared ", catalog), Is.EqualTo(("declared", (string)null)),
					"the table's DOF name when the ROM has no rows");

				var (rom, problem) = DofGameSetup.ResolveRom("auto", new GameIdentity("vpe", "other"), null, "nothing", catalog);
				Assert.That(rom, Is.EqualTo("other"));
				Assert.That(problem, Does.Contain("no rows for other"));
				Assert.That(DofGameSetup.ResolveRom("missing", Rom, null, null, catalog).Problem, Does.Contain("no rows for missing"),
					"a chosen name without rows is reported too");
				Assert.That(problem, Is.EqualTo("The configuration has no rows for other, so nothing in the cabinet responds to this game."));
				Assert.That(DofGameSetup.ResolveRom("auto", default(GameIdentity), null, null, catalog),
					Is.EqualTo(((string)null, "The game has no ROM and the table declares no DOF game name; choose one in the game's DOF settings.")));

				// A table mapping goes by the title, which another table can share: it is only offered.
				Assert.That(DofGameSetup.ResolveRom("auto", new GameIdentity("vpe", "other"), null, null, catalog).Rom, Is.EqualTo("other"));
				Assert.That(DofGameSetup.TableMapping(catalog, new[] { "file", "my mapped table" }), Is.EqualTo(new KeyValuePair<string, string>("My Mapped Table", "mapped")));
				Assert.That(DofGameSetup.TableMapping(catalog, new[] { "other" }), Is.Null);
			}
		}

		[Test]
		public void OffersTheTableMappingAsAChoice()
		{
			using (var host = new DofTestHost()) {
				host.Import(342, "mapped,S1\r\nother,S2");
				File.WriteAllText(Path.Combine(host.Factory.Store.Active.ConfigDir, "tablemappings.xml"), Mappings);
				var request = new PluginChoiceRequest(new GameIdentity("vpe", "nothing"), new PluginSettingsSnapshot(1), "windows") {
					Package = new PluginPackageReference(@"C:\Tables\My Mapped Table.vpe", "My Mapped Table"),
				};
				var catalog = host.Factory.DiscoverAsync("roms", request, CancellationToken.None).Result;
				Assert.That(catalog.Choices.Select(c => c.Id), Is.EqualTo(new[] { "auto", "mapped", "other" }));
				Assert.That(catalog.Choices[0].Description, Does.Contain("no rows for nothing"));
				Assert.That(catalog.Choices[1].Label, Does.Contain("table mapping"));
			}
		}

		[Test]
		public void NamesTheTableByItsFileThenItsTitle()
		{
			Assert.That(DofGameSetup.TableNames(@"C:\Tables\my table.vpe", "MT").ToArray(), Is.EqualTo(new[] { "my table", "MT" }));
		}

		[Test]
		public void ReservesWithTheDmdPluginsKeys()
		{
			Assert.That(DofGameSetup.LeaseKeys(new[] { @"\\.\COM03", "com3", "/dev/ttyACM0" }, false),
				Is.EqualTo(new[] { "serial:COM3", "serial:/dev/ttyACM0" }));
			Assert.That(DofGameSetup.LeaseKeys(new string[0], true), Is.EqualTo(new[] { "usb:pindmd1:auto" }),
				"FT245R boards share the FTDI chip with PinDMD 1");
			Assert.That(DofGameSetup.CanonicalPort(" COM10 "), Is.EqualTo("COM10"));
		}

		[Test]
		public void ReservesTheFtdiBoardWhenDofConfiguresDevicesItself()
		{
			using (var host = new DofTestHost()) {
				host.Import(341, "vpetest,S1");
				var revision = host.Factory.Store.Active;
				Assert.That(DofGameSetup.AutoConfigEnabled(revision), Is.False, "the cabinet turns it off");
				Assert.That(DofGameSetup.LeaseKeys(revision, DofCatalog.Load(revision)), Is.Empty);

				host.Import(341, "vpetest,S1", "COM5", autoConfig: true);
				revision = host.Factory.Store.Active;
				Assert.That(DofGameSetup.AutoConfigEnabled(revision), Is.True);
				Assert.That(DofGameSetup.LeaseKeys(revision, DofCatalog.Load(revision)), Is.EqualTo(new[] { "serial:COM5", "usb:pindmd1:auto" }),
					"DOF's auto-configuration adds connected FT245R boards");
			}
		}

		[Test]
		public void ReadsAutoConfigEnabledAsLibdofDoes()
		{
			using (var host = new DofTestHost()) {
				host.Import(343, "vpetest,S1");
				var revision = host.Factory.Store.Active;
				var cabinet = Directory.GetFiles(revision.ConfigDir, "*.xml").Single(f => Path.GetFileName(f).Equals("Cabinet.xml", System.StringComparison.OrdinalIgnoreCase));
				var xml = File.ReadAllText(cabinet);
				var start = xml.IndexOf("<AutoConfigEnabled>", System.StringComparison.Ordinal);
				var end = xml.IndexOf("</AutoConfigEnabled>", System.StringComparison.Ordinal) + "</AutoConfigEnabled>".Length;
				Assert.That(start, Is.GreaterThan(0));

				// libdof's Cabinet::FromXml: on unless the element's text, untrimmed, is "true" in any case. The text is
				// tinyxml2's GetText, which skips comments before it.
				foreach (var (element, enabled) in new[] {
					("<AutoConfigEnabled>true</AutoConfigEnabled>", true),
					("<AutoConfigEnabled>TRUE</AutoConfigEnabled>", true),
					("<AutoConfigEnabled>false</AutoConfigEnabled>", false),
					("<AutoConfigEnabled>1</AutoConfigEnabled>", false),
					("<AutoConfigEnabled>0</AutoConfigEnabled>", false),
					("<AutoConfigEnabled> true </AutoConfigEnabled>", false),
					("<AutoConfigEnabled>yes</AutoConfigEnabled>", false),
					("<AutoConfigEnabled></AutoConfigEnabled>", true),
					("<AutoConfigEnabled>  </AutoConfigEnabled>", true),
					("<AutoConfigEnabled><!-- off -->false</AutoConfigEnabled>", false),
					("<AutoConfigEnabled><!-- on -->true</AutoConfigEnabled>", true),
					("<AutoConfigEnabled><!-- off --></AutoConfigEnabled>", true),
					("", true),
				}) {
					File.WriteAllText(cabinet, xml.Substring(0, start) + element + xml.Substring(end));
					Assert.That(DofGameSetup.AutoConfigEnabled(revision), Is.EqualTo(enabled), element);
					Assert.That(DofGameSetup.LeaseKeys(revision, DofCatalog.Load(revision)).Contains(DofGameSetup.FtdiFamily), Is.EqualTo(enabled), element);
				}
				File.WriteAllText(cabinet, "<NotACabinet><AutoConfigEnabled>false</AutoConfigEnabled></NotACabinet>");
				Assert.That(DofGameSetup.AutoConfigEnabled(revision), Is.True, "libdof loads no cabinet and configures devices itself");
				File.WriteAllText(cabinet, "<Cabinet><AutoConfigEnabled>false</AutoConfigEnabled>");
				Assert.That(DofGameSetup.AutoConfigEnabled(revision), Is.True, "nor from a file it cannot parse");
			}
		}

		[Test]
		public void ReservesWhatLibdofOpensFromACabinetWithADefaultNamespace()
		{
			using (var host = new DofTestHost()) {
				host.Import(343, "vpetest,S1");
				var revision = host.Factory.Store.Active;
				var cabinet = Directory.GetFiles(revision.ConfigDir, "*.xml").Single(f => Path.GetFileName(f).Equals("Cabinet.xml", System.StringComparison.OrdinalIgnoreCase));
				const string Controllers = "<AutoConfigEnabled>false</AutoConfigEnabled><OutputControllers>"
					+ "<FT245RBitbangController><Name>FT245R 1</Name></FT245RBitbangController>"
					+ "<TeensyStripController><Name>Teensy 1</Name><ComPortName>COM7</ComPortName></TeensyStripController></OutputControllers>";

				// tinyxml2 matches the names as written: a default namespace changes nothing, so libdof opens both.
				File.WriteAllText(cabinet, "<?xml version=\"1.0\"?><Cabinet xmlns=\"http://example.org/dof\">" + Controllers + "</Cabinet>");
				Assert.That(DofGameSetup.AutoConfigEnabled(revision), Is.False);
				Assert.That(DofGameSetup.LeaseKeys(revision, DofCatalog.Load(revision)), Is.EqualTo(new[] { "serial:COM7", DofGameSetup.FtdiFamily }));
				Assert.That(DofCatalog.Load(revision).HasCabinet, Is.True);

				// A prefix does: libdof finds no Cabinet, so it configures the connected devices itself.
				File.WriteAllText(cabinet, "<?xml version=\"1.0\"?><d:Cabinet xmlns:d=\"http://example.org/dof\">" + Controllers.Replace("<", "<d:").Replace("<d:/", "</d:") + "</d:Cabinet>");
				Assert.That(DofGameSetup.AutoConfigEnabled(revision), Is.True);
				Assert.That(DofGameSetup.LeaseKeys(revision, DofCatalog.Load(revision)), Is.EqualTo(new[] { DofGameSetup.FtdiFamily }));
				Assert.That(DofCatalog.Load(revision).HasCabinet, Is.False, "the settings page says so too");
			}
		}

		[Test]
		public void ValidatesSerialOverrides()
		{
			using (var host = new DofTestHost()) {
				var settings = new PluginSettingsSnapshot(1, new JObject {
					["SerialOverrides"] = new JArray(
						new JObject { ["Controller"] = "Teensy", ["Port"] = "COM5" },
						new JObject { ["Controller"] = "Teensy", ["Port"] = "COM6" },
						new JObject { ["Controller"] = "", ["Port"] = "COM7" },
						new JObject { ["Controller"] = "", ["Port"] = "" }),
				});
				var issues = host.Factory.Validate(new PluginChoiceRequest(null, settings, "windows"));
				Assert.That(issues.Select(i => i.Message), Is.EquivalentTo(new[] { "Teensy has more than one port.", "Each port needs a controller name and a port." }));
				Assert.That(DofPluginFactory.SerialOverrides(settings)["Teensy"], Is.EqualTo("COM6"));
			}
		}
	}
}
