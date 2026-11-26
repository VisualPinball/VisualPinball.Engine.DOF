// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VisualPinball.Engine.DOF.Session;

namespace VisualPinball.Engine.DOF.Test
{
	/// <summary>The session worker against a fake DOF instance: ordering, the snapshot, the gate, overflow and deadlines.</summary>
	[TestFixture]
	public class DofSessionTests
	{
		private const int Game = 7;
		private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

		private static (DofSession session, FakeNative native) Create(Action<DofSessionOptions> configure = null, FakeNative native = null)
		{
			native = native ?? new FakeNative();
			var fake = native;
			var options = new DofSessionOptions {
				BasePath = "unused",
				TableFile = "unused.vpx",
				RomName = "vpetest",
				GameSession = Game,
				WaitForSnapshot = false,
				NativeFactory = _ => fake,
			};
			configure?.Invoke(options);
			return (new DofSession(options), native);
		}

		private static T Wait<T>(Task<T> task)
		{
			Assert.That(task.Wait(Timeout), Is.True, "timed out");
			return task.Result;
		}

		private static void WaitUntil(Func<bool> condition, string what)
		{
			var watch = Stopwatch.StartNew();
			while (!condition()) {
				if (watch.Elapsed > Timeout) {
					Assert.Fail("timed out waiting for " + what);
				}
				Thread.Sleep(2);
			}
		}

		[Test]
		public void DeliversChangesInOrder()
		{
			var (session, native) = Create();
			Assert.That(Wait(session.StartAsync()).Started, Is.True);
			foreach (var value in new[] { 0, 255, 128, 0 }) {
				Assert.That(session.Publish(Game, DofElementKind.Solenoid, 1, value), Is.True);
			}
			WaitUntil(() => native.Changes.Count == 4, "four changes");
			Assert.That(native.Changes, Is.EqualTo(new[] { "S1=0", "S1=255", "S1=128", "S1=0" }));
			Wait(session.StopAsync());
		}

		[Test]
		public void DropsRepeatedValuesAndNormalisesSwitches()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			session.Publish(Game, DofElementKind.Lamp, 3, 1);
			session.Publish(Game, DofElementKind.Lamp, 3, 1);
			session.Publish(Game, DofElementKind.Switch, 9, 5);
			session.Publish(Game, DofElementKind.Switch, 9, 1);
			session.Publish(Game, DofElementKind.Mech, 5000, 12);
			session.Publish(Game, DofElementKind.Mech, 5000, 12);
			session.PublishEvent(101, 1);
			session.PublishEvent(101, 1);
			WaitUntil(() => native.Changes.Count == 4, "four changes");
			Thread.Sleep(50);
			Assert.That(native.Changes, Is.EquivalentTo(new[] { "L3=1", "W9=1", "N5000=12", "E101=1" }));
			Assert.That(session.Diagnostics.Unchanged, Is.EqualTo(4));
			Wait(session.StopAsync());
		}

		[Test]
		public void NamedElementsReachDofUnfiltered()
		{
			// DOF compares names ignoring case and drops repeated values itself; a filter of the session that told the
			// spellings apart dropped the last off here.
			var (session, native) = Create();
			Wait(session.StartAsync());
			session.PublishNamed("Knocker", 0);
			session.PublishNamed("KNOCKER", 1);
			session.PublishNamed("Knocker", 0);
			WaitUntil(() => native.Changes.Count == 3, "three changes");
			Assert.That(native.Changes, Is.EqualTo(new[] { "$Knocker=0", "$KNOCKER=1", "$Knocker=0" }));
			Wait(session.StopAsync());
		}

		[Test]
		public void InitialSnapshotIsBufferedAndGatesTheOpening()
		{
			var (session, native) = Create(o => o.WaitForSnapshot = true);
			// The game publishes its state before DOF runs.
			session.Publish(Game, DofElementKind.Lamp, 1, 1);
			session.Publish(Game, DofElementKind.Solenoid, 2, 255);
			Wait(session.StartAsync());
			WaitUntil(() => native.Changes.Count == 2, "the snapshot");
			Thread.Sleep(50);
			Assert.That(native.GateOpen, Is.False, "the gate waits for the snapshot boundary");

			session.PublishSnapshotBoundary(Game);
			WaitUntil(() => native.GateOpen, "the gate to open");
			var calls = native.Calls;
			Assert.That(calls.IndexOf("gate:closed"), Is.LessThan(calls.IndexOf("init")), "dark before init");
			Assert.That(calls.IndexOf("L1=1"), Is.GreaterThan(calls.IndexOf("init")));
			Assert.That(calls.LastIndexOf("gate:open"), Is.GreaterThan(calls.IndexOf("S2=255")), "the snapshot is in before the gate opens");
			Wait(session.StopAsync());
		}

		[Test]
		public void GateStaysClosedUntilTheSnapshotArrives()
		{
			var (session, native) = Create(o => {
				o.WaitForSnapshot = true;
				o.SnapshotTimeout = TimeSpan.FromMilliseconds(100);
			});
			Wait(session.StartAsync());
			WaitUntil(() => session.Diagnostics.SnapshotOverdue, "the missing snapshot to be reported");
			Thread.Sleep(50);
			Assert.That(native.GateOpen, Is.False, "no partial state shows");
			Assert.That(native.Calls, Has.No.Member("gate:open"));

			session.PublishSnapshotBoundary(Game);
			WaitUntil(() => native.GateOpen, "the gate to open");
			Assert.That(session.Diagnostics.SnapshotOverdue, Is.False);
			Wait(session.StopAsync());
		}

		[Test]
		public void GateOpensOnlyWhenDofProcessedTheSnapshot()
		{
			var (session, native) = Create(o => {
				o.WaitForSnapshot = true;
				o.GateTimeout = TimeSpan.FromMilliseconds(100);
			});
			native.HoldInput = true;
			session.Publish(Game, DofElementKind.Lamp, 1, 1);
			session.PublishSnapshotBoundary(Game);
			Wait(session.StartAsync());
			WaitUntil(() => native.Changes.Count == 1, "the snapshot");
			WaitUntil(() => session.Diagnostics.DrainOverdue, "the backlog to be reported");
			Assert.That(native.GateOpen, Is.False, "libdof runs the opening before its queued input: wait until it took it");

			native.Process();
			WaitUntil(() => native.GateOpen, "the gate to open");
			Assert.That(session.Diagnostics.DrainOverdue, Is.False);
			Wait(session.StopAsync());
		}

		[Test]
		public void PauseClosesAndResumeDeliversFirst()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");

			var paused = Wait(session.PauseAsync());
			Assert.That((paused.Open, paused.Settled), Is.EqualTo((false, true)));
			Assert.That(session.State, Is.EqualTo(DofSessionState.Paused));
			Assert.That(native.GateOpen, Is.False);

			session.Publish(Game, DofElementKind.Solenoid, 4, 255);
			var resumed = Wait(session.ResumeAsync());
			Assert.That((resumed.Open, resumed.Settled), Is.EqualTo((true, true)));
			var calls = native.Calls;
			Assert.That(calls.IndexOf("S4=255"), Is.LessThan(calls.LastIndexOf("gate:open")), "events of the pause reach DOF before the gate opens");
			Assert.That(session.State, Is.EqualTo(DofSessionState.Running));
			Wait(session.StopAsync());
		}

		[Test]
		public void ResumeHoldsTheGateUntilDofProcessedThePause()
		{
			var (session, native) = Create(o => o.GateTimeout = TimeSpan.FromMilliseconds(100));
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			Wait(session.PauseAsync());

			native.HoldInput = true;
			session.Publish(Game, DofElementKind.Solenoid, 4, 255);
			var resumed = Wait(session.ResumeAsync());
			Assert.That(resumed.Open, Is.False, resumed.ToString());
			Assert.That(resumed.Reason, Does.Contain("not processed"));
			Assert.That(session.Diagnostics.DrainOverdue, Is.True);
			Thread.Sleep(50);
			Assert.That(native.GateOpen, Is.False, "stale outputs never show");

			native.Process();
			WaitUntil(() => native.GateOpen, "the gate to open once DOF caught up");
			Wait(session.StopAsync());
		}

		[Test]
		public void EmergencyStopDuringResumeKeepsTheGateClosed()
		{
			var (session, native) = Create(o => o.GateTimeout = TimeSpan.FromSeconds(2));
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			Wait(session.PauseAsync());
			var paused = native.Calls.Count;

			native.HoldInput = true;
			session.Publish(Game, DofElementKind.Solenoid, 4, 255);
			var resume = session.ResumeAsync();
			// The resume delivered the pause's events and now waits for DOF to take them.
			WaitUntil(() => native.Changes.Contains("S4=255"), "the resume to deliver");
			Thread.Sleep(20);
			Task.Run(() => session.EmergencyStop()).Wait(Timeout);
			native.Process();

			Assert.That(Wait(resume).Open, Is.False);
			Wait(session.Completion);
			Assert.That(native.Calls.Skip(paused), Has.No.Member("gate:open"), "nothing opens after an emergency stop");
			Assert.That(native.GateOpen, Is.False);
		}

		[Test]
		public void OverflowDuringAGateWaitFaultsAtOnce()
		{
			var (session, native) = Create(o => {
				o.RingCapacity = 16;
				o.GateTimeout = TimeSpan.FromSeconds(3);
			});
			// The opening never settles, so the worker waits for it while the ring fills.
			native.UnitWrites = false;
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the opening");
			var watch = Stopwatch.StartNew();
			for (var i = 0; i < 1_000_000 && session.Publish(Game, DofElementKind.Lamp, i & 63, i & 1); i++) {
			}
			WaitUntil(() => session.State == DofSessionState.Faulted && !native.GateOpen, "the fault");
			Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1500), "the overflow ends the gate wait");
			Assert.That(session.Diagnostics.Fault, Does.Contain("overflowed"));
			native.UnitWrites = true;
			Wait(session.StopAsync());
			Assert.That(native.Calls.Skip(native.Calls.IndexOf("gate:latched") + 1), Has.No.Member("gate:open"));
		}

		[Test]
		public void BacklogDuringAGateWaitFaultsAtOnce()
		{
			var (session, native) = Create(o => {
				o.BacklogLimit = 10;
				o.GateTimeout = TimeSpan.FromSeconds(3);
			});
			// The opening never settles: the worker forwards events while it waits, and must check DOF's backlog there.
			native.UnitWrites = false;
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the opening");
			var watch = Stopwatch.StartNew();
			native.Pending = 11;
			session.Publish(Game, DofElementKind.Lamp, 1, 1);
			WaitUntil(() => session.State == DofSessionState.Faulted, "the fault");
			Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1500), "found between batches, not after the wait");
			Assert.That(session.Diagnostics.Fault, Does.Contain("cannot keep up"));
			Assert.That(native.Latched && !native.GateOpen, Is.True);
			native.Pending = 0;
			native.UnitWrites = true;
			Wait(session.StopAsync());
		}

		[Test]
		public void BacklogOnThePassTheGateSettlesFaults()
		{
			var (session, native) = Create(o => {
				o.BacklogLimit = 10;
				o.GateTimeout = TimeSpan.FromSeconds(3);
			});
			native.UnitWrites = false;
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the opening");
			// The events the opening's wait forwards take DOF over the limit, and the unit settles at that moment: DOF
			// catches up when the state is asked, so the backlog is only seen if it is checked before the gate's state.
			native.SettleAt = 11;
			native.CatchUpOnSettle = true;
			native.HoldInput = true;
			for (var i = 0; i < 11; i++) {
				session.Publish(Game, DofElementKind.Lamp, i, 1);
			}
			WaitUntil(() => session.State == DofSessionState.Faulted, "the fault");
			Assert.That(session.Diagnostics.Fault, Does.Contain("cannot keep up"));
			Assert.That(native.Latched && !native.GateOpen, Is.True);
			Wait(session.StopAsync());
		}

		[Test]
		public void BacklogOverTheLimitWhenAWaitStartsFaults()
		{
			var (session, native) = Create(o => {
				o.BacklogLimit = 10;
				o.GateTimeout = TimeSpan.FromSeconds(3);
			});
			// The worker waits for the opening with the ring empty; DOF's queue is already too long.
			native.UnitWrites = false;
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the opening");
			var watch = Stopwatch.StartNew();
			native.Pending = 11;
			WaitUntil(() => session.State == DofSessionState.Faulted, "the fault");
			Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1500), "found without a new batch");
			native.Pending = 0;
			native.UnitWrites = true;
			Wait(session.StopAsync());
		}

		[Test]
		public void PauseAndResumeOfAFaultedSessionReturnAtOnce()
		{
			var (session, native) = Create(o => o.GateTimeout = TimeSpan.FromSeconds(3));
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			// The fault's own all-off waits for the gate timeout; a pause or resume must not wait behind it.
			native.UnitWrites = false;
			session.Fault("The real-time feed overflowed.");
			var pause = session.PauseAsync();
			var resume = session.ResumeAsync();
			Assert.That(pause.IsCompleted && resume.IsCompleted, Is.True, "refused at admission");
			Assert.That(pause.Result.Reason, Does.Contain("faulted"));
			Assert.That(resume.Result.Open, Is.False);
			native.UnitWrites = true;
			Wait(session.StopAsync());
		}

		[Test]
		public void BacklogWhileResumeWaitsForDofFaults()
		{
			var (session, native) = Create(o => {
				o.BacklogLimit = 10;
				o.GateTimeout = TimeSpan.FromSeconds(3);
			});
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			Wait(session.PauseAsync());
			native.HoldInput = true;
			session.Publish(Game, DofElementKind.Solenoid, 1, 1);
			var resume = session.ResumeAsync();
			WaitUntil(() => native.Changes.Contains("S1=1"), "the resume to wait for DOF");
			var watch = Stopwatch.StartNew();
			for (var i = 0; i < 20; i++) {
				session.Publish(Game, DofElementKind.Lamp, i, 1);
			}
			var resumed = Wait(resume);
			Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1500), "the backlog ends the wait");
			Assert.That(resumed.Open, Is.False);
			Assert.That(resumed.Reason, Does.Contain("cannot keep up"));
			native.Process();
			Wait(session.StopAsync());
			Assert.That(native.Calls.Skip(native.Calls.IndexOf("gate:latched") + 1), Has.No.Member("gate:open"));
		}

		[Test]
		public void ACommandThatFailsStillCompletes()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			native.GateError = true;
			var paused = Wait(session.PauseAsync());
			Assert.That(paused.Reason, Does.Contain("the gate failed"));
			Assert.That(Wait(session.Completion).Error, Does.Contain("the gate failed"));
			Assert.That(native.Disposed, Is.True);
		}

		[Test]
		public void CommandsRacingAStopComplete()
		{
			for (var round = 0; round < 50; round++) {
				var (session, _) = Create();
				Wait(session.StartAsync());
				var go = new ManualResetEventSlim(false);
				var pause = Task.Run(() => { go.Wait(); return session.PauseAsync(); });
				var resume = Task.Run(() => { go.Wait(); return session.ResumeAsync(); });
				var stop = Task.Run(() => { go.Wait(); return session.StopAsync(); });
				go.Set();
				Assert.That(Task.WaitAll(new Task[] { pause, resume, stop }, Timeout), Is.True, $"round {round} hung");
			}
		}

		[Test]
		public void StopClosesTheGateThenFinishes()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			var stopped = Wait(session.StopAsync());
			Assert.That(stopped.Completed, Is.True);
			Assert.That((stopped.Gate.Open, stopped.Gate.Settled), Is.EqualTo((false, true)));
			Assert.That(stopped.Error, Is.Null);
			var calls = native.Calls;
			Assert.That(calls.Skip(calls.Count - 3), Is.EqualTo(new[] { "gate:latched", "finish", "dispose" }), "a stop latches the gate closed");
			Assert.That(session.State, Is.EqualTo(DofSessionState.Stopped));
			Assert.That(session.Publish(Game, DofElementKind.Lamp, 1, 1), Is.False, "no events after stop");
		}

		[Test]
		public void UnconfirmedAllOffIsReported()
		{
			var (session, native) = Create(o => o.GateTimeout = TimeSpan.FromMilliseconds(50));
			Wait(session.StartAsync());
			native.UnitWrites = false;
			var paused = Wait(session.PauseAsync());
			Assert.That(paused.Settled, Is.False);
			Assert.That(paused.Unconfirmed, Is.EqualTo(new[] { "Fake 1" }));
			Wait(session.StopAsync());
		}

		[Test]
		public void EventsOfAnotherGameSessionAreRejected()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			Assert.That(session.Publish(Game + 1, DofElementKind.Lamp, 1, 1), Is.False);
			session.Publish(Game, DofElementKind.Lamp, 2, 1);
			WaitUntil(() => native.Changes.Count == 1, "the change");
			Assert.That(native.Changes, Is.EqualTo(new[] { "L2=1" }));
			Assert.That(session.Diagnostics.Rejected, Is.EqualTo(1));
			Wait(session.StopAsync());
		}

		[Test]
		public void FullRingFaultsTheSessionAndClosesTheGate()
		{
			var (session, native) = Create(o => o.RingCapacity = 16);
			// Before the start nothing drains the ring.
			var accepted = 0;
			for (var i = 0; i < 40; i++) {
				if (session.Publish(Game, DofElementKind.Lamp, i, 1)) {
					accepted++;
				}
			}
			Assert.That(accepted, Is.LessThanOrEqualTo(16));
			Assert.That(session.Publish(Game, DofElementKind.Lamp, 99, 1), Is.False, "rejected after the overflow");
			Wait(session.StartAsync());
			WaitUntil(() => session.State == DofSessionState.Faulted, "the fault");
			Assert.That(session.Diagnostics.Fault, Does.Contain("overflowed"));
			Assert.That(native.GateOpen, Is.False);
			Thread.Sleep(50);
			Assert.That(native.GateOpen, Is.False, "a faulted session stays dark");
			Assert.That(Wait(session.ResumeAsync()).Open, Is.False, "resume keeps a faulted session closed");
			Assert.That(Wait(session.StopAsync()).Completed, Is.True);
		}

		[Test]
		public void BacklogBeyondTheLimitFaults()
		{
			var (session, native) = Create(o => o.BacklogLimit = 10);
			Wait(session.StartAsync());
			native.Pending = 11;
			WaitUntil(() => session.State == DofSessionState.Faulted, "the fault");
			Assert.That(session.Diagnostics.Fault, Does.Contain("cannot keep up"));
			native.Pending = 0;
			Wait(session.StopAsync());
		}

		[Test]
		public void FailedInitCompletesAndFreesTheInstance()
		{
			var (session, native) = Create(native: new FakeNative { InitError = "no cabinet" });
			var started = Wait(session.StartAsync());
			Assert.That(started.Started, Is.False);
			Assert.That(started.Error, Does.Contain("no cabinet"));
			var completion = Wait(session.Completion);
			Assert.That(completion.Completed, Is.True);
			Assert.That(native.Disposed, Is.True);
			Assert.That(session.State, Is.EqualTo(DofSessionState.Stopped));
		}

		[Test]
		public void StopBeforeStartCompletesAtOnce()
		{
			var (session, native) = Create();
			var stopped = Wait(session.StopAsync());
			Assert.That(stopped.Completed, Is.True);
			Assert.That(native.Calls, Is.Empty);
			Assert.Throws<InvalidOperationException>(() => session.StartAsync(), "a stopped session cannot start");
		}

		[Test]
		public void SlowFinishIsReportedAndTheStopWaitsForIt()
		{
			var native = new FakeNative { FinishDelay = TimeSpan.FromMilliseconds(800) };
			var (session, _) = Create(o => {
				o.GateTimeout = TimeSpan.FromMilliseconds(50);
				o.FinishTimeout = TimeSpan.FromMilliseconds(100);
			}, native);
			Wait(session.StartAsync());
			var overdue = new ManualResetEventSlim(false);
			session.StopOverdue += (sender, _) => overdue.Set();
			var watch = Stopwatch.StartNew();
			var stop = session.StopAsync();
			Assert.That(overdue.Wait(Timeout), Is.True, "the deadline is reported");
			Assert.That(watch.ElapsedMilliseconds, Is.LessThan(600), "at the deadline");
			Assert.That(session.IsStopOverdue && session.Diagnostics.StopOverdue, Is.True);
			// The owner keeps the game scope (leases, configuration) while DOF still runs.
			Assert.That(stop.IsCompleted, Is.False, "the stop stays pending while DOF finishes");
			Assert.That(native.Disposed, Is.False);

			var stopped = Wait(stop);
			Assert.That(watch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(700));
			Assert.That((stopped.Completed, stopped.Overdue), Is.EqualTo((true, true)));
			Assert.That(native.Disposed, Is.True);
			Assert.That(session.Completion, Is.SameAs(stop));
		}

		[Test]
		public void StopThatEndsLateIsReportedOnce()
		{
			// DOF finishes after the deadline but before its timer runs (turned off here): the worker reports it.
			var native = new FakeNative { FinishDelay = TimeSpan.FromMilliseconds(400) };
			var (session, _) = Create(o => {
				o.GateTimeout = TimeSpan.FromMilliseconds(50);
				o.FinishTimeout = TimeSpan.FromMilliseconds(100);
				o.StopDeadlineTimer = false;
			}, native);
			var raised = 0;
			var overdue = new ManualResetEventSlim(false);
			session.StopOverdue += (sender, _) => {
				Interlocked.Increment(ref raised);
				overdue.Set();
			};
			Wait(session.StartAsync());
			var stopped = Wait(session.StopAsync());
			Assert.That(stopped.Overdue, Is.True);
			Assert.That(overdue.Wait(Timeout), Is.True, "reported although the timer never ran");
			Thread.Sleep(100);
			Assert.That(raised, Is.EqualTo(1));
			Assert.That(session.IsStopOverdue, Is.True);
		}

		[Test]
		public void TimelyStopIsNotOverdue()
		{
			var (session, _) = Create(o => {
				o.GateTimeout = TimeSpan.FromMilliseconds(50);
				o.FinishTimeout = TimeSpan.FromMilliseconds(100);
			});
			var raised = false;
			session.StopOverdue += (sender, _) => raised = true;
			Wait(session.StartAsync());
			Assert.That(Wait(session.StopAsync()).Overdue, Is.False);
			Thread.Sleep(300);
			Assert.That(raised || session.IsStopOverdue, Is.False, "a finished stop is never reported late");
		}

		[Test]
		public void EmergencyStopClosesTheGateFromAnyThread()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			Task.Run(() => session.EmergencyStop()).Wait(Timeout);
			var completion = Wait(session.Completion);
			Assert.That(completion.Completed, Is.True);
			Assert.That(native.GateOpen, Is.False);
			Assert.That(native.Finished, Is.True);
			Assert.That(session.Publish(Game, DofElementKind.Lamp, 1, 1), Is.False);
		}

		[Test]
		public void EmergencyStopLatchesDofOnTheCallingThread()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			// The worker is idle, so the native handle is free: the calling thread latches DOF's gate before it returns,
			// and an opening DOF has not applied yet turns into a closing there.
			var latched = Task.Run(() => {
				session.EmergencyStop();
				return (native.Latched, native.LatchedBy == Thread.CurrentThread);
			});
			Assert.That(latched.Wait(Timeout), Is.True);
			Assert.That(latched.Result, Is.EqualTo((true, true)));
			Wait(session.Completion);
			Assert.That(native.GateOpen, Is.False);
		}

		[Test]
		public void OnlyTheFirstStopOrFaultLatchesDof()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			// Every later fault or emergency stop finds DOF's gate latched and leaves it alone.
			var calls = Enumerable.Range(0, 8).Select(i => Task.Run(() => {
				for (var j = 0; j < 10; j++) {
					if ((i + j) % 2 == 0) {
						session.Fault(UpstreamOverflow);
					} else {
						session.EmergencyStop();
					}
				}
			})).ToArray();
			Assert.That(Task.WaitAll(calls, Timeout), Is.True);
			Wait(session.Completion);
			// The first latch comes from a calling thread, or from the worker if it held the handle then; the worker's
			// own closings latch as well, so only the calling threads' latches are counted.
			Assert.That(native.Latches, Is.GreaterThanOrEqualTo(1));
			Assert.That(native.OutsideLatches, Is.LessThanOrEqualTo(1));
		}

		[Test]
		public void EmergencyStopEndsAPauseThatWaits()
		{
			var (session, native) = Create(o => o.GateTimeout = TimeSpan.FromSeconds(3));
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			native.UnitWrites = false;
			var pause = session.PauseAsync();
			WaitUntil(() => !native.GateOpen, "the pause to close the gate");
			var watch = Stopwatch.StartNew();
			session.EmergencyStop();
			var paused = Wait(pause);
			Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1500), "the pause stops waiting once the gate latches");
			Assert.That(paused.Reason, Does.Contain("stopping"));
			native.UnitWrites = true;
			Assert.That(Wait(session.Completion).Completed, Is.True);
		}

		private const string UpstreamOverflow = "The real-time feed overflowed.";

		[Test]
		public void FaultFromOutsideLatchesAndReports()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			Task.Run(() => session.Fault(UpstreamOverflow)).Wait(Timeout);
			Assert.That(native.Latched && !native.GateOpen, Is.True, "DOF's gate latched before Fault returned");
			WaitUntil(() => session.State == DofSessionState.Faulted, "the fault");
			Assert.That(session.Diagnostics.Fault, Is.EqualTo(UpstreamOverflow));
			Assert.That(session.Publish(Game, DofElementKind.Lamp, 1, 1), Is.False, "events are rejected");
			var resumed = Wait(session.ResumeAsync());
			Assert.That((resumed.Open, resumed.Reason), Is.EqualTo((false, "The session faulted: " + UpstreamOverflow)));
			session.Fault("a second fault");
			Thread.Sleep(20);
			Assert.That(session.Diagnostics.Fault, Is.EqualTo(UpstreamOverflow), "the first reason is kept");

			// The normal stop follows: bounded all-off, finish.
			var stopped = Wait(session.StopAsync());
			Assert.That((stopped.Completed, stopped.Gate.Settled), Is.EqualTo((true, true)));
			Assert.That(native.Calls.Skip(native.Calls.IndexOf("gate:latched") + 1), Has.No.Member("gate:open"));
		}

		[Test]
		public void FaultBeforeTheStartKeepsTheSessionDark()
		{
			var (session, native) = Create();
			session.Fault(UpstreamOverflow);
			Assert.That(Wait(session.StartAsync()).Started, Is.True);
			WaitUntil(() => session.State == DofSessionState.Faulted, "the fault");
			Assert.That(native.Calls.IndexOf("gate:latched"), Is.LessThan(native.Calls.IndexOf("init")), "latched before init");
			Thread.Sleep(50);
			Assert.That(native.GateOpen, Is.False);
			Wait(session.StopAsync());
		}

		[Test]
		public void FaultDoesNotAllocate()
		{
			// Warm up the path on one session, then measure another one's fault.
			var (warm, _) = Create();
			Wait(warm.StartAsync());
			warm.Fault(UpstreamOverflow);
			Wait(warm.StopAsync());

			var native = new FakeNative { RecordCalls = false };
			var (session, _) = Create(native: native);
			Wait(session.StartAsync());
			WaitUntil(() => native.GateOpen, "the gate to open");
			var before = GC.GetAllocatedBytesForCurrentThread();
			session.Fault(UpstreamOverflow);
			var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			Assert.That(native.Latched, Is.True);
			Assert.That(allocated, Is.EqualTo(0));
			Wait(session.StopAsync());
		}

		[Test]
		public void PublishDoesNotAllocate()
		{
			var (session, native) = Create();
			Wait(session.StartAsync());
			// Warm up the path (the JIT's tiering allocates on the calling thread for a while), then measure on this thread
			// only. The best of a few rounds is what Publish itself costs.
			for (var i = 0; i < 20000; i++) {
				session.Publish(Game, DofElementKind.Lamp, i % 64, i & 1);
			}
			var allocated = long.MaxValue;
			for (var round = 0; round < 5; round++) {
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var i = 0; i < 1000; i++) {
					session.Publish(Game, DofElementKind.Lamp, i % 64, i & 1);
				}
				allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
				Thread.Sleep(20);
			}
			Assert.That(allocated, Is.EqualTo(0));
			Wait(session.StopAsync());
		}
	}
}
