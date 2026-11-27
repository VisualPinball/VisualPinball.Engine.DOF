// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VisualPinball.Engine.DOF.Configuration;
using VisualPinball.Engine.DOF.Session;

namespace VisualPinball.Engine.DOF.Test
{
	/// <summary>
	/// An imported configuration run by a session on the real native library, against an Art-Net cabinet on localhost.
	/// </summary>
	[TestFixture, NonParallelizable]
	public class DofSessionIntegrationTests
	{
		private const int Game = 3;
		private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

		private ArtNetCapture _capture;
		private string _dir;
		// Every session a test starts, so one that a failed assertion left running is stopped before the next test,
		// which would otherwise find DOF's single instance busy.
		private readonly List<DofSession> _sessions = new List<DofSession>();

		[OneTimeSetUp]
		public void SetUpCapture()
		{
			DofLibrary.EnsureCompatible();
			_capture = new ArtNetCapture();
		}

		[OneTimeTearDown]
		public void TearDownCapture() => _capture?.Dispose();

		[SetUp]
		public void SetUp()
		{
			_dir = Path.Combine(Path.GetTempPath(), "vpe-dof-session", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_dir);
		}

		[TearDown]
		public void TearDown()
		{
			foreach (var session in _sessions) {
				if (!session.StopAsync().Wait(Timeout)) {
					TestContext.Out.WriteLine("A session did not stop within " + Timeout);
				}
			}
			_sessions.Clear();
			try {
				Directory.Delete(_dir, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}

		private static T Wait<T>(Task<T> task)
		{
			Assert.That(task.Wait(Timeout), Is.True, "timed out");
			return task.Result;
		}

		private void Expect(int universe, Func<byte[], bool> match, string what, int after = 0) => ExpectIndex(universe, match, what, after);

		/// <summary>Like <see cref="Expect"/>, and returns the matching frame's number.</summary>
		private int ExpectIndex(int universe, Func<byte[], bool> match, string what, int after = 0)
		{
			var index = _capture.WaitForIndex(universe, match, after);
			if (index < 0) {
				var latest = _capture.Latest(universe);
				Assert.Fail($"{what}: no matching frame on universe {universe}; last: {(latest == null ? "none" : string.Join(",", latest.Take(8)))}");
			}
			return index;
		}

		/// <summary>Imports an Art-Net cabinet on one universe and a row for the ROM, the way the player does.</summary>
		private DofRevisionLease Import(int universe, string row) => Import(_dir, universe, row);

		private static DofRevisionLease Import(string dir, int universe, string row, string otherControllers = "")
		{
			var source = Path.Combine(dir, "source");
			Directory.CreateDirectory(source);
			File.WriteAllText(Path.Combine(source, "Cabinet.xml"), "<?xml version=\"1.0\"?>\r\n<Cabinet><Name>Test</Name><AutoConfigEnabled>false</AutoConfigEnabled>"
				+ $"<OutputControllers><ArtNet><Name>ArtNet 1</Name><Universe>{universe}</Universe><BroadcastAddress>127.0.0.1</BroadcastAddress></ArtNet>{otherControllers}</OutputControllers>"
				+ "<Toys><LedWizEquivalent><Name>LedWizEquivalent 1</Name><LedWizNumber>1</LedWizNumber><Outputs>"
				+ string.Concat(Enumerable.Range(1, 8).Select(p => $"<LedWizEquivalentOutput><OutputName>ArtNet 1.{p:00}</OutputName><LedWizEquivalentOutputNumber>{p}</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>"))
				+ "</Outputs></LedWizEquivalent></Toys></Cabinet>\r\n");
			File.WriteAllText(Path.Combine(source, "directoutputconfig.ini"), TestConfig.IniText(row));
			var store = new DofConfigStore(Path.Combine(dir, "store"));
			var report = new DofImporter(store).Import(source);
			Assert.That(report.Accepted, Is.True, report.ToString());
			return store.Acquire();
		}

		private DofSession Session(DofRevision revision, bool waitForSnapshot = true)
		{
			var session = new DofSession(new DofSessionOptions {
				BasePath = revision.BasePath,
				RomName = "vpetest_l1",
				GameSession = Game,
				WaitForSnapshot = waitForSnapshot,
			});
			_sessions.Add(session);
			return session;
		}

		[Test]
		public void NamedEventsDriveNamedElementsOnly()
		{
			const int universe = 302;
			// Port 1 is solenoid 1, port 2 the element named knocker, port 3 the element named S1.
			using var lease = Import(universe, "vpetest,S1,$knocker,$S1");
			var session = Session(lease.Revision, false);
			Assert.That(Wait(session.StartAsync()).Started, Is.True);
			Expect(universe, f => true, "a frame");

			session.PublishNamed("S1", 1);
			Expect(universe, f => f[2] == 255, "the element named S1 shows");
			session.PublishNamed("Knocker", 1);
			var shown = ExpectIndex(universe, f => f[1] == 255, "the knocker shows");
			session.PublishNamed("KNOCKER", 1);
			session.PublishNamed("knocker", 0);
			// The knocker is on from that frame until it goes off.
			Expect(universe, f => f[1] == 0, "the knocker goes off, however it is spelled", shown + 1);
			Assert.That(_capture.Frames(universe).All(f => f[0] == 0), Is.True, "\"S1\" as a name never drives solenoid 1");
			Wait(session.StopAsync());
		}

		[Test]
		public void TableConfigsDoNotSilenceTheIniRows()
		{
			// libdof reads no effect assignments from a table config: loaded, one without AddLedControlConfig would leave
			// the game without effects. A session passes no table file, so none is looked up, also in a revision whose
			// global config still names a table config pattern, as earlier imports wrote it, and after a rollback to one.
			const int universe = 304;
			var source = Path.Combine(_dir, "source");
			Directory.CreateDirectory(source);
			foreach (var name in new[] { "bound", "vpetest_l1" }) {
				File.WriteAllText(Path.Combine(source, name + ".xml"),
					$"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Table><TableName>{name}</TableName><AddLedControlConfig>false</AddLedControlConfig></Table>\r\n");
			}
			string earlier;
			using (var lease = Import(universe, "vpetest,S1")) {
				var revision = lease.Revision;
				earlier = revision.Id;
				Assert.That(revision.TableConfigs, Is.EqualTo(new[] { "bound", "vpetest_l1" }));
				var global = System.Xml.Linq.XDocument.Load(revision.GlobalConfigFile);
				var patterns = global.Root.Element("TableConfigFilePatterns");
				Assert.That(patterns.Elements(), Is.Empty, "a new import names no pattern");
				DrivesTheIniRow(revision, universe, "a new import");

				patterns.Add(new System.Xml.Linq.XElement("FilePattern", Path.Combine(revision.TablesDir, "{TableName}.xml")));
				global.Save(revision.GlobalConfigFile);
				DrivesTheIniRow(revision, universe, "a revision with a pattern");
			}

			using (Import(universe, "vpetest,S2")) {
			}
			var store = new DofConfigStore(Path.Combine(_dir, "store"));
			Assert.That(store.Rollback(), Is.True);
			using (var lease = store.Acquire()) {
				Assert.That(lease.Revision.Id, Is.EqualTo(earlier));
				DrivesTheIniRow(lease.Revision, universe, "the revision a rollback restored");
			}
		}

		private void DrivesTheIniRow(DofRevision revision, int universe, string what)
		{
			var session = Session(revision, false);
			var started = Wait(session.StartAsync());
			Assert.That(started.Started, Is.True, started.Error);
			var mark = _capture.Mark(universe);
			session.Publish(Game, DofElementKind.Solenoid, 1, 255);
			Expect(universe, f => f[0] == 255, $"the ini row drives the port ({what})", mark);
			Wait(session.StopAsync());
		}

		[Test]
		public void RunsFromAFolderWithANonAsciiName()
		{
			const int universe = 303;
			var dir = Path.Combine(_dir, "Jürgen");
			Directory.CreateDirectory(dir);
			var windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
			if (windows) {
				try {
					DofPaths.ToNative(dir);
				} catch (DofException) {
					Assert.Ignore("This volume has no short (8.3) names, so libdof cannot open a non-ASCII folder on Windows.");
				}
			}
			using var lease = Import(dir, universe, "vpetest,S1");
			if (windows) {
				var global = System.Xml.Linq.XDocument.Load(lease.Revision.GlobalConfigFile).Root;
				Assert.That(DofPaths.IsAscii(global.Element("CabinetConfigFilePattern").Value), Is.True, "libdof opens files through the ANSI API on Windows");
				Assert.That(DofPaths.IsAscii(global.Element("IniFilesPath").Value), Is.True);
			}
			var session = Session(lease.Revision, false);
			var started = Wait(session.StartAsync());
			Assert.That(started.Started, Is.True, started.Error);
			Assert.That(started.Cabinet.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNet 1" }), "the explicit cabinet, not auto-configuration");
			session.Publish(Game, DofElementKind.Solenoid, 1, 255);
			Expect(universe, f => f[0] == 255, "the outputs work");
			Wait(session.StopAsync());
		}

		[Test]
		public void ACabinetWithAControllerLibdofCannotReadStartsTheOthers()
		{
			// The import accepts the cabinet without libdof reading it, so setup must not refuse it as a whole.
			const int universe = 305;
			using var lease = Import(_dir, universe, "vpetest,S1",
				"<ArtNet><Name>ArtNet 2</Name><Universe>unset</Universe><BroadcastAddress>127.0.0.1</BroadcastAddress></ArtNet>");
			var session = Session(lease.Revision, false);
			var started = Wait(session.StartAsync());
			Assert.That(started.Started, Is.True, started.Error);
			Assert.That(started.Cabinet.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNet 1" }), "the controller libdof cannot read is skipped");
			session.Publish(Game, DofElementKind.Solenoid, 1, 255);
			Expect(universe, f => f[0] == 255, "the other controller works");
			Wait(session.StopAsync());
		}

		[Test]
		public void ACabinetWithAControllerWhoseSettingsLibdofRefusesStartsTheOthers()
		{
			// libdof throws for some settings out of range (a PAC Id), and needs an Id or a number that some entries
			// leave out; each such controller is skipped, as one it cannot read is.
			const int universe = 307;
			using var lease = Import(_dir, universe, "vpetest,S1",
				"<PacUIO Name=\"Out of range\" Id=\"2\" /><PacLed64 Name=\"Also out of range\" Id=\"5\" />"
				+ "<PacUIO Name=\"No Id\" /><PacLed64 Name=\"Bad Id\" Id=\"one\" /><LedWiz><Name>No number</Name></LedWiz>");
			var session = Session(lease.Revision, false);
			var started = Wait(session.StartAsync());
			Assert.That(started.Started, Is.True, started.Error);
			Assert.That(started.Cabinet.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNet 1" }), "the controllers libdof refuses are skipped");
			session.Publish(Game, DofElementKind.Solenoid, 1, 255);
			Expect(universe, f => f[0] == 255, "the other controller works");
			Wait(session.StopAsync());
		}

		[Test, Platform("Win")]
		public void ReopensAStoreThroughAnotherPathToItsFolder()
		{
			// The import writes the store's folder into the generated config as it was given, here through a junction. A
			// session given the same folder through another junction reads it: what counts is the folder, not its path.
			const int universe = 308;
			var dir = Path.Combine(_dir, "real");
			Directory.CreateDirectory(dir);
			try {
				DofPaths.ToNative(dir);
			} catch (DofException) {
				Assert.Ignore("The temporary folder has a non-ASCII name and this volume no short (8.3) names, so libdof cannot open it on Windows.");
			}
			var imported = Path.Combine(_dir, "imported through");
			var reopened = Path.Combine(_dir, "reopened through");
			Junction(imported, dir);
			try {
				Junction(reopened, dir);
				using var lease = Import(imported, universe, "vpetest,S1");
				var basePath = Path.Combine(reopened, Path.GetRelativePath(imported, lease.Revision.BasePath));
				var session = new DofSession(new DofSessionOptions {
					BasePath = basePath,
					RomName = "vpetest_l1",
					GameSession = Game,
					WaitForSnapshot = false,
				});
				_sessions.Add(session);
				var started = Wait(session.StartAsync());
				Assert.That(started.Started, Is.True, started.Error);
				Assert.That(started.Cabinet.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNet 1" }), "the imported cabinet");
				session.Publish(Game, DofElementKind.Solenoid, 1, 255);
				Expect(universe, f => f[0] == 255, "the outputs work");
				Wait(session.StopAsync());
			} finally {
				// Removes the junctions, not the folder they point to.
				if (Directory.Exists(reopened)) {
					Directory.Delete(reopened);
				}
				Directory.Delete(imported);
			}
		}

		private static void Junction(string link, string target)
		{
			using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") {
				UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
			});
			mklink.WaitForExit();
			Assert.That(mklink.ExitCode, Is.EqualTo(0), "the junction is created");
		}

		[Test, Platform("Win")]
		public void RunsAUnicodeStoreReachedThroughAJunction()
		{
			// The import writes the store's 8.3 spelling into the generated config; the root is given through the junction.
			const int universe = 306;
			var dir = Path.Combine(_dir, "Jürgen");
			Directory.CreateDirectory(dir);
			try {
				DofPaths.ToNative(dir);
			} catch (DofException) {
				Assert.Ignore("This volume has no short (8.3) names, so libdof cannot open a non-ASCII folder on Windows.");
			}
			using var lease = Import(dir, universe, "vpetest,S1");
			var link = Path.Combine(_dir, "link");
			Junction(link, dir);
			try {
				// The temporary folder may have a non-ASCII name too: its 8.3 spelling, still through the junction.
				var basePath = Path.Combine(DofPaths.ToNative(link), Path.GetRelativePath(dir, lease.Revision.BasePath));
				Assert.That(DofPaths.IsAscii(basePath), Is.True);
				var session = new DofSession(new DofSessionOptions {
					BasePath = basePath,
					RomName = "vpetest_l1",
					GameSession = Game,
					WaitForSnapshot = false,
				});
				_sessions.Add(session);
				var started = Wait(session.StartAsync());
				Assert.That(started.Started, Is.True, started.Error);
				Assert.That(started.Cabinet.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNet 1" }), "the imported cabinet");
				session.Publish(Game, DofElementKind.Solenoid, 1, 255);
				Expect(universe, f => f[0] == 255, "the outputs work");
				Wait(session.StopAsync());
			} finally {
				// Removes the junction, not the folder it points to.
				Directory.Delete(link);
			}
		}

		[Test]
		public void RunsAGameFromSnapshotToStop()
		{
			const int universe = 300;
			using var lease = Import(universe, "vpetest,S1,L2 I#80,W3");
			var session = Session(lease.Revision);

			// The snapshot arrives before DOF runs; the outputs stay dark until its boundary.
			session.Publish(Game, DofElementKind.Solenoid, 1, 255);
			var started = Wait(session.StartAsync());
			Assert.That(started.Started, Is.True, started.Error);
			Assert.That(started.Cabinet.Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNet 1" }));
			Expect(universe, f => true, "a frame");
			Thread.Sleep(100);
			Assert.That(_capture.Frames(universe).All(f => f.Take(8).All(v => v == 0)), Is.True, "dark until the snapshot is in");

			session.PublishSnapshotBoundary(Game);
			Expect(universe, f => f[0] == 255, "the snapshot shows");
			session.Publish(Game, DofElementKind.Lamp, 2, 1);
			session.Publish(Game, DofElementKind.Switch, 3, 1);
			Expect(universe, f => f[0] == 255 && f[1] == 128 && f[2] == 255, "changes show");

			// Each mark comes before the transition, so its frame is after the mark.
			var mark = _capture.Mark(universe);
			var paused = Wait(session.PauseAsync());
			Assert.That(paused.Settled, Is.True, paused.ToString());
			Expect(universe, f => f.Take(8).All(v => v == 0), "pause darkens", mark);
			session.Publish(Game, DofElementKind.Solenoid, 1, 0);

			mark = _capture.Mark(universe);
			var resumed = Wait(session.ResumeAsync());
			Assert.That(resumed.Settled, Is.True, resumed.ToString());
			Expect(universe, f => f[0] == 0 && f[1] == 128 && f[2] == 255, "resume shows the current state", mark);

			var stopped = Wait(session.StopAsync());
			Assert.That((stopped.Completed, stopped.Gate.Settled, stopped.Error), Is.EqualTo((true, true, (string)null)));
			Assert.That(_capture.Latest(universe).Take(8), Is.All.EqualTo((byte)0), "stop turns everything off");
			Assert.That(session.Diagnostics.Delivered, Is.EqualTo(4));
		}

		[Test]
		public void ASecondGameStartsAfterTheFirstCompleted()
		{
			const int universe = 301;
			using var lease = Import(universe, "vpetest,S1");
			var first = Session(lease.Revision);
			Wait(first.StartAsync());
			Wait(first.StopAsync());
			Assert.That(first.Completion.IsCompleted, Is.True);

			var second = Session(lease.Revision);
			var started = Wait(second.StartAsync());
			Assert.That(started.Started, Is.True, started.Error);
			second.PublishSnapshotBoundary(Game);
			second.Publish(Game, DofElementKind.Solenoid, 1, 255);
			Expect(universe, f => f[0] == 255, "the second game drives the outputs", _capture.Count(universe));
			Wait(second.StopAsync());
		}

		[Test]
		public void ImportsARealConfigToolZip()
		{
			// Opt-in: a Config Tool download is personal (it carries the user's account details), so it is not part of
			// the repository. Set VPE_DOF_SAMPLE_ZIP to its path to run this.
			var zip = Environment.GetEnvironmentVariable("VPE_DOF_SAMPLE_ZIP");
			if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) {
				Assert.Ignore("VPE_DOF_SAMPLE_ZIP is not set.");
			}
			var store = new DofConfigStore(Path.Combine(_dir, "store"));
			var report = new DofImporter(store).Import(zip);
			TestContext.Out.WriteLine(report.ToString());
			Assert.That(report.Accepted, Is.True, report.ToString());
			var catalog = DofCatalog.Load(store.Active);
			Assert.That(catalog.Rows, Is.Not.Empty);
			Assert.That(catalog.Devices.All(d => d.Ports.Count > 0), Is.True, "the Config Tool header names the ports");
			TestContext.Out.WriteLine($"{catalog.Rows.Count} rows, {catalog.TableMappings.Count} table mappings, devices {string.Join(", ", catalog.Devices.Select(d => d.Number))}");
		}
	}
}
