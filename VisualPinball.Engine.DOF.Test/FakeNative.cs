// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using VisualPinball.Engine.DOF.Session;

namespace VisualPinball.Engine.DOF.Test
{
	/// <summary>A DOF instance that records the calls a session makes, with one controller unit that writes at once.</summary>
	internal sealed class FakeNative : IDofNative
	{
		private readonly object _lock = new object();
		private readonly List<string> _calls = new List<string>();
		private long _generation;

		/// <summary>Thrown by <see cref="Init"/> when set.</summary>
		public string InitError;
		/// <summary>The table file of each <see cref="Init"/>.</summary>
		public readonly List<string> InitTableFiles = new List<string>();
		/// <summary>How long <see cref="Finish"/> takes.</summary>
		public TimeSpan FinishDelay = TimeSpan.Zero;
		/// <summary>Reported as <see cref="PendingInput"/>.</summary>
		public int Pending;
		/// <summary>When false, gate changes are never written (the unit stays pending).</summary>
		public volatile bool UnitWrites = true;
		/// <summary>When set, every change stays queued (<see cref="PendingInput"/>) until <see cref="Process"/>.</summary>
		public volatile bool HoldInput;
		/// <summary>When above 0, the unit starts writing (<see cref="UnitWrites"/>) once that many changes are queued.</summary>
		public volatile int SettleAt;
		/// <summary>When set, DOF's main thread takes everything queued once the unit writes, when the gate state is asked.</summary>
		public volatile bool CatchUpOnSettle;
		/// <summary>When set, <see cref="SetOutputGate"/> throws.</summary>
		public volatile bool GateError;
		/// <summary>When cleared, calls are not recorded (recording allocates on the calling thread).</summary>
		public volatile bool RecordCalls = true;

		public volatile bool GateOpen = true;
		/// <summary>Set by <see cref="LatchOutputGate"/>: from then on every opening is a closing.</summary>
		public volatile bool Latched;
		/// <summary>The thread that latched the gate first.</summary>
		public volatile Thread LatchedBy;
		/// <summary>How many latches came from threads other than the session's worker.</summary>
		public int OutsideLatches;
		/// <summary>How many latches there were, from any thread.</summary>
		public int Latches;
		/// <summary>Runs at the start of every <see cref="LatchOutputGate"/>, on the thread that latches.</summary>
		public volatile Action OnLatch;
		/// <summary>Runs at the start of every <see cref="SetOutputGate"/>, with the state asked for, before DOF takes it.</summary>
		public volatile Action<bool> OnSetOutputGate;
		/// <summary>How many times <see cref="GetGateState"/> was asked.</summary>
		public int GateStateCalls;
		public volatile bool Disposed;
		public volatile bool Finished;
		public readonly ManualResetEventSlim FinishStarted = new ManualResetEventSlim(false);

		/// <summary>Everything the session called, in order: "init", "gate:open", "gate:closed", "gate:latched", "S1=255", "$name=1", "finish", "dispose".</summary>
		public List<string> Calls
		{
			get {
				lock (_lock) {
					return new List<string>(_calls);
				}
			}
		}

		/// <summary>Only the element changes, in order.</summary>
		public List<string> Changes => Calls.FindAll(c => c.Contains("="));

		private void Record(string call)
		{
			if (!RecordCalls) {
				return;
			}
			lock (_lock) {
				_calls.Add(call);
			}
		}

		public void SetOutputMap(int device, IReadOnlyList<int> targets) => Record($"map:{device}:{string.Join(",", targets)}");

		public void Init(string tableFile, string romName)
		{
			lock (_lock) {
				InitTableFiles.Add(tableFile);
			}
			Record("init");
			if (InitError != null) {
				throw new DofException(DofStatus.Error, InitError);
			}
		}

		public void Data(char type, int number, int value) => Queue($"{type}{number}={value}");

		public void DataNamed(string name, int value) => Queue($"${name}={value}");

		private void Queue(string change)
		{
			Record(change);
			if (HoldInput && Interlocked.Increment(ref Pending) == SettleAt) {
				UnitWrites = true;
			}
		}

		/// <summary>DOF's main thread takes everything queued.</summary>
		public void Process() => Interlocked.Exchange(ref Pending, 0);

		public int PendingInput => Volatile.Read(ref Pending);

		public void Finish()
		{
			Record("finish");
			FinishStarted.Set();
			if (FinishDelay > TimeSpan.Zero) {
				Thread.Sleep(FinishDelay);
			}
			Finished = true;
		}

		public long SetOutputGate(bool open)
		{
			OnSetOutputGate?.Invoke(open);
			if (GateError) {
				throw new DofException(DofStatus.Error, "the gate failed");
			}
			// As DOF does: a change and a latch from another thread are one after the other, and after a latch every
			// change is a closing.
			lock (_lock) {
				Record(open ? "gate:open" : "gate:closed");
				GateOpen = open && !Latched;
				return Interlocked.Increment(ref _generation);
			}
		}

		public long LatchOutputGate()
		{
			OnLatch?.Invoke();
			if (GateError) {
				throw new DofException(DofStatus.Error, "the gate failed");
			}
			lock (_lock) {
				Record("gate:latched");
				if (LatchedBy == null) {
					LatchedBy = Thread.CurrentThread;
				}
				Interlocked.Increment(ref Latches);
				if (Thread.CurrentThread.Name != "DOF session") {
					Interlocked.Increment(ref OutsideLatches);
				}
				Latched = true;
				GateOpen = false;
				return Interlocked.Increment(ref _generation);
			}
		}

		public DofGateState GetGateState(long generation)
		{
			Interlocked.Increment(ref GateStateCalls);
			if (!UnitWrites) {
				return new DofGateState(true, 0, 1, 0);
			}
			if (CatchUpOnSettle) {
				Process();
			}
			return new DofGateState(true, 1, 0, 0);
		}

		public IReadOnlyList<string> GetGatePending(long generation) => UnitWrites ? Array.Empty<string>() : new[] { "Fake 1" };

		public DofCabinetDescription Describe() => null;

		public void Dispose()
		{
			Record("dispose");
			Disposed = true;
		}
	}
}
