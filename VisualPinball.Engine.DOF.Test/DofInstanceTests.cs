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
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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
		private void Expect(int universe, Func<byte[], bool> match, string what, int after = 0) => ExpectIndex(universe, match, what, after);

		/// <summary>Like <see cref="Expect"/>, and returns the matching frame's number.</summary>
		private int ExpectIndex(int universe, Func<byte[], bool> match, string what, int after = 0)
		{
			var index = _capture.WaitForIndex(universe, match, after);
			if (index < 0) {
				var latest = _capture.Latest(universe);
				Assert.Fail($"{what}: no matching frame on universe {universe}; last of {_capture.Count(universe)}: "
					+ (latest == null ? "none" : string.Join(",", latest.Take(8))));
			}
			return index;
		}

		/// <summary>Waits for a frame received after both a frame number and a fresh mark.</summary>
		private int ExpectFrameAfter(int universe, int after, string what) => ExpectIndex(universe, f => true, what, Math.Max(after, _capture.Mark(universe)));

		/// <summary>Frames <paramref name="from"/> up to, not including, <paramref name="to"/> (all of them received from there on, without it).</summary>
		private List<byte[]> FramesBetween(int universe, int from, int to = int.MaxValue) => _capture.Frames(universe).Take(to).Skip(from).ToList();

		private static string Show(byte[] frame) => string.Join(",", frame.Take(8));

		/// <summary>Whether the thread is blocked: in a wait, a sleep, a join or on a lock.</summary>
		private static bool Blocked(Thread thread) => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;

		/// <summary>Waits until a thread that set <paramref name="entering"/> right before a call is blocked inside it.</summary>
		private static bool WaitBlockedInside(Thread thread, ManualResetEventSlim entering, int timeoutMs = 10000)
		{
			var watch = Stopwatch.StartNew();
			if (!entering.Wait(timeoutMs)) {
				return false;
			}
			while (!Blocked(thread)) {
				if (watch.ElapsedMilliseconds > timeoutMs) {
					return false;
				}
				Thread.Sleep(1);
			}
			return true;
		}

		/// <summary>Joins a thread that may not have been started.</summary>
		private static bool Joined(Thread thread, int timeoutMs) => (thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0 || thread.Join(timeoutMs);

		private void RestoreLog() => DofLibrary.SetLog((level, message) => _log.Enqueue($"[{level}] {message}"), DofLogLevel.Debug);

		/// <summary>
		/// Runs a cleanup step on a thread of its own and fails if it does not return in time: a Dispose waits for every
		/// call, and setting the log sink for the lines in flight, so a thread stuck in libdof would hang the run instead.
		/// </summary>
		private static void Bounded(Action cleanup, string what, int timeoutMs = 30000)
		{
			Exception error = null;
			var thread = new Thread(() => {
				try {
					cleanup();
				} catch (Exception e) {
					error = e;
				}
			}) { IsBackground = true };
			thread.Start();
			if (!thread.Join(timeoutMs)) {
				Assert.Fail($"{what} did not return within {timeoutMs} ms: a thread is stuck in libdof, and the instance stays alive.");
			}
			if (error != null) {
				ExceptionDispatchInfo.Capture(error).Throw();
			}
		}

		private void BoundedCleanup(DofInstance dof)
		{
			try {
				Bounded(RestoreLog, "Restoring the log sink");
			} finally {
				Bounded(dof.Dispose, "The cleanup Dispose");
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
			var shown = ExpectIndex(unit.Universe, f => f[0] == 255 && f[2] == 255 && f[3] == 255, "S1, static and inverted on");

			var closed = dof.SetOutputGate(false);
			var state = WaitSettled(dof, closed);
			Assert.That(state.Applied, Is.True);
			Assert.That((state.Written, state.Pending, state.Failed), Is.EqualTo((1, 0, 0)));
			// The updater resends the current values every 50 ms, and frames sent while still open can come after the
			// request. S1 is on from the frame above until the closing is written, so the first frame after it that's all
			// off is the closing's, and every frame after that one must stay off while the gate is closed.
			var closing = ExpectIndex(unit.Universe, f => f.Take(8).All(v => v == 0), "all off after closing", shown + 1);

			// Effects keep running while closed, but nothing reaches the outputs.
			dof.Data('S', 2, 1);
			dof.Data('S', 3, 1);
			ExpectFrameAfter(unit.Universe, closing + 1, "a frame while closed, after the effects changed");
			// Every frame received before the opening is requested was sent while closed.
			var baseline = _capture.Mark(unit.Universe);
			var whileClosed = FramesBetween(unit.Universe, closing + 1, baseline);
			var lit = whileClosed.FindIndex(f => f.Take(8).Any(v => v != 0));
			Assert.That(lit, Is.EqualTo(-1), () => $"no output while closed: frame {closing + 1 + lit} after the closing's frame {closing} is "
				+ Show(whileClosed[lit]));

			var open = dof.SetOutputGate(true);
			Assert.That(open, Is.GreaterThan(closed));
			Assert.That(WaitSettled(dof, open).Written, Is.EqualTo(1));
			Expect(unit.Universe, f => f[0] == 255 && f[1] == 128 && f[2] == 255 && f[3] == 0, "opening restores the current state", baseline);
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
				var mark = _capture.Mark(unit.Universe);
				var later = dof.SetOutputGate(true);
				Assert.That(later, Is.GreaterThan(latched));
				Assert.That(WaitSettled(dof, later).Written, Is.EqualTo(1), "a later opening confirms the closed state");
				dof.Data('S', 2, 1);
				// The session started closed, so every frame it ever sent is all off unless something opened it.
				ExpectFrameAfter(unit.Universe, mark, "a frame after the effect changed");
				var after = FramesBetween(unit.Universe, mark);
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
			var lit = ExpectIndex(good.Universe, f => f[0] == 255, "frame");

			var closed = dof.SetOutputGate(false);
			var state = WaitSettled(dof, closed);
			Assert.That((state.Written, state.Pending, state.Failed), Is.EqualTo((1, 0, 1)));
			Assert.That(dof.GetGatePending(closed), Is.EqualTo(new[] { "ArtNet ArtNetBad" }));
			var blackout = ExpectIndex(good.Universe, f => f.Take(4).All(v => v == 0), "the closing", lit + 1);

			// Open again, so the final all-off can be told from a refresh: S1 shows from then until finish turns it off.
			WaitSettled(dof, dof.SetOutputGate(true));
			var shown = ExpectIndex(good.Universe, f => f[0] == 255, "open again", blackout + 1);

			// The final all-off fails for the bad unit: finish says so, and the good unit still got it.
			var error = Assert.Throws<DofException>(() => dof.Finish());
			Assert.That(error.Message, Does.Contain("ArtNetBad"));
			Assert.That(error.Message, Does.Not.Contain("ArtNetGood"));
			Expect(good.Universe, f => f.Take(4).All(v => v == 0), "the good unit's final all-off", shown + 1);
		}

		[Test]
		public void PortTestGoesThroughTheGate()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, new Dictionary<int, string> { [9] = "Matrix1" }, unit).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			Assert.That(dof.TestOutput(1, 6, 200), Is.EqualTo(DofStatus.Ok));
			var shown = ExpectIndex(unit.Universe, f => f[5] == 200, "frame");

			Assert.That(dof.TestOutput(1, 99, 255), Is.EqualTo(DofStatus.InvalidArgument), "unknown port");
			Assert.That(dof.TestOutput(2, 1, 255), Is.EqualTo(DofStatus.InvalidArgument), "unknown device");
			Assert.That(dof.TestOutput(1, 9, 255), Is.EqualTo(DofStatus.InvalidArgument), "a port naming a toy");

			var closed = dof.SetOutputGate(false);
			WaitSettled(dof, closed);
			// Port 6 shows from the frame above until the closing is written: the first frame after it that doesn't is the
			// closing's, and no port shows after it.
			var closing = ExpectIndex(unit.Universe, f => f[5] == 0, "closed", shown + 1);
			Assert.That(dof.TestOutput(1, 7, 255), Is.EqualTo(DofStatus.Ok));
			ExpectFrameAfter(unit.Universe, closing + 1, "a frame after the port test");
			Assert.That(FramesBetween(unit.Universe, closing).All(f => f[6] == 0 && f[5] == 0), Is.True, "port test is gated");
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
		public void DisposeWaitsForTheCallsInFlight()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			for (var round = 0; round < 5; round++) {
				var dof = DofInstance.Create(config.Root);
				// Init is held in libdof at its first log line on its own thread, while callers keep calling the members that
				// return at once and two threads dispose the instance. libdof's shutdown says goodbye on the thread that runs it.
				var initThread = 0;
				var held = 0;
				using var inside = new ManualResetEventSlim();
				using var release = new ManualResetEventSlim();
				using var disposing = new ManualResetEventSlim();
				var shutdownThreads = new ConcurrentQueue<int>();
				var stop = 0;
				var unexpected = new ConcurrentQueue<Exception>();
				var callerThreads = new ConcurrentDictionary<int, bool>();
				var callers = Enumerable.Range(0, 4).Select(index => new Thread(() => {
					callerThreads[Environment.CurrentManagedThreadId] = true;
					while (Volatile.Read(ref stop) == 0) {
						try {
							switch (index) {
								case 0:
									dof.Data('S', 1, 1);
									dof.Data('S', 1, 0);
									break;
								case 1:
									dof.DataNamed("vpe_named", 1);
									_ = dof.PendingInput;
									break;
								case 2:
									dof.SetOutputGate(true);
									break;
								default:
									if (dof.LatchOutputGate() == -1) {
										disposing.Set();
									}
									break;
							}
						} catch (Exception e) {
							// These members do not throw, disposed or not.
							unexpected.Enqueue(e);
						}
					}
				}) { IsBackground = true }).ToList();
				var init = new Thread(() => {
					Volatile.Write(ref initThread, Environment.CurrentManagedThreadId);
					callerThreads[Environment.CurrentManagedThreadId] = true;
					try {
						dof.Init(config.TableFile, "vpetest");
					} catch (Exception e) {
						unexpected.Enqueue(e);
					}
				}) { IsBackground = true };
				var disposerThreads = new ConcurrentDictionary<int, bool>();
				var entering = Enumerable.Range(0, 2).Select(_ => new ManualResetEventSlim()).ToList();
				var disposers = Enumerable.Range(0, 2).Select(index => new Thread(() => {
					disposerThreads[Environment.CurrentManagedThreadId] = true;
					entering[index].Set();
					dof.Dispose();
				}) { IsBackground = true }).ToList();
				DofLibrary.SetLog((level, message) => {
					_log.Enqueue($"[{level}] {message}");
					var thread = Environment.CurrentManagedThreadId;
					if (message.Contains("Bye and thanks for using!")) {
						shutdownThreads.Enqueue(thread);
					}
					if (thread == Volatile.Read(ref initThread) && Interlocked.Exchange(ref held, 1) == 0) {
						inside.Set();
						release.Wait(30000);
					}
				}, DofLogLevel.Debug);
				var ended = false;
				try {
					callers.ForEach(t => t.Start());
					init.Start();
					Assert.That(inside.Wait(10000), Is.True, "init is held in libdof");
					disposers.ForEach(t => t.Start());
					Assert.That(disposing.Wait(10000), Is.True, "the callers see the dispose");
					// Both disposers are inside Dispose, blocked there (one waits for the calls, the other for the first),
					// while the call is still held.
					for (var i = 0; i < disposers.Count; i++) {
						Assert.That(WaitBlockedInside(disposers[i], entering[i]), Is.True, $"disposer {i} is blocked in Dispose");
					}
					Assert.That(disposers.Any(t => !t.IsAlive), Is.False, "no disposer returns while a call is in flight");
					release.Set();
					Assert.That(init.Join(30000), Is.True, "init returned");
					Assert.That(disposers.All(t => t.Join(30000)), Is.True, "both disposers returned");
				} finally {
					try {
						release.Set();
						Volatile.Write(ref stop, 1);
						ended = Joined(init, 30000) & callers.All(t => Joined(t, 10000));
						// The instance never outlives the test, also when an assertion above failed: a Dispose after the
						// disposers' returns at once, one while they still run waits for them.
						BoundedCleanup(dof);
					} finally {
						entering.ForEach(e => e.Dispose());
					}
				}
				Assert.That(ended, Is.True, "init and the callers ended");
				Assert.That(unexpected, Is.Empty, string.Join("\n", unexpected));
				Assert.That(shutdownThreads.Count, Is.EqualTo(1), "libdof shut down once");
				Assert.That(shutdownThreads.All(disposerThreads.ContainsKey), Is.True, "on a disposing thread");
				Assert.That(shutdownThreads.Any(callerThreads.ContainsKey), Is.False, "never on a caller's");

				// Each member does what it does on a disposed instance.
				Assert.That(dof.SetOutputGate(true), Is.EqualTo(-1), "no gate after dispose");
				Assert.That(dof.LatchOutputGate(), Is.EqualTo(-1), "no latch after dispose");
				Assert.That(dof.PendingInput, Is.Zero, "no input after dispose");
				dof.Data('S', 1, 1);
				dof.DataNamed("vpe_named", 1);
				Assert.Throws<ObjectDisposedException>(() => dof.GetGateState(1));
				Assert.Throws<ObjectDisposedException>(() => dof.Finish());
				dof.Dispose();
				// The instance is gone once Dispose returned: the next one can be created.
				using (DofInstance.Create(config.Root)) {
				}
			}
		}

		[Test]
		public void LogSinkThatDisposesDuringShutdownReturns()
		{
			// Every line written once the instance is disposed calls Dispose again: the lines the destruction writes on its
			// own thread, and those of libdof's threads it stops. None of them waits for the destruction.
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			var dof = Start(config);
			var disposing = 0;
			var calls = 0;
			var unexpected = new ConcurrentQueue<Exception>();
			try {
				dof.Data('S', 1, 1);
				Expect(unit.Universe, f => f[0] == 255, "S1 on");
				DofLibrary.SetLog((level, message) => {
					_log.Enqueue($"[{level}] {message}");
					if (Volatile.Read(ref disposing) == 0) {
						return;
					}
					Interlocked.Increment(ref calls);
					try {
						dof.Dispose();
					} catch (Exception e) {
						unexpected.Enqueue(e);
					}
				}, DofLogLevel.Debug);
				Volatile.Write(ref disposing, 1);
				var disposer = new Thread(() => dof.Dispose()) { IsBackground = true };
				disposer.Start();
				Assert.That(disposer.Join(30000), Is.True, "Dispose returns");
			} finally {
				BoundedCleanup(dof);
			}
			Assert.That(calls, Is.GreaterThan(0), "the shutdown wrote lines");
			Assert.That(unexpected, Is.Empty, string.Join("\n", unexpected));
			Assert.That(dof.SetOutputGate(true), Is.EqualTo(-1), "the instance is disposed");
			using (DofInstance.Create(config.Root)) {
			}
		}

		[Test]
		public void DisposeFromALogLineDuringACallIsRefused()
		{
			// A Dispose from a line of a call on the same thread would wait for that call, and one from a line of libdof's
			// own threads would have them run or wait for their own shutdown: both are refused, and the instance goes on.
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			var dof = DofInstance.Create(config.Root);
			var caller = 0;
			var armed = 1;
			var sameThread = new ConcurrentQueue<Exception>();
			var otherThread = new ConcurrentQueue<Exception>();
			Exception initError = null;
			// Init runs on a thread of its own, so a Dispose that waits for the call it is made from fails the test
			// instead of hanging it.
			var init = new Thread(() => {
				Volatile.Write(ref caller, Environment.CurrentManagedThreadId);
				try {
					dof.Init(config.TableFile, "vpetest");
				} catch (Exception e) {
					initError = e;
				}
			}) { IsBackground = true };
			try {
				DofLibrary.SetLog((level, message) => {
					_log.Enqueue($"[{level}] {message}");
					if (Volatile.Read(ref armed) == 0) {
						return;
					}
					var mine = Environment.CurrentManagedThreadId == Volatile.Read(ref caller);
					var results = mine ? sameThread : otherThread;
					if (!results.IsEmpty) {
						return;
					}
					try {
						dof.Dispose();
						results.Enqueue(null);
					} catch (Exception e) {
						results.Enqueue(e);
					}
				}, DofLogLevel.Debug);
				init.Start();
				Assert.That(init.Join(30000), Is.True, "init returned");
				Assert.That(initError, Is.Null, initError?.ToString());
				var watch = Stopwatch.StartNew();
				while (otherThread.IsEmpty && watch.ElapsedMilliseconds < 5000) {
					Thread.Sleep(5);
				}
				Volatile.Write(ref armed, 0);
				Assert.That(sameThread.TryPeek(out var same), Is.True, "init wrote a line on its thread");
				Assert.That(same, Is.InstanceOf<InvalidOperationException>(), "refused inside the call");
				Assert.That(otherThread.TryPeek(out var other), Is.True, "one of libdof's threads wrote a line");
				Assert.That(other, Is.InstanceOf<InvalidOperationException>(), "refused on libdof's thread");
				dof.Data('S', 1, 1);
				Expect(unit.Universe, f => f[0] == 255, "the instance goes on");
				dof.Finish();
			} finally {
				BoundedCleanup(dof);
			}
		}

		[Test]
		public void InterruptedDisposeStillDestroysTheInstance()
		{
			// The disposer is interrupted, twice, while it waits for a call held in libdof: it keeps waiting, destroys the
			// instance once the call returned, and raises the interrupt after that. The held call's own thread is interrupted
			// too: the call returns as usual, wakes the disposer, and leaves the interrupt for that thread's next wait.
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			var dof = DofInstance.Create(config.Root);
			var initThread = 0;
			var held = 0;
			using var inside = new ManualResetEventSlim();
			using var release = new ManualResetEventSlim();
			using var entering = new ManualResetEventSlim();
			var shutdownThreads = new ConcurrentQueue<int>();
			Exception initError = null;
			Exception disposeError = null;
			var disposerThread = 0;
			bool? initInterruptKept = null;
			var init = new Thread(() => {
				Volatile.Write(ref initThread, Environment.CurrentManagedThreadId);
				try {
					dof.Init(config.TableFile, "vpetest");
				} catch (Exception e) {
					initError = e;
				}
				try {
					Thread.Sleep(5000);
					initInterruptKept = false;
				} catch (ThreadInterruptedException) {
					initInterruptKept = true;
				}
			}) { IsBackground = true };
			var disposer = new Thread(() => {
				disposerThread = Environment.CurrentManagedThreadId;
				entering.Set();
				try {
					dof.Dispose();
				} catch (Exception e) {
					disposeError = e;
				}
			}) { IsBackground = true };
			DofLibrary.SetLog((level, message) => {
				_log.Enqueue($"[{level}] {message}");
				var thread = Environment.CurrentManagedThreadId;
				if (message.Contains("Bye and thanks for using!")) {
					shutdownThreads.Enqueue(thread);
				}
				if (thread == Volatile.Read(ref initThread) && Interlocked.Exchange(ref held, 1) == 0) {
					inside.Set();
					// Spun, not waited: a wait would take the interrupt this thread gets meanwhile.
					var watch = Stopwatch.StartNew();
					while (!release.IsSet && watch.ElapsedMilliseconds < 30000) {
						Thread.SpinWait(100);
					}
				}
			}, DofLogLevel.Debug);
			try {
				init.Start();
				Assert.That(inside.Wait(10000), Is.True, "init is held in libdof");
				disposer.Start();
				Assert.That(WaitBlockedInside(disposer, entering), Is.True, "the disposer waits for init");
				for (var i = 0; i < 2; i++) {
					disposer.Interrupt();
					Assert.That(disposer.Join(200), Is.False, $"the disposer still waits for init after interrupt {i + 1}");
					Assert.That(Blocked(disposer), Is.True, $"and is blocked again after interrupt {i + 1}");
				}
				init.Interrupt();
				release.Set();
				Assert.That(init.Join(30000), Is.True, "init returned");
				Assert.That(disposer.Join(30000), Is.True, "the disposer returned");
			} finally {
				release.Set();
				Joined(init, 30000);
				BoundedCleanup(dof);
			}
			Assert.That(initError, Is.Null, initError?.ToString());
			Assert.That(initInterruptKept, Is.True, "the last call left its thread's interrupt for the next wait");
			Assert.That(disposeError, Is.InstanceOf<ThreadInterruptedException>(), "the interrupt is raised once the instance is gone");
			Assert.That(shutdownThreads, Is.EqualTo(new[] { disposerThread }), "the interrupted disposer destroyed it");
			using (DofInstance.Create(config.Root)) {
			}
		}

		[Test]
		public void HotPathDoesNotAllocate()
		{
			var unit = Unit("ArtNetA");
			using var config = StandardConfig(unit);
			using var dof = Start(config);
			long Measure()
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var i = 0; i < 1000; i++) {
					dof.Data('S', 1, i & 1);
					dof.SetOutputGate(true);
				}
				_ = dof.PendingInput;
				dof.LatchOutputGate();
				return GC.GetAllocatedBytesForCurrentThread() - before;
			}
			Measure();
			Assert.That(Measure(), Is.Zero, "the handle is held for each call without allocating");

			// The last call to return while a Dispose waits signals it, also with an interrupt pending on its thread.
			const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
			var calls = typeof(DofInstance).GetField("_calls", Private);
			var state = typeof(DofInstance).GetField("_state", Private);
			var drained = (ManualResetEvent)typeof(DofInstance).GetField("_drained", Private).GetValue(dof);
			var release = (Action)Delegate.CreateDelegate(typeof(Action), dof, typeof(DofInstance).GetMethod("Release", Private));
			long Signal()
			{
				calls.SetValue(dof, 1);
				drained.Reset();
				var before = GC.GetAllocatedBytesForCurrentThread();
				release();
				return GC.GetAllocatedBytesForCurrentThread() - before;
			}
			state.SetValue(dof, 1);
			try {
				Signal();
				Assert.That(drained.WaitOne(0), Is.True, "the last call signalled the Dispose");
				Thread.CurrentThread.Interrupt();
				var signalled = Signal();
				Assert.That(() => Thread.Sleep(1000), Throws.InstanceOf<ThreadInterruptedException>(), "the interrupt is left for the next wait");
				Assert.That(drained.WaitOne(0), Is.True, "and the Dispose signalled");
				Assert.That(signalled, Is.Zero, "the signal does not allocate");
			} finally {
				try {
					Thread.Sleep(0);
				} catch (ThreadInterruptedException) {
					// Not left to a later test.
				}
				state.SetValue(dof, 0);
				calls.SetValue(dof, 0);
				drained.Reset();
			}
			dof.Finish();
			dof.Dispose();
			Assert.That(Measure(), Is.Zero, "nor once disposed");
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
			var firstCount = _capture.Mark(first.Universe);

			var second = Unit("ArtNetSecond");
			using (var config = new TestConfig().GlobalConfig().Cabinet(8, second).Ini(1, "vpetest,0,S1")) {
				using var dof = Start(config);
				Assert.That(dof.Describe().Controllers.Select(c => c.Name), Is.EqualTo(new[] { "ArtNetSecond" }));
				dof.Data('S', 1, 1);
				Expect(second.Universe, f => f[1] == 255 && f[0] == 0, "frame");
				Thread.Sleep(100);
				dof.Finish();
			}
			Assert.That(_capture.Mark(first.Universe), Is.EqualTo(firstCount), "the first cabinet is gone");
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
			var mark = _capture.Mark(unit.Universe);
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
			// The refresh after the open frame shows the state the unit ended in.
			var mark = _capture.Mark(unit.Universe);
			var shown = ExpectIndex(unit.Universe, f => f[0] == 255 && f[2] == 255, "open at the end", mark);
			var next = ExpectIndex(unit.Universe, f => true, "a frame after it", shown + 1);
			var refresh = _capture.Frames(unit.Universe)[next];
			Assert.That(refresh[0] == 255 && refresh[2] == 255, Is.True, "the unit stays open: " + Show(refresh));
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
			var shown = ExpectIndex(unit.Universe, f => f[0] == 255, "S1 on");
			// Past the minimum effect duration (60 ms), which would hold S1 on for a moment after it goes off.
			Thread.Sleep(100);
			Assert.That(WaitSettled(dof, dof.SetOutputGate(false)).Settled, Is.True);
			// S1 is on from the frame above until the closing is written: the first frame after it with S1 off is the
			// closing's, and S1 must not show after it.
			var closing = ExpectIndex(unit.Universe, f => f[0] == 0, "closed", shown + 1);

			// The opening comes behind a backlog of input that ends with S1 going off: it applies after that input.
			Backlog(dof);
			dof.Data('S', 1, 0);
			var open = dof.SetOutputGate(true);
			Assert.That(WaitSettled(dof, open).Settled, Is.True);
			ExpectFrameAfter(unit.Universe, closing + 1, "a frame after the opening");
			var after = FramesBetween(unit.Universe, closing + 1);
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
			var shown = ExpectIndex(unit.Universe, f => f[0] == 255, "S1 on");
			// Past the minimum effect duration (60 ms), which would hold S1 on for a moment after it goes off.
			Thread.Sleep(100);
			Assert.That(WaitSettled(dof, dof.SetOutputGate(false)).Settled, Is.True);
			// S1 is on from the frame above until the closing is written: the first frame after it with S1 off is the
			// closing's, and S1 must not show after it.
			var closing = ExpectIndex(unit.Universe, f => f[0] == 0, "closed", shown + 1);

			// Finish right after the opening: the input queued before it still goes first. Whether that input is still
			// queued when the opening comes depends on how fast DOF's main thread is, so the backlog makes it likely and
			// the test asserts only what holds either way: finish applies the opening, and S1 is off when it does. The
			// order itself is decided by the queue and tested with the input held in place, natively
			// (FinishRunsTheInputQueuedBeforeAnOpening in native/tests/internal.cpp).
			Backlog(dof);
			dof.Data('S', 1, 0);
			var open = dof.SetOutputGate(true);
			dof.Finish();
			Assert.That(open, Is.GreaterThanOrEqualTo(0), "the opening was accepted");
			var opening = dof.GetGateState(open);
			Assert.That((opening.Applied, opening.Superseded), Is.EqualTo((true, false)), "finish applied the opening: " + opening);
			// Finish returned: what it sent is all there once the capture caught up.
			var after = FramesBetween(unit.Universe, closing + 1, _capture.Mark(unit.Universe));
			Assert.That(after, Is.Not.Empty, "finish sends a final frame");
			Assert.That(after.All(f => f[0] == 0), Is.True, "S1 was off before the gate opened: " + string.Join(",", after.Select(f => f[0])));
		}

		[Test]
		public void ClosingDuringFinishLatchesTheGate()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			Expect(unit.Universe, f => f[0] == 255, "S1 on");

			// Finish is held at its first log line, once it started, while the gate is changed.
			using var finishing = new ManualResetEventSlim();
			using var release = new ManualResetEventSlim();
			DofLibrary.SetLog((level, message) => {
				_log.Enqueue($"[{level}] {message}");
				if (message.Contains("Finishing framework")) {
					finishing.Set();
					release.Wait(5000);
				}
			}, DofLogLevel.Debug);
			Task finish = null;
			try {
				finish = Task.Run(() => dof.Finish());
				Assert.That(finishing.Wait(5000), Is.True, "finish started");
				var opening = dof.SetOutputGate(true);
				var closing = dof.SetOutputGate(false);
				release.Set();
				Assert.That(finish.Wait(10000), Is.True, "finish ended");
				Assert.That(opening, Is.EqualTo(-1), "an opening is refused once finish started");
				Assert.That(closing, Is.GreaterThan(0), "a closing latches the gate");
				var state = dof.GetGateState(closing);
				Assert.That((state.Applied, state.Superseded, state.Written, state.Failed), Is.EqualTo((true, false, 1, 0)), state.ToString());
			} finally {
				release.Set();
				// The hook is taken off before the events go: SetLog returns once no line is in it. Disposing the instance
				// waits for a finish still running, also when an assertion above failed.
				DofLibrary.SetLog((level, message) => _log.Enqueue($"[{level}] {message}"), DofLogLevel.Debug);
			}
		}

		[Test]
		public void OnQueuedAfterAClosingIsNotSent()
		{
			var unit = Unit("ArtNetA");
			using var config = new TestConfig().GlobalConfig().Cabinet(8, unit).Ini(1, "vpetest,S1");
			using var dof = Start(config);
			dof.Data('S', 1, 1);
			var shown = ExpectIndex(unit.Universe, f => f[0] == 255, "S1 on");
			// Rounds with and without input still queued when the closing comes, so the main thread is busy or idle.
			for (var round = 0; round < 6; round++) {
				// S1 is on from the frame shown until it goes off: the first frame after it with S1 off is that change's,
				// and S1 must not show again until the gate opens.
				dof.Data('S', 1, 0);
				var off = ExpectIndex(unit.Universe, f => f[0] == 0, "S1 off", shown + 1);
				if (round % 2 == 1) {
					Backlog(dof);
				}
				var closed = dof.SetOutputGate(false);
				dof.Data('S', 1, 1);
				Assert.That(WaitSettled(dof, closed).Settled, Is.True, "round " + round);
				WaitForInput(dof);
				ExpectFrameAfter(unit.Universe, off + 1, $"round {round}: a frame after the input");
				var after = FramesBetween(unit.Universe, off + 1);
				Assert.That(after.All(f => f[0] == 0), Is.True, $"round {round}: S1 came on after the closing");

				var open = dof.SetOutputGate(true);
				Assert.That(WaitSettled(dof, open).Settled, Is.True, "round " + round);
				shown = ExpectIndex(unit.Universe, f => f[0] == 255, "S1 shows once the gate opens", off + 1);
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
