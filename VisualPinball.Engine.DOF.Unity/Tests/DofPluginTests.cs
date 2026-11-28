// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using VisualPinball.Engine.DOF.Player;
using VisualPinball.Engine.DOF.Session;
using VisualPinball.Plugins;

namespace VisualPinball.Engine.DOF.Unity.Tests
{
	/// <summary>The plugin's game lifecycle, against libdof sending Art-Net to 127.0.0.1 or a fake DOF instance.</summary>
	/// <remarks>
	/// DOF is one per process, so every test frees what it started in <see cref="FreeDof"/>, whether it passed or not:
	/// a test that fails half-way never leaves DOF held for the next one.
	/// </remarks>
	public class DofPluginTests
	{
		private static ArtNetCapture _capture;

		private DofTestHost _host;
		private readonly List<BlockingNative> _natives = new List<BlockingNative>();

		[OneTimeSetUp]
		public void SetUpCapture()
		{
			DofLibrary.EnsureCompatible();
			_capture = new ArtNetCapture();
		}

		[OneTimeTearDown]
		public void TearDownCapture() => _capture?.Dispose();

		[TearDown]
		public void FreeDof()
		{
			lock (_natives) {
				foreach (var native in _natives) {
					native.FinishAllowed.Set();
				}
				_natives.Clear();
			}
			var host = _host;
			_host = null;
			// Stops the plugin and waits until DOF is freed and what it used is released.
			host?.Dispose();
			using (var ownership = DofOwnership.AcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None).Result) {
				Assert.That(ownership, Is.Not.Null, "DOF is still held after the test");
			}
		}

		private DofTestHost Host(Func<string, IDofNative> nativeFactory = null) => _host = new DofTestHost(nativeFactory);

		private BlockingNative Blocking()
		{
			var native = new BlockingNative();
			lock (_natives) {
				_natives.Add(native);
			}
			return native;
		}

		private static void Expect(int universe, Func<byte[], bool> match, string what, int after = 0)
		{
			if (_capture.WaitFor(universe, match, after) == null) {
				var latest = _capture.Latest(universe);
				Assert.Fail($"{what}: no matching frame on universe {universe}; last: {(latest == null ? "none" : string.Join(",", latest.Take(8)))}");
			}
		}

		private static void AssertDofIsFree(string why)
		{
			var ownership = DofOwnership.TryAcquire();
			ownership?.Dispose();
			Assert.That(ownership, Is.Not.Null, why);
		}

		private static void AssertDofIsHeld(string why)
		{
			var ownership = DofOwnership.TryAcquire();
			ownership?.Dispose();
			Assert.That(ownership, Is.Null, why);
		}

		[UnityTest]
		public IEnumerator RunsAGameOverArtNetFromSnapshotToStop()
		{
			const int universe = 320;
			var host = Host();
			host.Import(universe, "vpetest,S1,L2 I#80,W3,E101,$knocker");
			var feed = host.PublishFeed();
			var game = host.Game();

			yield return host.Await(host.Plugin.PrepareGameAsync(game, CancellationToken.None), "prepare");
			Assert.That(host.Triggers, Is.Not.Null, "the cabinet triggers are published while the game prepares");
			Assert.That(feed.HasReceivers, Is.True, "the feed is bound before the game starts, so its snapshot is not missed");

			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");
			Expect(universe, f => true, "a frame");
			Assert.That(_capture.Latest(universe).Take(8), Is.All.EqualTo((byte)0), "dark until the game's state is in");

			// The simulation thread's snapshot, then changes.
			Assert.That(feed.BeginSnapshot(), Is.True);
			feed.PublishSnapshot(RealtimeOutputKind.Solenoid, 1, 255);
			feed.PublishSnapshot(RealtimeOutputKind.Lamp, 2, 0);
			feed.EndSnapshot();
			Expect(universe, f => f[0] == 255, "the snapshot shows");
			feed.Publish(RealtimeOutputKind.Lamp, 2, 1);
			feed.Publish(RealtimeOutputKind.Switch, 3, 1);
			Expect(universe, f => f[0] == 255 && f[1] == 128 && f[2] == 255, "changes show");

			// Table and PUP triggers.
			host.Triggers.SetEvent(101, 1);
			host.Triggers.SetNamed("$knocker", 1);
			Expect(universe, f => f[3] == 255 && f[4] == 255, "events and named elements show");

			host.Plugin.PauseChanged(true);
			var mark = _capture.Count(universe);
			Expect(universe, f => f.Take(8).All(v => v == 0), "pause darkens", mark);
			feed.Publish(RealtimeOutputKind.Solenoid, 1, 0);
			host.Plugin.PauseChanged(false);
			mark = _capture.Count(universe);
			Expect(universe, f => f[0] == 0 && f[1] == 128 && f[2] == 255, "resume shows the current state", mark);

			yield return host.Await(host.Plugin.StopGameAsync(CancellationToken.None), "stop");
			Assert.That(host.Triggers, Is.Null, "the triggers are withdrawn on stop");
			yield return host.Await(host.Plugin.Quiescence, "quiescence");
			Assert.That(_capture.Latest(universe).Take(8), Is.All.EqualTo((byte)0), "stop turns everything off");
			AssertDofIsFree("DOF is free for the next game");
		}

		[UnityTest]
		public IEnumerator TurnsTheCabinetOffWhenTheFeedLostValues()
		{
			const int universe = 321;
			var host = Host();
			host.Import(universe, "vpetest,S1");
			var feed = host.PublishFeed();
			yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");
			Assert.That(feed.BeginSnapshot(), Is.True);
			feed.PublishSnapshot(RealtimeOutputKind.Solenoid, 1, 255);
			feed.EndSnapshot();
			Expect(universe, f => f[0] == 255, "the snapshot shows");

			var mark = _capture.Count(universe);
			feed.Publish(RealtimeOutputKind.Overflow, RealtimeOutputOverflow.Outputs, 5);
			Expect(universe, f => f[0] == 0, "an overflow darkens the cabinet", mark);
			var status = new PluginStatusWriter();
			host.Plugin.DescribeStatus(status);
			Assert.That(status.Lines[0].Value, Does.StartWith("off:"));
			Assert.That(host.Plugin.Quiescence.IsCompleted, Is.False, "DOF stays, dark, until the game ends");

			mark = _capture.Count(universe);
			feed.Publish(RealtimeOutputKind.Solenoid, 1, 0);
			feed.Publish(RealtimeOutputKind.Solenoid, 1, 255);
			Assert.That(_capture.WaitFor(universe, f => f[0] != 0, mark, 500), Is.Null, "and it stays dark");

			yield return host.Await(host.Plugin.StopGameAsync(CancellationToken.None), "stop");
			yield return host.Await(host.Plugin.Quiescence, "quiescence");
			Assert.That(_capture.Latest(universe)[0], Is.EqualTo((byte)0));
			AssertDofIsFree("the stop frees DOF");
		}

		[UnityTest]
		public IEnumerator KeepsTheDevicesUntilDofIsFreed()
		{
			var native = Blocking();
			var host = Host(_ => native);
			host.Import(330, "vpetest,S1", "COM7");
			host.PublishFeed();
			yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
			Assert.That(host.Plugin.LeaseKeys, Is.EqualTo(new[] { "serial:COM7" }));
			Assert.That(host.Leases.Owner("serial:COM7"), Is.EqualTo(DofPluginFactory.Id), "the serial port is reserved before DOF opens it");
			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");

			native.FinishAllowed.Reset();
			var stop = host.Plugin.StopGameAsync(CancellationToken.None);
			Assert.That(native.FinishStarted.Wait(TimeSpan.FromSeconds(5)), Is.True, "DOF finishes");
			for (var i = 0; i < 10; i++) {
				host.Scheduler.Pump();
				yield return null;
			}
			Assert.That(stop.IsCompleted, Is.False, "the stop waits for DOF to finish");
			Assert.That(host.Plugin.Quiescence.IsCompleted, Is.False);
			Assert.That(host.Leases.Owner("serial:COM7"), Is.EqualTo(DofPluginFactory.Id), "the port stays reserved while DOF may still use it");
			AssertDofIsHeld("no other DOF instance can start");

			native.FinishAllowed.Set();
			yield return host.Await(stop, "stop");
			yield return host.Await(host.Plugin.Quiescence, "quiescence");
			Assert.That(native.Disposed, Is.True);
			var calls = native.Calls;
			Assert.That(calls.Skip(calls.Count - 3), Is.EqualTo(new[] { "gate:latched", "finish", "dispose" }), "the stop latches the outputs off before DOF finishes");
			Assert.That(host.Leases.Owner("serial:COM7"), Is.Null, "released once DOF is freed");
			AssertDofIsFree("DOF is free once it finished");
		}

		[UnityTest]
		public IEnumerator EmergencyStopDarkensAndReleasesOnQuiescence()
		{
			var native = Blocking();
			var host = Host(_ => native);
			host.Import(331, "vpetest,S1", "COM8");
			host.PublishFeed();
			yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");

			native.FinishAllowed.Reset();
			host.Plugin.EmergencyStop();
			Assert.That(host.Triggers, Is.Null, "the triggers are withdrawn at once");
			Assert.That(native.FinishStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
			Assert.That(native.Calls.Last(c => c.StartsWith("gate:")), Is.EqualTo("gate:latched"), "the outputs are latched off before DOF finishes");
			Assert.That(host.Leases.Owner("serial:COM8"), Is.EqualTo(DofPluginFactory.Id), "kept until DOF is freed");
			host.Plugin.EmergencyStop();

			native.FinishAllowed.Set();
			yield return host.Await(host.Plugin.Quiescence, "quiescence");
			var calls = native.Calls;
			Assert.That(calls.Last(), Is.EqualTo("dispose"));
			Assert.That(calls.IndexOf("gate:latched"), Is.InRange(0, calls.IndexOf("finish")), "latched before DOF finishes");
			Assert.That(calls.Skip(calls.IndexOf("finish")).Where(c => c.StartsWith("gate:")), Is.All.EqualTo("gate:latched"), "and nothing opens it again");
			Assert.That(host.Leases.Owner("serial:COM8"), Is.Null);
			AssertDofIsFree("DOF is free once it finished");
		}

		[UnityTest]
		public IEnumerator RunsWithoutDofWhenAPortIsTaken()
		{
			var host = Host(_ => Blocking());
			host.Import(332, "vpetest,S1", "COM9");
			using (host.Leases.Acquire("serial:COM9", "org.visualpinball.dmd")) {
				yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
				Assert.That(host.Plugin.Session, Is.Null, "no DOF for this game");
				Assert.That(host.Triggers, Is.Null);
				AssertDofIsFree("nothing is held");
				var status = new PluginStatusWriter();
				host.Plugin.DescribeStatus(status);
				Assert.That(status.Lines[0].Value, Does.Contain("COM9").And.Contain("org.visualpinball.dmd"));
			}
			yield return host.Await(host.Plugin.StopGameAsync(CancellationToken.None), "stop");
		}

		[Test]
		public void TestsAPortOutsideAGame()
		{
			var native = new RecordingTestNative();
			var host = Host();
			host.Factory.TestNativeFactory = _ => native;
			host.Factory.PulseDuration = TimeSpan.FromMilliseconds(20);
			host.Import(333, "vpetest,S1", "COM10", "\"Rom\",\"Flasher\"");
			var map = host.Factory.DescribeOutputsAsync("outputs", new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows"), CancellationToken.None).Result;
			var board = map.Boards.Single();
			Assert.That(board.Detail, Does.EndWith("directoutputconfig.ini"));
			Assert.That(board.Outputs.Count, Is.EqualTo(32), "device 1 stands for a LedWiz");
			Assert.That((board.Outputs[0].Id, board.Outputs[0].Number, board.Outputs[0].Label), Is.EqualTo(("1:1", 1, "Flasher")));
			Assert.That(board.Outputs.Skip(1).All(output => output.Label.Length == 0), Is.True, "the header names nothing else");
			Assert.That(map.TestUnavailableReason, Is.Empty);

			var result = Pulse(host);
			Assert.That(result.Success, Is.True, result.Message);
			Assert.That(native.Calls, Is.EqualTo(new[] { "gate:closed", "connect-tests:off", "init", "gate:closed", "confirmed", "1:1=255", "gate:open", "gate:closed", "1:1=0", "finish", "dispose" }),
				"DOF starts dark without connect tests, every unit confirms it, and only the tested port is let through");
			Assert.That(result.Message, Does.StartWith("Requested a pulse of 0").And.EndWith("02 s on output 1 of device 1: 1 controller acknowledged the gate opening. Delivery to the selected port is unconfirmed."));
			Assert.That(host.Leases.Owner("serial:COM10"), Is.Null, "released after the test");
			AssertDofIsFree("released after the test");

			using (host.Leases.Acquire("serial:COM10", "org.visualpinball.dmd")) {
				result = Pulse(host);
				Assert.That(result.Success, Is.False);
				Assert.That(result.Message, Does.Contain("org.visualpinball.dmd"));
			}
		}

		[Test]
		public void TheOutputMapShowsRgbToysAsTheirChannels()
		{
			var host = Host();
			host.Import(349, "vpetest,S1,0,E2 White", null, "\"Rom\",\"Knocker\",\"\",\"Flasher\"");
			var map = host.Factory.DescribeOutputsAsync("outputs", new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows"), CancellationToken.None).Result;
			var outputs = map.Boards.Single().Outputs;
			Assert.That(outputs.Take(5).Select(o => (o.Number, o.Label, o.Channel)), Is.EqualTo(new[] {
				(1, "Knocker", PluginOutputChannel.None), (2, "", PluginOutputChannel.None),
				(3, "Flasher", PluginOutputChannel.Red), (4, "Flasher", PluginOutputChannel.Green), (5, "Flasher", PluginOutputChannel.Blue),
			}));
			Assert.That(outputs.Select(o => o.Number), Is.EqualTo(Enumerable.Range(1, 32)));
		}

		[Test]
		public void TurnsEveryOutputOffOutsideAGame()
		{
			var native = new RecordingTestNative();
			var host = Host();
			host.Factory.TestNativeFactory = _ => native;
			host.Import(335, "vpetest,S1");
			var result = host.Factory.RunActionAsync("all-off", new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows"), CancellationToken.None).Result;
			Assert.That(result.Success, Is.True, result.Message);
			Assert.That(result.Message, Is.EqualTo("DOF sent \"off\" to every output: 1 controller confirmed sending it."),
				"a controller confirms what it sent, such as an Art-Net packet, not what the device did");
			Assert.That(native.Calls, Is.EqualTo(new[] { "gate:closed", "connect-tests:off", "init", "gate:closed", "confirmed", "finish", "dispose" }), "DOF finishing turns every output off");
			AssertDofIsFree("released after the action");

			// DOF's auto-configuration may open the FT245R board the DMD plugin drives as PinDMD 1.
			native.Calls.Clear();
			host.Import(335, "vpetest,S1", autoConfig: true);
			using (host.Leases.Acquire(DofGameSetup.FtdiFamily, "org.visualpinball.dmd")) {
				result = host.Factory.RunActionAsync("all-off", new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows"), CancellationToken.None).Result;
				Assert.That(result.Success, Is.False);
				Assert.That(result.Message, Does.Contain("FTDI").And.Contain("org.visualpinball.dmd"));
				Assert.That(native.Calls, Is.Empty, "nothing is opened");
			}
		}

		private static PluginActionResult RunAction(DofTestHost host, string action, CancellationToken cancellation = default)
			=> host.Factory.RunActionAsync(action, new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows"), cancellation).Result;

		private static PluginActionResult Pulse(DofTestHost host, string output = "1:1", CancellationToken cancellation = default)
			=> host.Factory.TestOutputAsync("outputs", output, new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows"), cancellation).Result;

		[Test]
		public void OpensTheGateOnlyOnceEveryControllerConfirmedItIsDark()
		{
			// A unit that confirms late, as a controller that is still connecting when DOF started.
			var native = new RecordingTestNative { ConfirmAfter = 5, Units = new[] { "Wemos 1" } };
			var host = Host();
			host.Factory.TestNativeFactory = _ => native;
			host.Factory.PulseDuration = TimeSpan.FromMilliseconds(20);
			host.Import(338, "vpetest,S1");
			var result = Pulse(host);
			Assert.That(result.Success, Is.True, result.Message);
			var calls = native.Calls;
			Assert.That(calls.IndexOf("confirmed"), Is.InRange(calls.IndexOf("init"), calls.IndexOf("1:1=255")));
			Assert.That(calls.IndexOf("gate:open"), Is.GreaterThan(calls.IndexOf("confirmed")), "a unit that confirmed the dark state has finished connecting");
		}

		[Test]
		public void RefusesTheTestWhileAControllerHasNotConfirmedItIsDark()
		{
			var native = new RecordingTestNative { ConfirmAfter = -1, Units = new[] { "Wemos 1", "UMX 1" } };
			var host = Host();
			host.Factory.TestNativeFactory = _ => native;
			host.Factory.ConfirmTimeout = TimeSpan.FromMilliseconds(100);
			host.Import(339, "vpetest,S1");
			var result = Pulse(host);
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Does.Contain("Wemos 1 and UMX 1").And.Contain("nothing was turned on"));
			Assert.That(native.Calls, Has.No.Member("gate:open").And.No.Member("1:1=255"), "the gate never opens");
			Assert.That(native.Calls.Skip(native.Calls.Count - 2), Is.EqualTo(new[] { "finish", "dispose" }));
			AssertDofIsFree("released after the test");
		}

		[Test]
		public void AllOffNamesTheControllersItCouldNotReach()
		{
			// A Teensy that did not connect: its unit failed, so DOF finishing says nothing about its outputs.
			var native = new RecordingTestNative { ConfirmAfter = 0, FailedUnits = new[] { "Teensy" } };
			var host = Host();
			host.Factory.TestNativeFactory = _ => native;
			host.Import(344, "vpetest,S1", "COM12");
			var result = RunAction(host, "all-off");
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Does.Contain("Teensy failed").And.Contain("may still be on"));
			Assert.That(native.Calls, Does.Contain("finish"), "what DOF reaches is still turned off");

			var pending = new RecordingTestNative { ConfirmAfter = -1, Units = new[] { "Pinscape 1" } };
			host.Factory.TestNativeFactory = _ => pending;
			host.Factory.ConfirmTimeout = TimeSpan.FromMilliseconds(100);
			result = RunAction(host, "all-off");
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Does.Contain("Pinscape 1 did not confirm"));
		}

		[Test]
		public void AllOffSucceedsOnlyWhenControllersTookPart()
		{
			// No unit registered, so nothing confirmed that its outputs are off.
			var native = new RecordingTestNative { Units = new string[0] };
			var host = Host();
			host.Factory.TestNativeFactory = _ => native;
			host.Import(346, "vpetest,S1");
			var result = RunAction(host, "all-off");
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Is.EqualTo("No controller took part, so DOF turned nothing off."));

			// A controller of the cabinet DOF skipped registers no unit: only the comparison with the cabinet tells.
			host.Import(347, "vpetest,S1", "COM13");
			var skipped = new RecordingTestNative { Started = new[] { new DofController("ArtNet", "ArtNet 1", 512) } };
			host.Factory.TestNativeFactory = _ => skipped;
			result = RunAction(host, "all-off");
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Is.EqualTo("DOF sent \"off\" to the outputs of the controllers it started: 1 controller confirmed sending it. "
				+ "The cabinet also names Teensy, which DOF did not start: its outputs may still be on."));

			var none = new RecordingTestNative { Units = new string[0], Started = new DofController[0] };
			host.Factory.TestNativeFactory = _ => none;
			result = RunAction(host, "all-off");
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Is.EqualTo("No controller took part, so DOF turned nothing off. "
				+ "The cabinet also names ArtNet 1 and Teensy, which DOF did not start: their outputs may still be on."));
		}

		[Test]
		public void APulseNoControllerSentFails()
		{
			// The tested port's board is mapped but unplugged: its unit failed, and no other unit takes part.
			var unplugged = new RecordingTestNative { Units = new string[0], FailedUnits = new[] { "PacUIO" } };
			var host = Host();
			host.Factory.TestNativeFactory = _ => unplugged;
			host.Factory.PulseDuration = TimeSpan.FromMilliseconds(20);
			host.Import(348, "vpetest,S1");
			var result = Pulse(host);
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Is.EqualTo("No controller can send the pulse: PacUIO failed, so nothing was turned on."));
			Assert.That(unplugged.Calls, Has.No.Member("gate:open"));

			// The units confirmed the dark state but failed to write the opening.
			var failing = new RecordingTestNative { SendsOpening = false };
			host.Factory.TestNativeFactory = _ => failing;
			result = Pulse(host);
			Assert.That(result.Success, Is.False);
			Assert.That(result.Message, Does.StartWith("Requested a pulse of ").And.EndWith(" on output 1 of device 1, but no controller confirmed writing it."));

			// One board wrote the opening while another, maybe the tested port's, failed: the result counts only what
			// was written and names the failed board, without claiming the port got the pulse.
			var mixed = new RecordingTestNative { Units = new[] { "ArtNet 1" }, FailedUnits = new[] { "PacUIO" } };
			host.Factory.TestNativeFactory = _ => mixed;
			result = Pulse(host);
			Assert.That(result.Success, Is.True, result.Message);
			Assert.That(result.Message, Does.StartWith("Requested a pulse of ")
				.And.EndWith(" on output 1 of device 1: 1 controller acknowledged the gate opening. Delivery to the selected port is unconfirmed. PacUIO failed and took no part."));
			Assert.That(result.Message, Does.Not.Contain("Sent").And.Not.Contain("turned on"));
		}

		[Test]
		public void ACancelledTestSaysHowLongTheOutputWasOn()
		{
			var native = new RecordingTestNative();
			var host = Host();
			host.Factory.TestNativeFactory = _ => native;
			host.Factory.PulseDuration = TimeSpan.FromSeconds(10);
			host.Import(345, "vpetest,S1");
			using (var cancellation = new CancellationTokenSource()) {
				var watch = System.Diagnostics.Stopwatch.StartNew();
				var action = Task.Run(() => Pulse(host, "1:1", cancellation.Token));
				while (!native.Calls.Contains("gate:open") && watch.Elapsed < TimeSpan.FromSeconds(5)) {
					Thread.Sleep(5);
				}
				cancellation.Cancel();
				Assert.That(action.Wait(TimeSpan.FromSeconds(5)), Is.True);
				Assert.That(action.Result.Success, Is.False);
				// The gate's time: the controller may not have delivered the opening before the gate closed again.
				Assert.That(action.Result.Message, Does.StartWith("Cancelled after ").And.Contain("the pulse to output 1 of device 1 was requested for 10 s."));
				Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "the pulse ended early");
			}
			Assert.That(native.Calls.Skip(native.Calls.Count - 4), Is.EqualTo(new[] { "gate:closed", "1:1=0", "finish", "dispose" }));
		}

		[Test]
		public void RefusesToTestOutputsBeforeThePlayerSharesItsReservations()
		{
			var native = new RecordingTestNative();
			var host = Host();
			host.Import(336, "vpetest,S1", "COM11");
			// The settings of a player that has not created the plugin yet: no game ran, so it has no device leases.
			var settingsOnly = new DofPluginFactory(host.Factory.Store.Root) { TestNativeFactory = _ => native, PulseDuration = TimeSpan.FromMilliseconds(20) };
			var request = new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows");
			var pulse = settingsOnly.TestOutputAsync("outputs", "1:1", request, CancellationToken.None).Result;
			var allOff = settingsOnly.RunActionAsync("all-off", request, CancellationToken.None).Result;
			Assert.That(settingsOnly.DescribeOutputsAsync("outputs", request, CancellationToken.None).Result.TestUnavailableReason, Is.EqualTo(DofPluginFactory.NoTestsYet));
			Assert.That((pulse.Success, pulse.Message), Is.EqualTo((false, DofPortTest.NoLeases)));
			Assert.That((allOff.Success, allOff.Message), Is.EqualTo((false, DofPortTest.NoLeases)));
			Assert.That(native.Calls, Is.Empty, "no device is opened without a reservation");
			AssertDofIsFree("nothing is held");
		}

		[Test]
		public void TestsOutputsBeforeAGameWithTheReservationsTheSettingsPageShares()
		{
			var native = new RecordingTestNative();
			var host = Host();
			host.Import(340, "vpetest,S1", "COM11");
			// The settings of a player that has not created the plugin yet, but passes its capabilities to the page.
			var settingsOnly = new DofPluginFactory(host.Factory.Store.Root) { TestNativeFactory = _ => native, PulseDuration = TimeSpan.FromMilliseconds(20) };
			var request = new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows") { Capabilities = host.Capabilities };
			Assert.That(settingsOnly.DescribeOutputsAsync("outputs", request, CancellationToken.None).Result.TestUnavailableReason, Is.Empty);
			// a host that shares its capabilities but no reservations: nothing to test with, whatever an earlier request brought
			var without = new PluginChoiceRequest(null, new PluginSettingsSnapshot(1), "windows") { Capabilities = new PluginCapabilities() };
			Assert.That(settingsOnly.DescribeOutputsAsync("outputs", without, CancellationToken.None).Result.TestUnavailableReason, Is.EqualTo(DofPluginFactory.NoTestsYet));
			Assert.That(settingsOnly.TestOutputAsync("outputs", "1:1", without, CancellationToken.None).Result.Message, Is.EqualTo(DofPortTest.NoLeases));
			using (host.Leases.Acquire("serial:COM11", "org.visualpinball.other")) {
				var held = settingsOnly.TestOutputAsync("outputs", "1:1", request, CancellationToken.None).Result;
				Assert.That(held.Success, Is.False, "the page's test respects what a game holds");
				Assert.That(native.Calls, Is.Empty);
			}
			var pulse = settingsOnly.TestOutputAsync("outputs", "1:1", request, CancellationToken.None).Result;
			Assert.That(pulse.Success, Is.True, pulse.Message);
			Assert.That(native.Calls, Does.Contain("1:1=255"));
			AssertDofIsFree("released after the test");
		}

		[UnityTest]
		public IEnumerator TurnsTheCabinetOffWhenTheFeedIsWithdrawn()
		{
			const int universe = 322;
			var host = Host();
			host.Import(universe, "vpetest,S1");
			var feed = host.PublishFeed();
			yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");
			Assert.That(host.Plugin.FollowsFeed, Is.True);
			Assert.That(feed.BeginSnapshot(), Is.True);
			feed.PublishSnapshot(RealtimeOutputKind.Solenoid, 1, 255);
			feed.EndSnapshot();
			Expect(universe, f => f[0] == 255, "the snapshot shows");

			// The producer goes away with the solenoid on: DOF would show it on for the rest of the game.
			var mark = _capture.Count(universe);
			host.WithdrawFeed();
			Expect(universe, f => f[0] == 0, "withdrawing the feed darkens the cabinet", mark);
			var status = new PluginStatusWriter();
			host.Plugin.DescribeStatus(status);
			Assert.That(status.Lines[0].Value, Does.StartWith("off:"));
			Assert.That(host.Plugin.Quiescence.IsCompleted, Is.False, "DOF stays, dark, until the game ends");

			yield return host.Await(host.Plugin.StopGameAsync(CancellationToken.None), "stop");
			yield return host.Await(host.Plugin.Quiescence, "quiescence");
			Assert.That(_capture.Latest(universe)[0], Is.EqualTo((byte)0));
			AssertDofIsFree("the stop frees DOF");
		}

		[UnityTest]
		public IEnumerator IgnoresAnAbandonedSnapshotAttempt()
		{
			const int universe = 323;
			var host = Host();
			// S1 starts a two-second effect: shown from an abandoned attempt, it would outlive the attempt.
			host.Import(universe, "vpetest,S1 2000,S2");
			var feed = host.PublishFeed();
			yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");
			Expect(universe, f => true, "a frame");
			var mark = _capture.Count(universe);

			Assert.That(feed.BeginSnapshot(), Is.True);
			feed.PublishSnapshot(RealtimeOutputKind.Solenoid, 1, 1);
			feed.AbortSnapshot();
			Assert.That(feed.BeginSnapshot(), Is.True, "the next attempt");
			feed.PublishSnapshot(RealtimeOutputKind.Solenoid, 1, 0);
			feed.PublishSnapshot(RealtimeOutputKind.Solenoid, 2, 0);
			feed.EndSnapshot();
			feed.Publish(RealtimeOutputKind.Solenoid, 2, 1);
			Expect(universe, f => f[1] == 255, "changes after the snapshot show", mark);
			Assert.That(_capture.WaitFor(universe, f => f[0] != 0, mark, 1000), Is.Null, "the abandoned attempt never reached DOF");

			yield return host.Await(host.Plugin.StopGameAsync(CancellationToken.None), "stop");
			yield return host.Await(host.Plugin.Quiescence, "quiescence");
		}

		[UnityTest]
		public IEnumerator FollowsOnlyTheTriggersOfAFeedItCannotRead()
		{
			var native = Blocking();
			var host = Host(_ => native);
			host.Import(337, "vpetest,S1,E101");
			var feed = host.PublishFeed("other");
			yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
			Assert.That(host.Plugin.FollowsFeed, Is.False);
			Assert.That(feed.HasReceivers, Is.False, "not subscribed to numbers DOF would misread");
			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");

			// No snapshot ever comes, so DOF must not wait for one.
			host.Triggers.SetEvent(101, 1);
			var watch = System.Diagnostics.Stopwatch.StartNew();
			while (!(native.Calls.Contains("gate:open") && native.Calls.Contains("E101=1")) && watch.Elapsed < TimeSpan.FromSeconds(5)) {
				host.Scheduler.Pump();
				yield return null;
			}
			Assert.That(native.Calls, Does.Contain("gate:open").And.Contain("E101=1"), "the outputs open for the table's triggers");

			yield return host.Await(host.Plugin.StopGameAsync(CancellationToken.None), "stop");
			yield return host.Await(host.Plugin.Quiescence, "quiescence");
		}

		[Test]
		public void ListsTheRomsOfTheConfiguration()
		{
			var host = Host();
			host.Import(334, "vpetest,S1");
			var catalog = host.Factory.DiscoverAsync("roms", new PluginChoiceRequest(new GameIdentity("pinmame", "vpetest_l1"), new PluginSettingsSnapshot(1), "windows"), CancellationToken.None).Result;
			Assert.That(catalog.Choices.Select(c => c.Id), Is.EqualTo(new[] { "auto", "vpetest" }));
			Assert.That(catalog.Choices[0].Description, Does.Contain("vpetest_l1 has rows on 1"));
		}

		[UnityTest]
		public IEnumerator RunsWithoutDofWithoutAConfiguration()
		{
			var host = Host(_ => Blocking());
			yield return host.Await(host.Plugin.PrepareGameAsync(host.Game(), CancellationToken.None), "prepare");
			Assert.That(host.Plugin.Session, Is.Null);
			yield return host.Await(host.Plugin.StartGameAsync(CancellationToken.None), "start");
			yield return host.Await(host.Plugin.StopGameAsync(CancellationToken.None), "stop");
			AssertDofIsFree("nothing is held");
		}
	}

	/// <summary>A port test's DOF instance that records its calls, with controller units that confirm the gate as told.</summary>
	internal sealed class RecordingTestNative : IDofTestNative
	{
		public readonly List<string> Calls = new List<string>();
		private long _generation;
		private int _polls;

		/// <summary>How many times the state of a closing after init is asked before its units have written it; -1 for never.</summary>
		public int ConfirmAfter;
		/// <summary>The units, by name, that fail instead of confirming.</summary>
		public string[] FailedUnits = new string[0];
		/// <summary>The units, by name, that confirm (or not, see <see cref="ConfirmAfter"/>).</summary>
		public string[] Units = { "ArtNet 1" };
		/// <summary>The controllers DOF started: those of the test cabinet unless set.</summary>
		public DofController[] Started = { new DofController("ArtNet", "ArtNet 1", 512), new DofController("TeensyStripController", "Teensy", 8) };
		/// <summary>Whether the units write an opening of the gate; if not, they fail it.</summary>
		public bool SendsOpening = true;
		private readonly HashSet<long> _openings = new HashSet<long>();

		public void SetConnectTests(bool allowed) => Calls.Add(allowed ? "connect-tests:on" : "connect-tests:off");
		public void Init(string tableFile, string romName) => Calls.Add("init");
		public IReadOnlyList<DofController> Controllers() => Started;
		public long SetOutputGate(bool open)
		{
			Calls.Add(open ? "gate:open" : "gate:closed");
			_polls = 0;
			if (open) {
				_openings.Add(_generation + 1);
			}
			return ++_generation;
		}
		public DofGateState GetGateState(long generation)
		{
			if (_openings.Contains(generation)) {
				return SendsOpening
					? new DofGateState(true, Units.Length, 0, FailedUnits.Length, true)
					: new DofGateState(true, 0, 0, Units.Length + FailedUnits.Length, true);
			}
			var confirmed = ConfirmAfter >= 0 && _polls++ >= ConfirmAfter;
			if (confirmed && _polls == ConfirmAfter + 1) {
				Calls.Add("confirmed");
			}
			return new DofGateState(true, confirmed ? Units.Length : 0, confirmed ? 0 : Units.Length, FailedUnits.Length);
		}
		public IReadOnlyList<string> GetGatePending(long generation)
			=> (ConfirmAfter >= 0 && _polls > ConfirmAfter ? new string[0] : Units).Concat(FailedUnits).ToList();
		public DofStatus TestOutput(int device, int port, int value)
		{
			Calls.Add($"{device}:{port}={value}");
			return DofStatus.Ok;
		}
		public void Finish() => Calls.Add("finish");
		public void Dispose() => Calls.Add("dispose");
	}
}
