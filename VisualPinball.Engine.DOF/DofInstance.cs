// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
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
	/// Calls may come from any thread, but <see cref="Dispose"/> must not run concurrently with another call on the same
	/// instance: the owner serialises them (the session worker does). <see cref="Finish"/> blocks until libdof's threads
	/// stopped, which can take as long as the slowest device; call it from a thread that can wait.
	/// </remarks>
	public sealed class DofInstance : IDisposable
	{
		private const int ErrorSize = 1024;

		private IntPtr _handle;

		/// <summary>The folder that contains <c>directoutputconfig/</c>; libdof reads no file outside it.</summary>
		public string BasePath { get; }

		private DofInstance(IntPtr handle, string basePath)
		{
			_handle = handle;
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
			var status = DofNative.Init(Handle, Utf8.Encode(tableFile == null ? string.Empty : DofPaths.ToNative(tableFile)),
				Utf8.Encode(romName ?? string.Empty), error, error.Length);
			DofException.ThrowIfFailed(status, "Initialising DOF", Utf8.DecodeBuffer(error));
		}

		/// <summary>
		/// Queues a table element change for DOF's main thread. Returns at once and does not allocate.
		/// </summary>
		/// <param name="type">'S' solenoid, 'L' lamp, 'G' GI string, 'W' switch, 'N' mech, 'D' display, 'E' event.</param>
		public void Data(char type, int number, int value)
		{
			var handle = _handle;
			if (handle != IntPtr.Zero) {
				DofNative.Data(handle, (byte)type, number, value);
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
			var handle = _handle;
			if (handle != IntPtr.Zero) {
				DofNative.DataNamed(handle, Utf8.Encode(NativeName(name)), value);
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
				var handle = _handle;
				return handle == IntPtr.Zero ? 0 : DofNative.PendingInput(handle);
			}
		}

		/// <summary>Turns every output off, stops DOF's threads and disconnects the controllers. Blocks until done.</summary>
		public void Finish()
		{
			var error = new byte[ErrorSize];
			var status = DofNative.Finish(Handle, error, error.Length);
			DofException.ThrowIfFailed(status, "Finishing DOF", Utf8.DecodeBuffer(error));
		}

		/// <summary>
		/// Closes or opens the output gate. While closed, every output sends 0 and keeps the value effects set for when it
		/// opens again. Before <see cref="Init"/> it applies at once; afterwards it is applied by DOF's main thread and this
		/// returns at once. It never waits for DOF's main thread or a device, only for short internal locks. A change made
		/// just before <see cref="Finish"/> is still applied, after the table element changes made before it; once finish
		/// started, an opening returns -1 and a closing latches the gate (<see cref="LatchOutputGate"/>).
		/// </summary>
		/// <returns>The gate generation to pass to <see cref="GetGateState"/>, or -1 while init or finish runs or after finish.</returns>
		public long SetOutputGate(bool open)
		{
			var handle = _handle;
			return handle == IntPtr.Zero ? -1 : DofNative.SetOutputGate(handle, open ? 1 : 0);
		}

		/// <summary>
		/// Turns the controllers' connect test sequences (TestOnConnect of Wemos D1 and UMX controllers) off for the rest
		/// of the instance, or on again. They run only while the output gate is open; turned off, a controller that
		/// connects late or reconnects while the gate is open does not light its whole strip. Call before
		/// <see cref="Init"/> to cover the controllers it connects. Every new instance allows them.
		/// </summary>
		public void SetConnectTests(bool allowed) => DofException.ThrowIfFailed(DofNative.SetConnectTests(Handle, allowed ? 1 : 0), "Setting DOF's connect tests");

		/// <summary>
		/// Any thread, in any state until disposed: closes the output gate at once and latches it closed for the rest of
		/// the instance. An opening requested before the latch that DOF's main thread has not applied yet never reaches
		/// the controllers as an opening: it is not applied until DOF's main thread gets to it, then reports
		/// <see cref="DofGateState.Superseded"/>. Every change requested after the latch applies as a closing and reports
		/// the closed state. When latches race, every change requested after the first one, the other latches included,
		/// is a closing, and a state once reported does not change when a later latch returns. The closing reaches the
		/// controllers without waiting for DOF's main thread: an idle updater checks the gate every 50 ms, its polling
		/// interval, which bounds how long it takes to notice; a busy one stops the opening's transfer before its next
		/// write to the device (each report, packet, command or retry), so nothing of the opening goes out after the
		/// latch returned, and then sends the closing to every output. Takes no lock and never allocates, so the
		/// simulation thread or a fault handler may call it. Once it returned, the gate reports closed, also to an
		/// opening DOF's main thread is applying at that moment. It never reaches a later instance's gate.
		/// </summary>
		/// <returns>The closing's generation, or -1 after <see cref="Dispose"/>.</returns>
		public long LatchOutputGate()
		{
			var handle = _handle;
			return handle == IntPtr.Zero ? -1 : DofNative.LatchOutputGate(handle);
		}

		public DofGateState GetGateState(long generation)
		{
			var status = DofNative.GateState(Handle, generation, out var written, out var pending, out var failed, out var untouched);
			return new DofGateState(status == 1, written, pending, failed, status == 2, untouched);
		}

		/// <summary>The names of the controller units that have not written <paramref name="generation"/> yet, or failed.</summary>
		public IReadOnlyList<string> GetGatePending(long generation)
		{
			var handle = Handle;
			return StringCollector.Collect(user => DofNative.GatePending(handle, generation, StringCollector.Callback, user), out _);
		}

		/// <summary>
		/// Sets one physical port of an ini device (the N of directoutputconfigN.ini, 1 for directoutputconfig.ini) to a
		/// value, through the output gate.
		/// </summary>
		/// <returns>
		/// <see cref="DofStatus.InvalidArgument"/> if the device or port does not exist or the port names a toy instead of
		/// a controller output; <see cref="DofStatus.InvalidState"/> before init or after finish.
		/// </returns>
		public DofStatus TestOutput(int device, int port, int value) => (DofStatus)DofNative.TestOutput(Handle, device, port, value);

		/// <summary>The output controllers and toys, after <see cref="Init"/>.</summary>
		public DofCabinetDescription Describe()
		{
			var handle = Handle;
			var lines = StringCollector.Collect(user => DofNative.Describe(handle, StringCollector.Callback, user), out var status);
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

		/// <summary>Frees the instance, finishing it first if needed, so a new one can be created.</summary>
		public void Dispose()
		{
			var handle = _handle;
			_handle = IntPtr.Zero;
			if (handle != IntPtr.Zero) {
				DofNative.Destroy(handle);
			}
		}

		private IntPtr Handle
		{
			get {
				var handle = _handle;
				if (handle == IntPtr.Zero) {
					throw new ObjectDisposedException(nameof(DofInstance));
				}
				return handle;
			}
		}
	}
}
