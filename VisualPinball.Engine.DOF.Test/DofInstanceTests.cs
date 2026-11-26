// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace VisualPinball.Engine.DOF.Test
{
	/// <summary>
	/// Runs the real native library against Art-Net cabinets on localhost. libdof's state is process-wide, so these
	/// tests run one at a time, each with its own universes so frames of an earlier test cannot match.
	/// </summary>
	[TestFixture, NonParallelizable]
	public class DofInstanceTests
	{
		// Ports of the test row: 1 = S1, 2 = S2 at intensity 128 (I#80), 3 = static ON, 4 = inverted S3, 5 = switch 4.
		private const string TestRow = "vpetest,S1,S2 I#80,ON,S3 Invert,W4";

		private static int _nextUniverse = 100;
		private ArtNetCapture _capture;
		private readonly ConcurrentQueue<string> _log = new ConcurrentQueue<string>();

		[OneTimeSetUp]
		public void SetUpCapture()
		{
			DofLibrary.EnsureCompatible();
			_capture = new ArtNetCapture();
			DofLibrary.SetLog((level, message) => _log.Enqueue($"[{level}] {message}"), DofLogLevel.Debug);
		}

		[OneTimeTearDown]
		public void TearDownCapture()
		{
			DofLibrary.SetLog(null);
			_capture?.Dispose();
		}

		[TearDown]
		public void DumpLogOnFailure()
		{
			if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed) {
				foreach (var line in _log) {
					TestContext.Out.WriteLine(line);
				}
			}
			while (_log.TryDequeue(out _)) { }
		}

		private static ArtNetUnit Unit(string name, string address = "127.0.0.1") => new ArtNetUnit { Name = name, Universe = Interlocked.Increment(ref _nextUniverse), Address = address };

		private static TestConfig StandardConfig(ArtNetUnit unit) => new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, TestRow, "vpeother,S5,0,0,0");

		private static DofInstance Start(TestConfig config, string rom = "vpetest", bool closed = false)
		{
			var dof = DofInstance.Create(config.Root);
			try {
				if (closed) {
					Assert.That(dof.SetOutputGate(false), Is.GreaterThan(0));
				}
				dof.Init(config.TableFile, rom);
				return dof;
			} catch {
				dof.Dispose();
				throw;
			}
		}

		/// <summary>Waits until every unit has the generation's state, or a later generation replaced it.</summary>
		private static DofGateState WaitSettled(DofInstance dof, long generation, int timeoutMs = 2000)
		{
			var watch = Stopwatch.StartNew();
			var state = dof.GetGateState(generation);
			while (!state.Settled && !state.Superseded && watch.ElapsedMilliseconds < timeoutMs) {
				Thread.Sleep(5);
				state = dof.GetGateState(generation);
			}
			return state;
		}

		/// <summary>Waits for a frame that matches and fails with the last frame received when none does.</summary>
		private void Expect(int universe, Func<byte[], bool> match, string what, int after = 0)
		{
			if (_capture.WaitFor(universe, match, after) == null) {
				var latest = _capture.Latest(universe);
				Assert.Fail($"{what}: no matching frame on universe {universe}; last of {_capture.Count(universe)}: "
					+ (latest == null ? "none" : string.Join(",", latest.Take(8))));
			}
		}

		[Test]
		public void ReportsAbiAndLibdofVersion()
		{
			Assert.That(DofLibrary.NativeAbiVersion, Is.EqualTo(4));
			Assert.That(DofLibrary.LibdofVersion, Does.Match(@"^\d+\.\d+\.\d+"));
		}

		[Test]
		public void ListsRomRowsWithTheirDevice()
		{
			using var config = new TestConfig().GlobalConfig().Ini(1, TestRow, "vpeother,S5").Ini(51, "vpetest,S1", "third,0");
			var rows = DofLibrary.RomNames(config.Root, config.TableFile);
			Assert.That(rows, Is.EquivalentTo(new[] {
				new DofRomRow(1, "vpetest"), new DofRomRow(1, "vpeother"), new DofRomRow(51, "vpetest"), new DofRomRow(51, "third"),
			}));
		}

		[Test]
		public void RomCatalogRunsNextToALiveInstance()
		{
			var unit = Unit("ArtNetA");
			using var live = StandardConfig(unit);
			using var other = new TestConfig().GlobalConfig().Ini(1, "elsewhere,S1");
			using var dof = Start(live);
			Assert.That(DofLibrary.RomNames(other.Root, other.TableFile), Is.EqualTo(new[] { new DofRomRow(1, "elsewhere") }));

			// The live instance still reads its own configuration.
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "frame");
			dof.Finish();
		}

		[Test]
		public void DescribesControllersAndToys()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config);
			var cabinet = dof.Describe();
			Assert.That(cabinet.Controllers.Select(c => (c.Type, c.Name)), Is.EqualTo(new[] { ("ArtNet", "ArtNetA") }));
			Assert.That(cabinet.Controllers[0].Outputs, Is.EqualTo(512));
			Assert.That(cabinet.Toys.Select(t => (t.Type, t.Name)), Does.Contain(("LedWizEquivalent", "LedWizEquivalent 1")));
			dof.Finish();
		}

		[Test]
		public void TableElementsDriveTheirPorts()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config);

			// Static ON is on as soon as DOF runs. Elements start at 0, so the inverted S3 lights only after S3 changes back
			// to 0, as in C# DOF.
			Expect(unit.Universe, f => f[2] == 255 && f[3] == 0 && f[0] == 0, "static port");
			dof.Data('S', 3, 1);
			dof.Data('S', 3, 0);
			Expect(unit.Universe, f => f[2] == 255 && f[3] == 255 && f[0] == 0, "inverted port");

			dof.Data('S', 1, 1);
			dof.Data('S', 2, 1);
			dof.Data('S', 3, 1);
			dof.Data('W', 4, 1);
			Expect(unit.Universe, f => f[0] == 255 && f[1] == 128 && f[3] == 0 && f[4] == 255, "elements on");

			dof.Data('S', 1, 0);
			dof.Data('W', 4, 0);
			Expect(unit.Universe, f => f[0] == 0 && f[1] == 128 && f[4] == 0, "elements off");
			dof.Finish();
			Assert.That(_capture.Latest(unit.Universe).Take(8), Is.All.EqualTo((byte)0), "finish turns everything off");
		}

		[Test]
		public void InvertedEffectFollowsEveryChange()
		{
			// Ported from C#, where effect data is passed by value: libdof's full-range mapping used to remember the
			// value its inverting target had overwritten, and then dropped the change back to 0.
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,S3 Invert,S3,S6 Invert NoBool");
			using var dof = Start(config);
			dof.Data('S', 3, 1);
			Expect(unit.Universe, f => f[0] == 0 && f[1] == 255, "S3 on");
			dof.Data('S', 3, 0);
			Expect(unit.Universe, f => f[0] == 255 && f[1] == 0, "S3 off");
			dof.Data('S', 3, 1);
			Expect(unit.Universe, f => f[0] == 0 && f[1] == 255, "S3 on again");
			dof.Data('S', 6, 1);
			Expect(unit.Universe, f => f[2] == 254, "inverted without bool mapping");
			dof.Finish();
		}

		[Test]
		public void ClosedGateSendsZerosAndOpeningRestores()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			dof.Data('S', 3, 1);
			dof.Data('S', 3, 0);
			Expect(unit.Universe, f => f[0] == 255 && f[2] == 255 && f[3] == 255, "S1, static and inverted on");

			var closed = dof.SetOutputGate(false);
			var mark = _capture.Count(unit.Universe);
			var state = WaitSettled(dof, closed);
			Assert.That(state.Applied, Is.True);
			Assert.That((state.Written, state.Pending, state.Failed), Is.EqualTo((1, 0, 0)));
			Expect(unit.Universe, f => f.Take(8).All(v => v == 0), "all off after closing", mark);

			// Effects keep running while closed, but nothing reaches the outputs.
			dof.Data('S', 2, 1);
			dof.Data('S', 3, 1);
			Thread.Sleep(300);
			var whileClosed = _capture.Frames(unit.Universe).Skip(mark).ToList();
			Assert.That(whileClosed, Is.Not.Empty);
			Assert.That(whileClosed.All(f => f.Take(8).All(v => v == 0)), Is.True, "no output while closed");

			var open = dof.SetOutputGate(true);
			Assert.That(open, Is.GreaterThan(closed));
			Assert.That(WaitSettled(dof, open).Written, Is.EqualTo(1));
			Expect(unit.Universe, f => f[0] == 255 && f[1] == 128 && f[2] == 255 && f[3] == 0, "opening restores the current state", mark);
			dof.Finish();
		}

		[Test]
		public void LatchedGateTurnsOpeningsIntoClosings()
		{
			var unit = Unit("ArtNetA");
			using (var config = StandardConfig(unit))
			using (var dof = Start(config, closed: true)) {
				dof.Data('S', 1, 1);
				dof.Data('S', 3, 1);
				dof.Data('S', 3, 0);
				Expect(unit.Universe, f => true, "the closed gate sends zeros");

				// An opening still queued for DOF's main thread when the latch comes applies as a closing, and so does
				// every later one: the outputs never show the effects' state again.
				var open = dof.SetOutputGate(true);
				var latched = dof.LatchOutputGate();
				Assert.That(latched, Is.GreaterThan(open));
				var state = WaitSettled(dof, latched);
				Assert.That((state.Applied, state.Written), Is.EqualTo((true, 1)), "the latch reaches the unit");
				var mark = _capture.Count(unit.Universe);
				var later = dof.SetOutputGate(true);
				Assert.That(later, Is.GreaterThan(latched));
				Assert.That(WaitSettled(dof, later).Written, Is.EqualTo(1), "a later opening confirms the closed state");
				dof.Data('S', 2, 1);
				Thread.Sleep(300);
				var after = _capture.Frames(unit.Universe).Skip(mark).ToList();
				Assert.That(after, Is.Not.Empty);
				Assert.That(after.All(f => f.Take(8).All(v => v == 0)), Is.True, "nothing opens a latched gate");
				dof.Finish();
			}

			// The latch lasts as long as its instance: the next one opens.
			var next = Unit("ArtNetNext");
			using (var config = StandardConfig(next))
			using (var dof = Start(config)) {
				dof.Data('S', 1, 1);
				Expect(next.Universe, f => f[0] == 255, "a new instance starts unlatched");
				dof.Finish();
			}
		}

		[Test]
		public void LatchAppliesBeforeInit()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = DofInstance.Create(config.Root);
			Assert.That(dof.LatchOutputGate(), Is.GreaterThan(0));
			dof.Init(config.TableFile, "vpetest");
			dof.SetOutputGate(true);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => true, "frames");
			Thread.Sleep(300);
			Assert.That(_capture.Frames(unit.Universe).All(f => f.Take(8).All(v => v == 0)), Is.True, "init keeps the latch");
			dof.Finish();
			dof.Dispose();
			Assert.That(dof.LatchOutputGate(), Is.EqualTo(-1), "no instance after dispose");
		}

		[Test]
		public void GateClosedBeforeInitKeepsTheSessionDark()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config, closed: true);
			dof.Data('S', 1, 1);
			dof.Data('S', 3, 1);
			dof.Data('S', 3, 0);
			Expect(unit.Universe, f => true, "the closed gate still sends zeros");
			Thread.Sleep(300);
			Assert.That(_capture.Frames(unit.Universe).All(f => f.Take(8).All(v => v == 0)), Is.True, "static, inverted and S1 stay dark");

			var open = dof.SetOutputGate(true);
			Assert.That(WaitSettled(dof, open).Written, Is.EqualTo(1));
			Expect(unit.Universe, f => f[0] == 255 && f[2] == 255 && f[3] == 255, "S1, static and inverted on after opening");
			dof.Finish();
		}

		[Test]
		public void EveryUnitWritesTheGate()
		{
			var a = Unit("ArtNetA");
			var b = Unit("ArtNetB");
			using var config = new TestConfig().GlobalConfig().Cabinet(4, a, b).Ini(1, "vpetest,S1,0,0,0,S1");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(a.Universe, f => f[0] == 255, "frame");
			Expect(b.Universe, f => f[0] == 255, "frame");

			var closed = dof.SetOutputGate(false);
			var state = WaitSettled(dof, closed);
			Assert.That((state.Written, state.Pending, state.Failed), Is.EqualTo((2, 0, 0)));
			Assert.That(dof.GetGatePending(closed), Is.Empty);
			dof.Finish();
		}

		[Test]
		public void FailedWriteIsReported()
		{
			var good = Unit("ArtNetGood");
			var bad = Unit("ArtNetBad", "999.0.0.1");
			using var config = new TestConfig().GlobalConfig().Cabinet(4, good, bad).Ini(1, "vpetest,S1,0,0,0,S1");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(good.Universe, f => f[0] == 255, "frame");

			var closed = dof.SetOutputGate(false);
			var state = WaitSettled(dof, closed);
			Assert.That((state.Written, state.Pending, state.Failed), Is.EqualTo((1, 0, 1)));
			Assert.That(dof.GetGatePending(closed), Is.EqualTo(new[] { "ArtNet ArtNetBad" }));

			// The final all-off fails for the bad unit: finish says so, and the good unit still got it.
			var mark = _capture.Count(good.Universe);
			var error = Assert.Throws<DofException>(() => dof.Finish());
			Assert.That(error.Message, Does.Contain("ArtNetBad"));
			Assert.That(error.Message, Does.Not.Contain("ArtNetGood"));
			Expect(good.Universe, f => f.Take(4).All(v => v == 0), "the good unit's final all-off", mark);
		}

		[Test]
		public void PortTestGoesThroughTheGate()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, new Dictionary<int, string> { [9] = "Matrix1" }, unit).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			Assert.That(dof.TestOutput(1, 6, 200), Is.EqualTo(DofStatus.Ok));
			Expect(unit.Universe, f => f[5] == 200, "frame");

			Assert.That(dof.TestOutput(1, 99, 255), Is.EqualTo(DofStatus.InvalidArgument), "unknown port");
			Assert.That(dof.TestOutput(2, 1, 255), Is.EqualTo(DofStatus.InvalidArgument), "unknown device");
			Assert.That(dof.TestOutput(1, 9, 255), Is.EqualTo(DofStatus.InvalidArgument), "a port naming a toy");

			var closed = dof.SetOutputGate(false);
			WaitSettled(dof, closed);
			var mark = _capture.Count(unit.Universe);
			Assert.That(dof.TestOutput(1, 7, 255), Is.EqualTo(DofStatus.Ok));
			Thread.Sleep(200);
			Assert.That(_capture.Frames(unit.Universe).Skip(mark).All(f => f[6] == 0 && f[5] == 0), Is.True, "port test is gated");
			dof.Finish();
		}

		[Test]
		public void OnlyOneInstanceAtATime()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = DofInstance.Create(config.Root);
			var busy = Assert.Throws<DofException>(() => DofInstance.Create(config.Root));
			Assert.That(busy.Status, Is.EqualTo(DofStatus.Busy));
			dof.Init(config.TableFile, "vpetest");
			Assert.That(Assert.Throws<DofException>(() => dof.Init(config.TableFile, "vpetest")).Status, Is.EqualTo(DofStatus.InvalidState));
			dof.Finish();
			Assert.That(dof.SetOutputGate(false), Is.EqualTo(-1), "no gate after finish");
			dof.Dispose();
			using var next = DofInstance.Create(config.Root);
		}

		[Test]
		public void FailedInitLeavesTheProcessUsable()
		{
			using var empty = new TestConfig();
			var dof = DofInstance.Create(empty.Root);
			var error = Assert.Throws<DofException>(() => dof.Init(empty.TableFile, "vpetest"));
			Assert.That(error.Message, Does.Contain("GlobalConfig_B2SServer.xml"));
			Assert.That(dof.SetOutputGate(false), Is.EqualTo(-1));
			dof.Dispose();

			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var next = Start(config);
			next.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "frame");
			next.Finish();
		}

		[Test]
		public void SuccessiveSessionsUseTheirOwnCabinet()
		{
			var first = Unit("ArtNetFirst");
			using (var config = StandardConfig(first))
			using (var dof = Start(config)) {
				dof.SetOutputGate(false);
				dof.SetOutputGate(true);
				dof.Data('S', 1, 1);
				Expect(first.Universe, f => f[0] == 255, "frame");
				dof.Finish();
			}
			var firstCount = _capture.Count(first.Universe);

			var second = Unit("ArtNetSecond");
			using (var config = new TestConfig().GlobalConfig().Cabinet(8, second).Ini(1, "vpetest,0,S1")) {
				using var dof = Start(config);
				Assert.That(dof.Describe().Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNetSecond" }));
				dof.Data('S', 1, 1);
				Expect(second.Universe, f => f[1] == 255 && f[0] == 0, "frame");
				Thread.Sleep(100);
				dof.Finish();
			}
			Assert.That(_capture.Count(first.Universe), Is.EqualTo(firstCount), "the first cabinet is gone");
		}

		[Test]
		public void ConfinementKeepsFilesOutsideTheConfigurationUntouched()
		{
			// The log pattern resolves next to the configuration folder, outside it, and asks for the log to be cleared.
			var unit = Unit("ArtNetA");
			var name = $"canary-{Guid.NewGuid():N}.log";
			using var config = new TestConfig().GlobalConfig(logPattern: "{GlobalConfigDir}/../../" + name, clearLog: true).Cabinet(8, unit).Ini(1, TestRow);
			var canary = Path.Combine(Path.GetDirectoryName(config.Root), name);
			File.WriteAllText(canary, "canary");
			try {
				using (var dof = Start(config)) {
					dof.Data('S', 1, 1);
					Expect(unit.Universe, f => f[0] == 255, "frame");
					dof.Finish();
				}
				Assert.That(File.Exists(canary), Is.True, "the log file outside the configuration was not deleted");
				Assert.That(File.ReadAllText(canary), Is.EqualTo("canary"), "and not written");
			} finally {
				File.Delete(canary);
			}
		}

		[Test]
		public void BoundTableConfigCanReplaceTheIniRows()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig(tableConfigPattern: "{GlobalConfigDir}/{TableName}.xml").Cabinet(8, unit).Ini(1, TestRow)
				.Write("test.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Table><TableName>test</TableName><AddLedControlConfig>false</AddLedControlConfig></Table>\n");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			dof.TestOutput(1, 8, 1);
			Expect(unit.Universe, f => f[7] == 1, "frame");
			Thread.Sleep(200);
			Assert.That(_capture.Frames(unit.Universe).All(f => f[0] == 0 && f[2] == 0), Is.True, "the ini rows are not loaded");
			dof.Finish();
		}

		[Test]
		public void InvalidTableConfigFallsBackToTheIniRows()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig(tableConfigPattern: "{GlobalConfigDir}/{TableName}.xml").Cabinet(8, unit).Ini(1, TestRow)
				.Write("test.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<NotATable />\n");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "frame");
			dof.Finish();
		}

		[Test]
		public void QueuedInputIsReported()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config);
			Assert.That(dof.PendingInput, Is.GreaterThanOrEqualTo(0));
			for (var i = 0; i < 1000; i++) {
				dof.Data('L', 1 + i % 64, i & 1);
			}
			var watch = Stopwatch.StartNew();
			while (dof.PendingInput > 0 && watch.ElapsedMilliseconds < 2000) {
				Thread.Sleep(5);
			}
			Assert.That(dof.PendingInput, Is.EqualTo(0));
			dof.Finish();
		}

		[Test]
		public void MalformedGlobalConfigFailsInit()
		{
			using var config = new TestConfig().Write("GlobalConfig_B2SServer.xml", "<?xml version=\"1.0\"?>\n<GlobalConfig><IniFilesPath>")
				.Cabinet(8, Unit("ArtNetA")).Ini(1, TestRow);
			using var dof = DofInstance.Create(config.Root);
			Assert.Throws<DofException>(() => dof.Init(config.TableFile, "vpetest"));
			Assert.That(dof.SetOutputGate(false), Is.EqualTo(-1));
		}

		[Test]
		public void InvalidCabinetFailsInsteadOfAutoConfiguring()
		{
			using var config = new TestConfig().GlobalConfig().Write("Cabinet.xml", "<?xml version=\"1.0\"?>\n<NotACabinet />\n").Ini(1, TestRow);
			using var dof = DofInstance.Create(config.Root);
			var error = Assert.Throws<DofException>(() => dof.Init(config.TableFile, "vpetest"));
			Assert.That(error.Message, Does.Contain("Cabinet.xml"));
		}

		[Test]
		public void MissingCabinetFailsInsteadOfAutoConfiguring()
		{
			using var config = new TestConfig().GlobalConfig().Ini(1, TestRow);
			using var dof = DofInstance.Create(config.Root);
			var error = Assert.Throws<DofException>(() => dof.Init(config.TableFile, "vpetest"));
			Assert.That(error.Message, Does.Contain("Cabinet.xml"));
		}

		[Test]
		public void IniFilesOutsideTheConfigurationAreNotRead()
		{
			// A sibling folder whose name starts with the configuration's, reached by absolute path and through "..".
			using var config = new TestConfig();
			var sibling = config.Root + "-sibling";
			Directory.CreateDirectory(sibling);
			try {
				File.WriteAllText(Path.Combine(sibling, "directoutputconfig.ini"), TestConfig.IniText("outside,S1"));
				config.GlobalConfig(iniFilesPath: sibling);
				Assert.That(DofLibrary.RomNames(config.Root, config.TableFile), Is.Empty, "absolute path to a sibling");
				config.GlobalConfig(iniFilesPath: "{GlobalConfigDir}/../../" + Path.GetFileName(sibling));
				Assert.That(DofLibrary.RomNames(config.Root, config.TableFile), Is.Empty, "relative path out of the folder");

				// Control: the same ini inside the configuration is read.
				config.GlobalConfig().Ini(1, "inside,S1");
				Assert.That(DofLibrary.RomNames(config.Root, config.TableFile), Is.EqualTo(new[] { new DofRomRow(1, "inside") }));
			} finally {
				Directory.Delete(sibling, true);
			}
		}

		[Test]
		public void IniFilesBehindASymbolicLinkAreNotRead()
		{
			using var config = new TestConfig();
			var outside = config.Root + "-target";
			Directory.CreateDirectory(outside);
			try {
				File.WriteAllText(Path.Combine(outside, "directoutputconfig.ini"), TestConfig.IniText("outside,S1"));
				var link = Path.Combine(config.Root, "linked");
				try {
					Directory.CreateSymbolicLink(link, outside);
				} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
					Assert.Ignore("Symbolic links cannot be created here: " + e.Message);
				}
				config.GlobalConfig(iniFilesPath: link);
				Assert.That(DofLibrary.RomNames(config.Root, config.TableFile), Is.Empty);
				Directory.Delete(link);
			} finally {
				Directory.Delete(outside, true);
			}
		}

		[Test]
		public void RunsFromAFolderWithNonAsciiCharacters()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig("dof-Ünïcödé-日本-" + Guid.NewGuid().ToString("N")).GlobalConfig().Cabinet(8, unit).Ini(1, TestRow);
			Assert.That(DofLibrary.RomNames(config.Root, config.TableFile), Is.EqualTo(new[] { new DofRomRow(1, "vpetest") }));
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "frame");
			dof.Finish();
		}

		[Test]
		public void RepeatedCloseSettles()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "frame");
			var first = dof.SetOutputGate(false);
			Assert.That(WaitSettled(dof, first).Written, Is.EqualTo(1));
			var mark = _capture.Count(unit.Universe);
			var second = dof.SetOutputGate(false);
			Assert.That(second, Is.GreaterThan(first));
			var state = WaitSettled(dof, second);
			Expect(unit.Universe, f => f.Take(8).All(v => v == 0), "a fresh all-off for the second close", mark);
			Assert.That((state.Applied, state.Superseded, state.Written, state.Pending), Is.EqualTo((true, false, 1, 0)));
			dof.Finish();
		}

		[Test]
		public void QuickCloseAndOpenSettleBoth()
		{
			// Closing and opening right after each other: the close either reaches the unit or is superseded by the open
			// before it does. Both settle, and the unit ends up open.
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "frame");
			var superseded = 0;
			for (var i = 0; i < 20; i++) {
				var closed = dof.SetOutputGate(false);
				var open = dof.SetOutputGate(true);
				var closedState = WaitSettled(dof, closed);
				var openState = WaitSettled(dof, open);
				Assert.That(closedState.Settled || closedState.Superseded, Is.True, $"close {i}: {closedState}");
				Assert.That((openState.Applied, openState.Superseded, openState.Written), Is.EqualTo((true, false, 1)), $"open {i}");
				if (closedState.Superseded) {
					superseded++;
				}
			}
			TestContext.Out.WriteLine($"{superseded} of 20 closes were superseded");
			var mark = _capture.Count(unit.Universe);
			Expect(unit.Universe, f => f[0] == 255 && f[2] == 255, "open at the end", mark);
			dof.Finish();
		}

		[Test]
		public void NamedElementsDriveConditionsAndPorts()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,($left=1 and $right=1),$flipper,$kicker I#80");
			using var dof = Start(config);
			dof.DataNamed("left", 1);
			dof.DataNamed("flipper", 1);
			Expect(unit.Universe, f => f[0] == 0 && f[1] == 255, "flipper on, condition false");
			dof.DataNamed("RIGHT", 1);
			dof.DataNamed("kicker", 1);
			Expect(unit.Universe, f => f[0] == 255 && f[1] == 255 && f[2] == 128, "condition true, names case-insensitive");
			dof.DataNamed("left", 0);
			Expect(unit.Universe, f => f[0] == 0 && f[2] == 128, "condition false again");
			dof.Finish();
		}

		[Test]
		public void ConditionNamesTheWholeElement()
		{
			// As in C# DOF, "S1X" is not a table element: the condition is dropped instead of following S1.
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,(S1X=1),S1");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[1] == 255, "S1 on");
			Thread.Sleep(200);
			Assert.That(_capture.Frames(unit.Universe).All(f => f[0] == 0), Is.True, "(S1X=1) does not follow S1");
			dof.Finish();
		}

		[Test]
		public void SignedDirectTriggerDrivesItsPort()
		{
			// The number of a direct trigger has to parse whole, and a sign is part of it, as in C# DOF.
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,E-1,S1X,S2");
			using var dof = Start(config);
			dof.Data('E', -1, 1);
			Expect(unit.Universe, f => f[0] == 255, "E-1 on");
			dof.Data('S', 1, 1);
			dof.Data('S', 2, 1);
			Expect(unit.Universe, f => f[2] == 255, "S2 on");
			Thread.Sleep(200);
			Assert.That(_capture.Frames(unit.Universe).All(f => f[1] == 0), Is.True, "S1X does not follow S1");
			dof.Finish();
		}

		[Test]
		public void OffQueuedBeforeAnOpeningIsNotSentAsOn()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			// Used while open, so the opening below has to send its frame.
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "S1 on");
			// Past the minimum effect duration (60 ms), which would hold S1 on for a moment after it goes off.
			Thread.Sleep(100);
			var start = _capture.Count(unit.Universe);
			Assert.That(WaitSettled(dof, dof.SetOutputGate(false)).Settled, Is.True);
			// The closing's frame was sent; frames sent before it may still be on their way.
			Expect(unit.Universe, f => f[0] == 0, "closed", start);
			var mark = _capture.Count(unit.Universe);

			// The opening comes behind a backlog of input that ends with S1 going off: it applies after that input.
			Backlog(dof);
			dof.Data('S', 1, 0);
			var open = dof.SetOutputGate(true);
			Assert.That(WaitSettled(dof, open).Settled, Is.True);
			Thread.Sleep(200);
			var after = _capture.Frames(unit.Universe).Skip(mark).ToList();
			Assert.That(after, Is.Not.Empty);
			Assert.That(after.All(f => f[0] == 0), Is.True, "S1 was off before the gate opened: " + string.Join(",", after.Select(f => f[0])));
			dof.Finish();
		}

		[Test]
		public void FinishDoesNotSendAnOffQueuedBeforeAnOpeningAsOn()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			// Used while open, so finish has to send its final frame.
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "S1 on");
			// Past the minimum effect duration (60 ms), which would hold S1 on for a moment after it goes off.
			Thread.Sleep(100);
			var start = _capture.Count(unit.Universe);
			Assert.That(WaitSettled(dof, dof.SetOutputGate(false)).Settled, Is.True);
			// The closing's frame was sent; frames sent before it may still be on their way.
			Expect(unit.Universe, f => f[0] == 0, "closed", start);
			var mark = _capture.Count(unit.Universe);

			// Finish right after the opening: the input queued before it still goes first.
			Backlog(dof);
			dof.Data('S', 1, 0);
			var queued = dof.PendingInput;
			var open = dof.SetOutputGate(true);
			dof.Finish();
			Thread.Sleep(100);
			// What the test is about: the opening came with input still queued before it, and finish applied it.
			Assert.That(queued, Is.GreaterThan(0), "input was queued before the opening");
			Assert.That(open, Is.GreaterThanOrEqualTo(0), "the opening was accepted");
			var opening = dof.GetGateState(open);
			Assert.That((opening.Applied, opening.Superseded), Is.EqualTo((true, false)), "finish applied the opening: " + opening);
			var after = _capture.Frames(unit.Universe).Skip(mark).ToList();
			Assert.That(after, Is.Not.Empty, "finish sends a final frame");
			Assert.That(after.All(f => f[0] == 0), Is.True, "S1 was off before the gate opened: " + string.Join(",", after.Select(f => f[0])));
		}

		[Test]
		public void OnQueuedAfterAClosingIsNotSent()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "S1 on");
			// Rounds with and without input still queued when the closing comes, so the main thread is busy or idle.
			for (var round = 0; round < 6; round++) {
				var start = _capture.Count(unit.Universe);
				dof.Data('S', 1, 0);
				Expect(unit.Universe, f => f[0] == 0, "S1 off", start);
				var mark = _capture.Count(unit.Universe);
				if (round % 2 == 1) {
					Backlog(dof);
				}
				var closed = dof.SetOutputGate(false);
				dof.Data('S', 1, 1);
				Assert.That(WaitSettled(dof, closed).Settled, Is.True, "round " + round);
				WaitForInput(dof);
				var after = _capture.Frames(unit.Universe).Skip(mark).ToList();
				Assert.That(after, Is.Not.Empty, "round " + round);
				Assert.That(after.All(f => f[0] == 0), Is.True, $"round {round}: S1 came on after the closing");

				var open = dof.SetOutputGate(true);
				Assert.That(WaitSettled(dof, open).Settled, Is.True, "round " + round);
				Expect(unit.Universe, f => f[0] == 255, "S1 shows once the gate opens", mark);
			}
			dof.Finish();
		}

		[Test]
		public void UnusedUnitIsUntouchedWhileOpen()
		{
			// Unit B has no port the ini row uses, so DOF leaves it alone while the gate is open. A closing still goes to it.
			var a = Unit("ArtNetA");
			var b = Unit("ArtNetB");
			using var config = new TestConfig().GlobalConfig().Cabinet(4, a, b).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(a.Universe, f => f[0] == 255, "frame");

			var closed = dof.SetOutputGate(false);
			var closing = WaitSettled(dof, closed);
			Assert.That((closing.Written, closing.Pending, closing.Failed, closing.Untouched), Is.EqualTo((2, 0, 0, 0)), closing.ToString());

			var open = dof.SetOutputGate(true);
			var opening = WaitSettled(dof, open);
			Assert.That((opening.Settled, opening.Written, opening.Pending, opening.Failed, opening.Untouched), Is.EqualTo((true, 1, 0, 0, 1)), opening.ToString());
			Assert.That(dof.GetGatePending(open), Is.Empty);
			dof.Finish();
		}

		private static int _backlogNames;

		/// <summary>Queues input faster than DOF's main thread takes it, so some is still queued when this returns.</summary>
		private static void Backlog(DofInstance dof)
		{
			// Numbered input is taken as fast as it is queued. A named element DOF does not know is added to the table, and
			// every named input is looked up through all of them, so the main thread falls further behind with each one.
			for (var i = 0; i < 4000; i++) {
				dof.DataNamed("Backlog" + Interlocked.Increment(ref _backlogNames), 1);
			}
		}

		private static void WaitForInput(DofInstance dof)
		{
			var watch = Stopwatch.StartNew();
			while (dof.PendingInput > 0 && watch.ElapsedMilliseconds < 2000) {
				Thread.Sleep(5);
			}
			Assert.That(dof.PendingInput, Is.Zero, "DOF's main thread processed the queued input");
			// The last change taken from the queue is processed and sent.
			Thread.Sleep(100);
		}

		[Test]
		public void EnumeratesDevicesWithoutAnInstance()
		{
			var devices = DofLibrary.EnumerateDevices();
			Assert.That(devices, Is.Not.Null);
			foreach (var device in devices) {
				TestContext.Out.WriteLine(device);
			}
		}
	}
}
