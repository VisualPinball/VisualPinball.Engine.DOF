// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using VisualPinball.Engine.DOF.Player;
using VisualPinball.Engine.DOF.Session;
using VisualPinball.Plugins;
using Is = NUnit.Framework.Is;

namespace VisualPinball.Engine.DOF.Unity.Tests
{
	/// <summary>How the game's real-time outputs become DOF elements.</summary>
	public class DofFeedTests
	{
		[TestCase(RealtimeOutputKind.Solenoid, DofElementKind.Solenoid)]
		[TestCase(RealtimeOutputKind.Switch, DofElementKind.Switch)]
		[TestCase(RealtimeOutputKind.Lamp, DofElementKind.Lamp)]
		[TestCase(RealtimeOutputKind.GeneralIllumination, DofElementKind.GI)]
		[TestCase(RealtimeOutputKind.Mech, DofElementKind.Mech)]
		public void MapsEachElementKind(RealtimeOutputKind kind, DofElementKind expected)
		{
			Assert.That(DofGameSession.TryMap(kind, out var element), Is.True);
			Assert.That(element, Is.EqualTo(expected));
		}

		[Test]
		public void MapsEveryKindOfTheFeed()
		{
			var unmapped = Enum.GetValues(typeof(RealtimeOutputKind)).Cast<RealtimeOutputKind>()
				.Where(kind => kind != RealtimeOutputKind.SnapshotBoundary && kind != RealtimeOutputKind.SnapshotAborted && kind != RealtimeOutputKind.Overflow
					&& !DofGameSession.TryMap(kind, out _)).ToList();
			Assert.That(unmapped, Is.Empty, "a new kind of the feed needs a DOF element, or a decision to drop it");
		}

		[Test]
		public void DropsTheBoundaryAndUnknownKinds()
		{
			Assert.That(DofGameSession.TryMap(RealtimeOutputKind.SnapshotBoundary, out _), Is.False, "the boundary is not an element");
			Assert.That(DofGameSession.TryMap(RealtimeOutputKind.SnapshotAborted, out _), Is.False);
			Assert.That(DofGameSession.TryMap((RealtimeOutputKind)99, out _), Is.False);
		}

		[Test]
		public void TurnsTheOutputsOffWhenTheFeedLostValues()
		{
			var session = new DofGameSession(new DofSessionOptions { BasePath = "unused", GameSession = 3, RingCapacity = 1024 });
			session.Receiver(RealtimeOutputKind.Overflow, RealtimeOutputOverflow.Lamps, 12);
			Assert.That(session.State, Does.StartWith("off:").And.Contain("lamp"));
			Assert.That(session.Completion.IsCompleted, Is.False, "a faulted session stays until it is stopped");
			session.Receiver(RealtimeOutputKind.Overflow, RealtimeOutputOverflow.Outputs, 1);
			Assert.That(session.State, Does.Contain("lamp"), "the first fault is the one reported");
			Assert.That(session.StopAsync().Wait(TimeSpan.FromSeconds(5)), Is.True, "and stops the usual way");
			Assert.That(session.Completion.IsCompleted, Is.True);
		}

		[Test]
		public void ReceivesWithoutAllocating()
		{
			var session = new DofGameSession(new DofSessionOptions { BasePath = "unused", GameSession = 3, RingCapacity = 1024 });
			var receive = session.Receiver;
			receive(RealtimeOutputKind.Solenoid, 1, 1);
			receive(RealtimeOutputKind.SnapshotBoundary, 0, 0);
			Assert.That(() => {
				receive(RealtimeOutputKind.Solenoid, 1, 255);
				receive(RealtimeOutputKind.Lamp, 12, 1);
				receive(RealtimeOutputKind.Switch, 15, 0);
				receive(RealtimeOutputKind.GeneralIllumination, 0, 8);
				receive(RealtimeOutputKind.Mech, -1, 20);
				receive(RealtimeOutputKind.SnapshotBoundary, 0, 0);
				receive((RealtimeOutputKind)99, 1, 1);
			}, Is.Not.AllocatingGCMemory());
			session.EmergencyStop();

			// The snapshot is kept in a preallocated buffer until its boundary.
			var staging = new DofGameSession(new DofSessionOptions { BasePath = "unused", GameSession = 3, RingCapacity = 1024 });
			var stage = staging.Receiver;
			Assert.That(() => {
				stage(RealtimeOutputKind.Solenoid, 1, 1);
				stage(RealtimeOutputKind.SnapshotAborted, 0, 0);
				stage(RealtimeOutputKind.Solenoid, 1, 0);
				stage(RealtimeOutputKind.Lamp, 12, 1);
				stage(RealtimeOutputKind.SnapshotBoundary, 0, 0);
			}, Is.Not.AllocatingGCMemory());
			staging.EmergencyStop();
		}

		[Test]
		public void KeepsAnAbandonedSnapshotAttemptFromDof()
		{
			var native = new BlockingNative();
			var session = new DofGameSession(new DofSessionOptions { BasePath = "unused", GameSession = 3, NativeFactory = _ => native });
			try {
				Assert.That(session.StartAsync().Wait(TimeSpan.FromSeconds(5)), Is.True);
				var receive = session.Receiver;
				// The game abandons its first attempt, which could start a timed effect in DOF.
				receive(RealtimeOutputKind.Solenoid, 1, 1);
				receive(RealtimeOutputKind.Lamp, 7, 1);
				receive(RealtimeOutputKind.SnapshotAborted, 0, 0);
				receive(RealtimeOutputKind.Solenoid, 1, 0);
				receive(RealtimeOutputKind.Lamp, 7, 0);
				receive(RealtimeOutputKind.Solenoid, 2, 1);
				receive(RealtimeOutputKind.SnapshotBoundary, 0, 0);
				receive(RealtimeOutputKind.Solenoid, 3, 1);

				var watch = Stopwatch.StartNew();
				while (!native.Calls.Contains("S3=1") && watch.Elapsed < TimeSpan.FromSeconds(5)) {
					Thread.Sleep(5);
				}
				var calls = native.Calls;
				Assert.That(calls, Does.Contain("S3=1"), "changes follow the snapshot");
				Assert.That(calls, Does.Contain("S2=1"), "the complete attempt reaches DOF at its boundary");
				Assert.That(calls, Has.No.Member("S1=1").And.No.Member("L7=1"), "nothing of the abandoned attempt does");
			} finally {
				session.StopAsync().Wait(TimeSpan.FromSeconds(5));
			}
		}

		[Test]
		public void KeepsBothEdgesOfPulsesFromSeveralThreads()
		{
			const int pulses = 500;
			var native = new BlockingNative();
			var session = new DofGameSession(new DofSessionOptions { BasePath = "unused", GameSession = 3, WaitForSnapshot = false, NativeFactory = _ => native });
			try {
				Assert.That(session.StartAsync().Wait(TimeSpan.FromSeconds(5)), Is.True);
				using (var start = new Barrier(2)) {
					var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() => {
						start.SignalAndWait();
						for (var i = 0; i < pulses; i++) {
							session.PulseEvent(101);
						}
					})).ToArray();
					foreach (var thread in threads) {
						thread.Start();
					}
					foreach (var thread in threads) {
						thread.Join();
					}
				}
				// Two threads pulsing the same event at once: an interleaving of 1, 1, 0, 0 would be one pulse in DOF.
				const int expected = 2 * pulses * 2;
				var watch = Stopwatch.StartNew();
				var events = native.Calls.Where(c => c.StartsWith("E101=")).ToList();
				while (events.Count < expected && watch.Elapsed < TimeSpan.FromSeconds(10)) {
					Thread.Sleep(5);
					events = native.Calls.Where(c => c.StartsWith("E101=")).ToList();
				}
				Assert.That(events.Count, Is.EqualTo(expected), "every pulse reaches DOF as an on and an off");
				Assert.That(events.Where((call, i) => call != (i % 2 == 0 ? "E101=1" : "E101=0")), Is.Empty);
			} finally {
				session.StopAsync().Wait(TimeSpan.FromSeconds(5));
			}
		}

		[Test]
		public void TakesTheNamedPathWithoutTheDollarSign()
		{
			Assert.That(DofGameSession.ElementName("$knocker"), Is.EqualTo("knocker"));
			Assert.That(DofGameSession.ElementName("  $S1 "), Is.EqualTo("S1"));
			Assert.That(DofGameSession.ElementName(null), Is.EqualTo(""));
		}
	}
}
