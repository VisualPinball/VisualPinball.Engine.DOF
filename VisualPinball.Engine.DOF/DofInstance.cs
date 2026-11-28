// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualPinball.Engine.DOF.Native;

namespace VisualPinball.Engine.DOF
{
	/// <summary>The state of one output gate change, from <see cref="DofInstance.GetGateState"/>.</summary>
	public readonly struct DofGateState
	{
		/// <summary>The change was published to the controllers: by DOF's main thread, or at once for a latch.</summary>
		public bool Applied { get; }
		/// <summary>
		/// This is an opening that a latch overtook (requested before the latch, applied after it), which never reaches
		/// the controllers as an opening, or an applied change that a later change reaching the controllers with the
		/// other state replaced. The latest change is the one to wait for. A superseded change stays superseded.
		/// </summary>
		/// <remarks>
		/// Only the latest change's counts tell what the outputs show. For an older change they are what each unit wrote
		/// last in that state, at different times. A change old enough to be forgotten reports superseded.
		/// </remarks>
		public bool Superseded { get; }
		/// <summary>
		/// Controller units that confirmed the state: a transfer to their device carried it, or a later change to the same
		/// state did.
		/// </summary>
		/// <remarks>
		/// Written is not a physical acknowledgement: it means the transport took the data (an Art-Net unit, for one, has
		/// only sent a packet).
		/// </remarks>
		public int Written { get; }
		/// <summary>Units that have not written it yet.</summary>
		public int Pending { get; }
		/// <summary>
		/// Units whose write failed, whose device is missing, or whose updater ended on its own, even after it wrote the
		/// state. A unit that finished cleanly keeps what it wrote or left alone, and fails later changes.
		/// </summary>
		public int Failed { get; }
		/// <summary>
		/// Units DOF never used, which it leaves alone while the gate is open: nothing was written to them. A closing has
		/// none, since it is written to every unit.
		/// </summary>
		public int Untouched { get; }

		/// <summary>Applied, and every unit has written it, failed, or is left alone.</summary>
		public bool Settled => Applied && Pending == 0;

		public DofGateState(bool applied, int written, int pending, int failed, bool superseded = false)
			: this(applied, written, pending, failed, superseded, 0)
		{
		}

		public DofGateState(bool applied, int written, int pending, int failed, bool superseded, int untouched)
		{
			Applied = applied || superseded;
			Superseded = superseded;
			Written = written;
			Pending = pending;
			Failed = failed;
			Untouched = untouched;
		}

		public override string ToString() => $"applied={Applied} superseded={Superseded} written={Written} pending={Pending} failed={Failed} untouched={Untouched}";
	}

	public readonly struct DofController
	{
		/// <summary>libdof's controller type, its XML element name (LedWiz, Pinscape, ArtNet, ...).</summary>
		public string Type { get; }
		public string Name { get; }
		public int Outputs { get; }

		public DofController(string type, string name, int outputs)
		{
			Type = type;
			Name = name;
			Outputs = outputs;
		}
	}

	public readonly struct DofToy
	{
		/// <summary>libdof's toy class (LedWizEquivalent, LedStrip, RGBAToy, ...).</summary>
		public string Type { get; }
		public string Name { get; }

		public DofToy(string type, string name)
		{
			Type = type;
			Name = name;
		}
	}

	/// <summary>The output controllers and toys of a running instance.</summary>
	public sealed class DofCabinetDescription
	{
		public IReadOnlyList<DofController> Controllers { get; }
		public IReadOnlyList<DofToy> Toys { get; }

		internal DofCabinetDescription(IReadOnlyList<DofController> controllers, IReadOnlyList<DofToy> toys)
		{
			Controllers = controllers;
			Toys = toys;
		}
	}

	/// <summary>
	/// The DOF instance of this process: created on a configuration folder, initialised once for a table and ROM, fed
	/// with table element changes, finished and disposed. libdof's state is process-wide, so only one instance exists at
	/// a time; <see cref="Create"/> throws <see cref="DofStatus.Busy"/> while another one is alive.
	/// </summary>
	/// <remarks>
	/// Calls may come from any thread, <see cref="Dispose"/> included. Each call into libdof is admitted first and counted
	/// until it returned; Dispose stops admitting calls, waits for the admitted ones to return, and then destroys the
	/// instance on its own thread, so a call never runs libdof's shutdown. It returns once the instance is destroyed, so a
	/// new instance can be created then; a second Dispose waits for that too. A call that is not admitted any more does
	/// what that member does on a disposed instance. <see cref="Finish"/> blocks until libdof's threads stopped, which
	/// can take as long as the slowest device; call it from a thread that can wait.
	/// <para>
	/// From the log sink (<see cref="DofLibrary.SetLog"/>), Dispose cannot wait: the line comes from a call on that
	/// thread, from the destruction itself, or from one of libdof's threads, which the destruction joins. A Dispose from a
	/// line the destruction writes on its thread, or from a line of libdof's threads once a Dispose started, returns at
	/// once. One from inside a call on the same thread, or from a line of libdof's threads while nothing disposes the
	/// instance, throws <see cref="InvalidOperationException"/> and leaves the instance as it was.
	/// </para>
	/// </remarks>
	public sealed class DofInstance : IDisposable
	{
		private const int ErrorSize = 1024;

		private const int Alive = 0;
		private const int Disposing = 1;
		private const int Destroyed = 2;

		private readonly IntPtr _dof;
		// The calls admitted into libdof (Enter) that have not returned yet.
		private int _calls;
		private int _state = Alive;
		// Set once the instance is destroyed; a second Dispose waits on it. Never disposed: a late waiter may still hold it,
		// so it is left to finalization. The calls never touch it.
		private readonly ManualResetEvent _destroyed = new ManualResetEvent(false);
		// The managed thread that runs the destruction, once a Dispose won.
		private int _destroyingThread;
		// The calls this thread is in, so a Dispose from inside one of them is refused instead of waiting for it.
		[ThreadStatic]
		private static int t_admitted;

		/// <summary>The folder that contains <c>directoutputconfig/</c>; libdof reads no file outside it.</summary>
		public string BasePath { get; }

		private DofInstance(IntPtr dof, string basePath)
		{
			_dof = dof;
			BasePath = basePath;
		}

		/// <summary>Creates the instance on a configuration folder. Its output gate is open until closed.</summary>
		/// <exception cref="DofException"><see cref="DofStatus.Busy"/> while another instance exists.</exception>
		public static DofInstance Create(string basePath)
		{
			if (string.IsNullOrEmpty(basePath)) {
				throw new ArgumentException("A configuration folder is required.", nameof(basePath));
			}
			DofLibrary.EnsureCompatible();
			var handle = DofNative.Create(Utf8.Encode(DofPaths.ToNative(basePath)), out var status);
			if (handle == IntPtr.Zero) {
				DofException.ThrowIfFailed(status == 0 ? (int)DofStatus.Error : status, "Creating the DOF instance",
					status == (int)DofStatus.Busy ? "another DOF instance is running in this process" : null);
			}
			return new DofInstance(handle, basePath);
		}

		/// <summary>
		/// Loads the configuration for a table and ROM, connects the output controllers and starts DOF's threads. Can be
		/// called once. On failure the instance is finished and can only be disposed.
		/// </summary>
		/// <param name="tableFile">A table file name; its folder and base name drive libdof's table lookups. May be null.</param>
		/// <param name="romName">The ROM name the ini rows are matched against. May be null.</param>
		public void Init(string tableFile, string romName)
		{
			var error = new byte[ErrorSize];
			EnterOrThrow();
			int status;
			try {
				status = DofNative.Init(_dof, Utf8.Encode(tableFile == null ? string.Empty : DofPaths.ToNative(tableFile)),
					Utf8.Encode(romName ?? string.Empty), error, error.Length);
			} finally {
				Leave();
			}
			DofException.ThrowIfFailed(status, "Initialising DOF", Utf8.DecodeBuffer(error));
		}

		/// <summary>
		/// Queues a table element change for DOF's main thread. Returns at once and does not allocate.
		/// </summary>
		/// <param name="type">'S' solenoid, 'L' lamp, 'G' GI string, 'W' switch, 'N' mech, 'D' display, 'E' event.</param>
		public void Data(char type, int number, int value)
		{
			if (!Enter()) {
				return;
			}
			try {
				DofNative.Data(_dof, (byte)type, number, value);
			} finally {
				Leave();
			}
		}

		/// <summary>
		/// Queues a change of a named table element (<c>$name</c> in DOF configs, with or without the dollar sign). The
		/// name always addresses a named element: "S1" is the element named S1, not solenoid 1.
		/// </summary>
		public void DataNamed(string name, int value)
		{
			if (string.IsNullOrEmpty(name)) {
				return;
			}
			if (!Enter()) {
				return;
			}
			try {
				DofNative.DataNamed(_dof, Utf8.Encode(NativeName(name)), value);
			} finally {
				Leave();
			}
		}

		/// <summary>
		/// The name as libdof's named path takes it. libdof reads a name that starts with an element type and a number
		/// ("S1", "L12") as that element; with the dollar sign in front it is always a name.
		/// </summary>
		internal static string NativeName(string name) => name[0] == '$' ? name : "$" + name;

		/// <summary>Table element changes queued but not processed by DOF's main thread yet.</summary>
		public int PendingInput
		{
			get {
				if (!Enter()) {
					return 0;
				}
				try {
					return DofNative.PendingInput(_dof);
				} finally {
					Leave();
				}
			}
		}

		/// <summary>Turns every output off, stops DOF's threads and disconnects the controllers. Blocks until done.</summary>
		public void Finish()
		{
			var error = new byte[ErrorSize];
			EnterOrThrow();
			int status;
			try {
				status = DofNative.Finish(_dof, error, error.Length);
			} finally {
				Leave();
			}
			DofException.ThrowIfFailed(status, "Finishing DOF", Utf8.DecodeBuffer(error));
		}

		/// <summary>
		/// Closes or opens the output gate. While closed, every output sends 0 and keeps the value effects set for when
		/// it opens again. Before <see cref="Init"/> it applies at once, so a session can start closed: every
		/// controller then turns its outputs off before an opening reaches it, also one whose first update comes after
		/// the gate opened. Afterwards it is applied by DOF's main thread and this returns at once. It never waits for
		/// DOF's main thread or a device, only for short internal locks. A change made just before <see cref="Finish"/>
		/// is still applied, after the table element changes made before it; once finish started, a closing latches the
		/// gate (<see cref="LatchOutputGate"/>) and returns the latch's generation.
		/// </summary>
		/// <returns>
		/// The gate generation to pass to <see cref="GetGateState"/>, or -1 while init runs, for an opening once finish
		/// started, and after finish.
		/// </returns>
		public long SetOutputGate(bool open)
		{
			if (!Enter()) {
				return -1;
			}
			try {
				return DofNative.SetOutputGate(_dof, open ? 1 : 0);
			} finally {
				Leave();
			}
		}

		/// <summary>
		/// Turns the controllers' connect test sequences (TestOnConnect of Wemos D1 and UMX controllers) off for the
		/// rest of the instance, or on again. They run only while the output gate is open for the controller (one
		/// connected while it was closed turned its outputs off first), and once: a test the gate skipped does not run
		/// on a reconnect. Turned off, a controller that connects late or reconnects while the gate is open does not
		/// light its whole strip. Call before <see cref="Init"/> to cover the controllers it connects. Every new
		/// instance allows them.
		/// </summary>
		public void SetConnectTests(bool allowed)
		{
			EnterOrThrow();
			int status;
			try {
				status = DofNative.SetConnectTests(_dof, allowed ? 1 : 0);
			} finally {
				Leave();
			}
			DofException.ThrowIfFailed(status, "Setting DOF's connect tests");
		}

		/// <summary>
		/// Rewires ini device <paramref name="device"/>'s ports: port <c>i + 1</c> drives the controller output that port
		/// <c>targets[i]</c> drives in the cabinet file (0 or <c>i + 1</c> keeps its own). Ports past the list keep theirs.
		/// An empty list removes the device's map. Call before <see cref="Init"/>; the cabinet's LedWiz equivalents take
		/// the map as DOF loads them, before the table configuration reads them.
		/// </summary>
		public void SetOutputMap(int device, IReadOnlyList<int> targets)
		{
			if (targets == null) {
				throw new ArgumentNullException(nameof(targets));
			}
			var array = targets.ToArray();
			EnterOrThrow();
			int status;
			try {
				status = DofNative.SetOutputMap(_dof, device, array, array.Length);
			} finally {
				Leave();
			}
			DofException.ThrowIfFailed(status, $"Setting the output map of DOF device {device}");
		}

		/// <summary>
		/// Any thread, in any state until disposed: closes the output gate at once and latches it closed for the rest of
		/// the instance. An opening requested before the latch that DOF's main thread has not applied yet never reaches
		/// the controllers as an opening: it is not applied until DOF's main thread gets to it, then reports
		/// <see cref="DofGateState.Superseded"/>. Every change requested after the latch applies as a closing and reports
		/// the closed state. When latches race, every change requested after the first one, the other latches included,
		/// is a closing, and a state once reported does not change when a later latch returns. The closing reaches the
		/// controllers without waiting for DOF's main thread: an idle updater checks the gate every 50 ms, its polling
		/// interval, which bounds how long it takes to notice; a busy one stops the opening's transfer before its next
		/// write to the device (each report, packet, command or retry), and then sends the closing to every output. A
		/// write that was already past that check when the latch came can still reach the device after the latch returned:
		/// at most that one write per controller, followed by the closing. Nothing else of the opening goes out. Takes no
		/// lock, never waits and does not allocate, so the simulation thread or a fault handler may call it, also while a
		/// Dispose waits for the calls. Once it returned, the gate
		/// reports closed, also to an opening DOF's main thread is applying at that moment. It never reaches a later
		/// instance's gate.
		/// </summary>
		/// <returns>The closing's generation, or -1 after <see cref="Dispose"/>.</returns>
		public long LatchOutputGate()
		{
			if (!Enter()) {
				return -1;
			}
			try {
				return DofNative.LatchOutputGate(_dof);
			} finally {
				Leave();
			}
		}

		public DofGateState GetGateState(long generation)
		{
			EnterOrThrow();
			int status, written, pending, failed, untouched;
			try {
				status = DofNative.GateState(_dof, generation, out written, out pending, out failed, out untouched);
			} finally {
				Leave();
			}
			return new DofGateState(status == 1, written, pending, failed, status == 2, untouched);
		}

		/// <summary>The names of the controller units that have not written <paramref name="generation"/> yet, or failed.</summary>
		public IReadOnlyList<string> GetGatePending(long generation)
		{
			EnterOrThrow();
			try {
				return StringCollector.Collect(user => DofNative.GatePending(_dof, generation, StringCollector.Callback, user), out _);
			} finally {
				Leave();
			}
		}

		/// <summary>
		/// Sets one physical port of an ini device (the N of directoutputconfigN.ini, 1 for directoutputconfig.ini) to a
		/// value, through the output gate.
		/// </summary>
		/// <returns>
		/// <see cref="DofStatus.InvalidArgument"/> if the device or port does not exist or the port names a toy instead of
		/// a controller output; <see cref="DofStatus.InvalidState"/> before init or after finish.
		/// </returns>
		public DofStatus TestOutput(int device, int port, int value)
		{
			EnterOrThrow();
			try {
				return (DofStatus)DofNative.TestOutput(_dof, device, port, value);
			} finally {
				Leave();
			}
		}

		/// <summary>The output controllers and toys, after <see cref="Init"/>.</summary>
		public DofCabinetDescription Describe()
		{
			IReadOnlyList<string> lines;
			int status;
			EnterOrThrow();
			try {
				lines = StringCollector.Collect(user => DofNative.Describe(_dof, StringCollector.Callback, user), out status);
			} finally {
				Leave();
			}
			DofException.ThrowIfFailed(status, "Describing the DOF cabinet");
			var controllers = new List<DofController>();
			var toys = new List<DofToy>();
			foreach (var line in lines) {
				var f = line.Split('|');
				if (f[0] == "controller" && f.Length >= 4) {
					controllers.Add(new DofController(f[1], f[2], int.TryParse(f[3], out var n) ? n : 0));
				} else if (f[0] == "toy" && f.Length >= 3) {
					toys.Add(new DofToy(f[1], f[2]));
				}
			}
			return new DofCabinetDescription(controllers, toys);
		}

		/// <summary>
		/// Frees the instance, finishing it first if needed, so a new one can be created. Stops admitting calls, waits for
		/// the admitted ones to return (a running <see cref="Finish"/> too), destroys the instance on this thread and
		/// returns. A Dispose while another one runs returns once that one destroyed the instance. See the remarks of
		/// <see cref="DofInstance"/> for a Dispose from the log sink.
		/// </summary>
		/// <exception cref="InvalidOperationException">
		/// From inside a call into this instance on the same thread, or from a line of one of libdof's threads while no
		/// Dispose runs. The instance is left as it was.
		/// </exception>
		/// <exception cref="ThreadInterruptedException">
		/// The thread was interrupted while it waited. The Dispose that destroys the instance still waits for the calls and
		/// destroys it, and throws once it did. One that waits for another Dispose to destroy it stops waiting and throws:
		/// it destroys nothing, and the other one still does.
		/// </exception>
		public void Dispose()
		{
			var thread = Environment.CurrentManagedThreadId;
			// A line the destruction running on this thread wrote: it is underway.
			if (Volatile.Read(ref _destroyingThread) == thread) {
				return;
			}
			var state = Volatile.Read(ref _state);
			if (state == Destroyed) {
				return;
			}
			if (t_admitted > 0) {
				throw new InvalidOperationException("A DOF instance can't be disposed from inside a call on the same thread, such as a log line it writes.");
			}
			if (DofLibrary.InLogCallback) {
				// One of libdof's threads, which the destruction joins: it can neither run the destruction nor wait for it.
				if (state == Disposing) {
					return;
				}
				throw new InvalidOperationException("A DOF instance can't be disposed from a log line while no other Dispose runs: the line may come from one of libdof's threads, which the destruction joins.");
			}
			if (Interlocked.CompareExchange(ref _state, Disposing, Alive) != Alive) {
				// Another Dispose destroys it. An interrupt stops this wait and propagates; the other one still destroys it.
				_destroyed.WaitOne();
				return;
			}
			Volatile.Write(ref _destroyingThread, thread);
			// The count is read after the state became Disposing, so every call admitted before that is counted, and no call
			// is admitted after it: what the count shows are the admitted calls and the ones being turned away, which take
			// themselves off again at once. Polled, so the calls never signal anything: a call that returns takes no lock and
			// never waits, and Dispose is rare enough that a millisecond's delay costs nothing. The instance is destroyed
			// whatever interrupts this thread meanwhile: only then is the interrupt raised.
			var interrupted = false;
			while (Volatile.Read(ref _calls) != 0) {
				try {
					Thread.Sleep(1);
				} catch (ThreadInterruptedException) {
					interrupted = true;
				}
			}
			try {
				DofNative.Destroy(_dof);
			} finally {
				Volatile.Write(ref _state, Destroyed);
				_destroyed.Set();
			}
			if (interrupted) {
				throw new ThreadInterruptedException("The thread was interrupted while it disposed the DOF instance, which it still destroyed.");
			}
		}

		/// <summary>
		/// Admits a call into libdof, or returns false once Dispose started. No allocation, no lock: an interlocked count
		/// that Dispose drains before it destroys the instance.
		/// </summary>
		private bool Enter()
		{
			// Once disposing, callers that keep calling do not touch the count, so they never hold the drain up.
			if (Volatile.Read(ref _state) != Alive) {
				return false;
			}
			Interlocked.Increment(ref _calls);
			// Checked again after the count, both full fences: either Dispose sees this call, or this call sees Dispose.
			if (Volatile.Read(ref _state) != Alive) {
				Release();
				return false;
			}
			t_admitted++;
			return true;
		}

		private void EnterOrThrow()
		{
			if (!Enter()) {
				throw new ObjectDisposedException(nameof(DofInstance));
			}
		}

		/// <summary>Ends an admitted call.</summary>
		private void Leave()
		{
			t_admitted--;
			Release();
		}

		/// <summary>
		/// Takes a call off the count, which a Dispose that waits for the calls polls: no signal, no lock, no wait, so a call
		/// that returns never blocks or throws here.
		/// </summary>
		private void Release() => Interlocked.Decrement(ref _calls);
	}
}
