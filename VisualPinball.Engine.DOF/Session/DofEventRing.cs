// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;

namespace VisualPinball.Engine.DOF.Session
{
	/// <summary>The kinds of table element DOF knows, and the snapshot boundary.</summary>
	public enum DofElementKind : byte
	{
		/// <summary>'S': solenoids and other driver outputs.</summary>
		Solenoid,
		/// <summary>'W': switches.</summary>
		Switch,
		/// <summary>'L': lamps.</summary>
		Lamp,
		/// <summary>'G': GI strings.</summary>
		GI,
		/// <summary>'N': mechs (n is the position of user mech n, -n its speed).</summary>
		Mech,
		/// <summary>'E': events, numbered by the table.</summary>
		Event,
		/// <summary>'D': display outputs.</summary>
		Display,
		/// <summary>Not an element: the initial snapshot is complete.</summary>
		SnapshotBoundary,
	}

	internal struct DofEvent
	{
		public long Sequence;
		/// <summary>Stopwatch timestamp of the publication.</summary>
		public long Timestamp;
		public int Number;
		public int Value;
		public DofElementKind Kind;
	}

	/// <summary>
	/// A bounded single-producer, single-consumer ring of element changes. The producer (the simulation thread) never
	/// allocates or blocks; it publishes each entry with a release write of the tail and signals the consumer only when
	/// the consumer said it is about to wait.
	/// </summary>
	internal sealed class DofEventRing
	{
		private readonly DofEvent[] _entries;
		private readonly int _mask;
		private readonly AutoResetEvent _signal = new AutoResetEvent(false);

		// Written by the producer only.
		private long _tail;
		// Written by the consumer only.
		private long _head;
		private int _waiting;

		public DofEventRing(int capacity)
		{
			if (capacity < 2 || (capacity & (capacity - 1)) != 0) {
				throw new ArgumentException("The capacity must be a power of two.", nameof(capacity));
			}
			_entries = new DofEvent[capacity];
			_mask = capacity - 1;
		}

		public int Capacity => _entries.Length;

		/// <summary>Entries published but not taken yet.</summary>
		public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

		/// <summary>The wait handle the consumer sleeps on; other wake-ups (commands) signal it too.</summary>
		public WaitHandle Signal => _signal;

		/// <summary>Producer: appends an entry. Returns false if the ring is full; nothing is written then.</summary>
		public bool TryWrite(DofElementKind kind, int number, int value, long timestamp)
		{
			var tail = _tail;
			if (tail - Volatile.Read(ref _head) >= _entries.Length) {
				return false;
			}
			ref var entry = ref _entries[tail & _mask];
			entry.Sequence = tail;
			entry.Timestamp = timestamp;
			entry.Kind = kind;
			entry.Number = number;
			entry.Value = value;
			// A full fence, not only a release: the read of the waiting flag must not move before the tail store, or a
			// consumer that is just starting to wait would miss this entry.
			Interlocked.Exchange(ref _tail, tail + 1);
			if (Volatile.Read(ref _waiting) != 0) {
				_signal.Set();
			}
			return true;
		}

		/// <summary>Consumer: takes the next entry, if there is one.</summary>
		public bool TryRead(out DofEvent entry)
		{
			var head = _head;
			if (head == Volatile.Read(ref _tail)) {
				entry = default;
				return false;
			}
			entry = _entries[head & _mask];
			Volatile.Write(ref _head, head + 1);
			return true;
		}

		/// <summary>
		/// Consumer: sleeps until an entry is published, <see cref="Wake"/> is called or the timeout passes. Returns at once
		/// if entries are waiting.
		/// </summary>
		public void Wait(int timeoutMs)
		{
			Interlocked.Exchange(ref _waiting, 1);
			// Re-check after announcing the wait: an entry published before the flag was visible would not signal.
			if (Volatile.Read(ref _tail) == _head) {
				_signal.WaitOne(timeoutMs);
			}
			Interlocked.Exchange(ref _waiting, 0);
		}

		/// <summary>Any thread: wakes the consumer.</summary>
		public void Wake() => _signal.Set();
	}
}
