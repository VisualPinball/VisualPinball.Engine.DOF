// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VisualPinball.Engine.DOF.Configuration;
using VisualPinball.Plugins;

namespace VisualPinball.Engine.DOF.Player
{
	/// <summary>
	/// Who uses DOF in the process. libdof allows one instance at a time, so a game and a port test exclude each other;
	/// the owner keeps it until its instance is freed.
	/// </summary>
	internal static class DofOwnership
	{
		private static readonly SemaphoreSlim Owner = new SemaphoreSlim(1, 1);

		/// <summary>Waits until nobody uses DOF. The result releases it, from any thread, once.</summary>
		/// <returns>The ownership, or null if the wait timed out.</returns>
		public static async Task<IDisposable> AcquireAsync(TimeSpan timeout, CancellationToken cancellation)
			=> await Owner.WaitAsync(timeout, cancellation).ConfigureAwait(false) ? new Subscription(() => Owner.Release()) : null;

		/// <summary>Takes DOF if nobody uses it, without waiting.</summary>
		public static IDisposable TryAcquire() => Owner.Wait(0) ? new Subscription(() => Owner.Release()) : null;
	}

	/// <summary>
	/// What a DOF instance uses (the configuration revision, the device leases, the ownership of DOF), released together
	/// once, from any thread, after the instance is freed.
	/// </summary>
	internal sealed class DofNativeHold : IDisposable
	{
		private readonly List<IDisposable> _items = new List<IDisposable>();
		private readonly Action<Exception> _fault;
		private int _released;

		public DofNativeHold(Action<Exception> fault) { _fault = fault; }

		public bool Released => Volatile.Read(ref _released) != 0;

		public T Add<T>(T item) where T : IDisposable
		{
			lock (_items) {
				if (_released != 0) {
					throw new ObjectDisposedException(nameof(DofNativeHold));
				}
				_items.Add(item);
			}
			return item;
		}

		/// <summary>Releases everything, last taken first.</summary>
		public void Dispose()
		{
			IDisposable[] items;
			lock (_items) {
				if (_released != 0) {
					return;
				}
				_released = 1;
				items = _items.ToArray();
				_items.Clear();
			}
			for (var i = items.Length - 1; i >= 0; i--) {
				try {
					items[i]?.Dispose();
				} catch (Exception e) {
					_fault?.Invoke(e);
				}
			}
		}
	}

	/// <summary>
	/// Drives the cabinet outside a game: turns one output on for a moment, to find which toy it drives, or turns every
	/// output off. Each opens DOF dark, on the active configuration, with the devices reserved like a game does.
	/// </summary>
	/// <remarks>
	/// The test opens the gate for the whole cabinet while the port is on. Units that test their device when they connect
	/// (TestOnConnect of Wemos D1 and UMX controllers) would light their whole strip if they connected then, still
	/// connecting from init or reconnecting after a failed write, so DOF runs with those connect tests turned off. It also
	/// stays dark until every controller unit confirmed the closed gate, so nothing DOF set on start shows.
	/// </remarks>
	internal static class DofPortTest
	{
		internal const string NoLeases = "The player has not shared its device reservations with DOF yet. Start a game once, then test the outputs.";

		/// <summary>How long the controller units have to confirm that their outputs are off, once DOF has started.</summary>
		internal static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromSeconds(3);

		/// <summary>An output to test: the device (N of directoutputconfigN.ini) and its port, from 1.</summary>
		internal static bool TryParse(string id, out int device, out int port)
		{
			device = port = 0;
			var separator = id?.IndexOf(':') ?? -1;
			return separator > 0 && int.TryParse(id.Substring(0, separator), out device) && int.TryParse(id.Substring(separator + 1), out port)
				&& device > 0 && port > 0;
		}

		internal static string Id(int device, int port) => device + ":" + port;

		/// <summary>
		/// Opens DOF dark and waits until every unit confirmed it, sets the port, opens the gate for
		/// <paramref name="duration"/> and closes it again before the port is turned off and DOF finishes. Runs on the
		/// calling thread and blocks; call it from a worker.
		/// </summary>
		/// <param name="store">The configurations.</param>
		/// <param name="leases">The player's device leases; without them nothing is opened.</param>
		/// <param name="pluginId">Who holds the leases.</param>
		/// <param name="device">The ini device.</param>
		/// <param name="port">The port, from 1.</param>
		/// <param name="duration">How long the port stays on.</param>
		/// <param name="confirmTimeout">How long the units have to confirm the dark state; until all did, the gate stays closed.</param>
		/// <param name="nativeFactory">Creates the DOF instance; tests replace it.</param>
		/// <param name="cancellation">Before the port is on, nothing is turned on; afterwards it ends the pulse early.</param>
		/// <returns>What happened, for the user, and whether a controller sent the pulse for its whole time.</returns>
		internal static (bool Success, string Message) Pulse(DofConfigStore store, PluginOutputLeases leases, string pluginId, int device, int port, TimeSpan duration,
			TimeSpan confirmTimeout, Func<string, IDofTestNative> nativeFactory, CancellationToken cancellation)
			=> Run(store, leases, pluginId, nativeFactory, confirmTimeout, cancellation, (native, dark) => {
				if (cancellation.IsCancellationRequested) {
					return (false, Cancelled);
				}
				if (!dark.Applied || dark.State.Pending > 0) {
					return (false, $"{Units(dark.Units, "DOF")} did not confirm within {confirmTimeout.TotalSeconds:0.#} s that {Their(dark.Units)} outputs "
						+ "are off, so nothing was turned on: a controller that is still connecting could run its own test.");
				}
				if (dark.State.Written == 0) {
					return (false, dark.State.Failed > 0
						? $"No controller can send the pulse: {Units(dark.Units, "every controller")} failed, so nothing was turned on."
						: "No controller took part, so nothing was turned on.");
				}
				var status = native.TestOutput(device, port, 255);
				if (status != DofStatus.Ok) {
					return (false, status == DofStatus.InvalidArgument
						? $"Device {device} has no output {port} DOF can set (it may be part of a toy, such as an RGB LED)."
						: $"DOF could not set output {port} of device {device} ({status}).");
				}
				if (cancellation.IsCancellationRequested) {
					return (false, Cancelled);
				}
				var watch = Stopwatch.StartNew();
				var opening = native.SetOutputGate(true);
				var cancelled = cancellation.WaitHandle.WaitOne(duration);
				native.SetOutputGate(false);
				var open = watch.Elapsed;
				native.TestOutput(device, port, 0);
				// The units that wrote the opening. Which of them drives the tested port is not known, so this counts the
				// controllers that wrote the open gate, not that the port got the pulse; and a write can land after the count.
				var wrote = opening >= 0 ? native.GetGateState(opening).Written : 0;
				var failed = dark.State.Failed > 0 ? $" {Units(dark.Units, "A controller")} failed and took no part." : string.Empty;
				// The times are the gate's, as requested: a controller writes the opening a little later and, when the
				// gate closes early, maybe not at all.
				if (cancelled) {
					return (false, $"Cancelled after {open.TotalSeconds:0.##} s: the pulse to output {port} of device {device} was requested for "
						+ $"{duration.TotalSeconds:0.##} s.{failed}");
				}
				var requested = $"Requested a pulse of {duration.TotalSeconds:0.##} s on output {port} of device {device}";
				return wrote > 0
					? (true, $"{requested}: {Controllers(wrote)} acknowledged the gate opening. Delivery to the selected port is unconfirmed.{failed}")
					: (false, $"{requested}, but no controller confirmed writing it.{failed}");
			});

		private const string Cancelled = "Cancelled; nothing was turned on.";

		/// <summary>
		/// Opens DOF dark, waits until every unit confirmed it, and finishes DOF, which turns every output off, such as one
		/// a crashed game left on. It succeeds when controllers sent their outputs off and none failed, did not confirm or
		/// was not started: those are named, as their outputs may still be on. A controller confirms what it sent, not
		/// what its device did (an Art-Net one sent a UDP packet), and the message says so. Runs on the calling thread and
		/// blocks; call it from a worker.
		/// </summary>
		internal static (bool Success, string Message) AllOff(DofConfigStore store, PluginOutputLeases leases, string pluginId, TimeSpan confirmTimeout,
			Func<string, IDofTestNative> nativeFactory, CancellationToken cancellation)
			=> Run(store, leases, pluginId, nativeFactory, confirmTimeout, cancellation, (native, dark) => {
				var missing = dark.Missing.Count == 0 ? string.Empty
					: $" The cabinet also names {Units(dark.Missing, null)}, which DOF did not start: {Their(dark.Missing)} outputs may still be on.";
				var confirmed = dark.Applied ? dark.State.Written : 0;
				if (dark.Applied && dark.State.Settled && dark.State.Failed == 0) {
					if (confirmed == 0) {
						return (false, "No controller took part, so DOF turned nothing off." + missing);
					}
					// The controllers DOF did not start took no part: the success covers only the others.
					return missing.Length == 0
						? (true, $"DOF sent \"off\" to every output: {Controllers(confirmed)} confirmed sending it.")
						: (false, $"DOF sent \"off\" to the outputs of the controllers it started: {Controllers(confirmed)} confirmed sending it.{missing}");
				}
				string what;
				if (!dark.Applied || dark.State.Failed == 0) {
					what = $"did not confirm within {confirmTimeout.TotalSeconds:0.#} s that {Their(dark.Units)} outputs are off";
				} else if (dark.State.Pending == 0) {
					what = "failed";
				} else {
					what = "failed or did not confirm that their outputs are off";
				}
				var reached = confirmed == 0 ? "No controller confirmed sending \"off\"" : $"{Controllers(confirmed)} confirmed sending \"off\"";
				return (false, $"{reached}, but {Units(dark.Units, "a controller")} {what}: {Their(dark.Units)} outputs may still be on.{missing} "
					+ "Check the connection and try again.");
			});

		private static string Controllers(int count) => count == 1 ? "1 controller" : count + " controllers";

		/// <summary>
		/// The dark state after DOF started: whether the closing applied, its state, the units it misses, and the
		/// controllers the cabinet names that DOF did not start, so they never took part.
		/// </summary>
		internal readonly struct Dark
		{
			public readonly bool Applied;
			public readonly DofGateState State;
			public readonly IReadOnlyList<string> Units;
			public readonly IReadOnlyList<string> Missing;

			public Dark(bool applied, DofGateState state, IReadOnlyList<string> units, IReadOnlyList<string> missing)
			{
				Applied = applied;
				State = state;
				Units = units ?? Array.Empty<string>();
				Missing = missing ?? Array.Empty<string>();
			}
		}

		/// <summary>
		/// Takes DOF, the active revision and the device leases, opens DOF with its gate closed (so nothing shows what DOF
		/// sets on start), closes it once more and waits until every unit confirmed that or failed, runs
		/// <paramref name="test"/>, and finishes DOF, which turns every output off.
		/// </summary>
		private static (bool Success, string Message) Run(DofConfigStore store, PluginOutputLeases leases, string pluginId,
			Func<string, IDofTestNative> nativeFactory, TimeSpan confirmTimeout, CancellationToken cancellation, Func<IDofTestNative, Dark, (bool, string)> test)
		{
			if (leases == null) {
				return (false, NoLeases);
			}
			using (var hold = new DofNativeHold(null)) {
				if (hold.Add(DofOwnership.TryAcquire()) == null) {
					return (false, "DOF is in use by a game. Test the outputs when no game runs.");
				}
				var lease = store.Acquire();
				if (lease == null) {
					return (false, "Import a DOF configuration first.");
				}
				hold.Add(lease);
				var revision = lease.Revision;
				var catalog = DofCatalog.Load(revision);
				foreach (var key in DofGameSetup.LeaseKeys(revision, catalog)) {
					try {
						hold.Add(leases.Acquire(key, pluginId));
					} catch (InvalidOperationException) {
						return (false, $"{DofGameSetup.Describe(key)} is in use by {leases.Owner(key) ?? "another plugin"}.");
					}
				}
				var native = nativeFactory != null ? nativeFactory(revision.BasePath) : DofTestInstance.Create(revision.BasePath);
				try {
					// Before init the gate applies at once, so DOF starts dark. The connect tests stay off for the whole
					// instance, also for a unit that connects or reconnects while the gate is open.
					native.SetOutputGate(false);
					native.SetConnectTests(false);
					native.Init(null, null);
					var result = test(native, ConfirmDark(native, catalog, confirmTimeout, cancellation));
					native.Finish();
					return result;
				} finally {
					native.Dispose();
				}
			}
		}

		/// <summary>
		/// Closes the gate after init and waits until every unit wrote that or failed. The units connect during init, and
		/// the closing before init says nothing about them.
		/// </summary>
		private static Dark ConfirmDark(IDofTestNative native, DofCatalog catalog, TimeSpan timeout, CancellationToken cancellation)
		{
			var missing = NotStarted(native, catalog);
			var generation = native.SetOutputGate(false);
			if (generation < 0) {
				return new Dark(false, default, Array.Empty<string>(), missing);
			}
			var watch = Stopwatch.StartNew();
			var state = native.GetGateState(generation);
			while (!state.Settled && watch.Elapsed < timeout && !cancellation.WaitHandle.WaitOne(5)) {
				state = native.GetGateState(generation);
			}
			var units = state.Settled && state.Failed == 0 ? Array.Empty<string>() : native.GetGatePending(generation);
			return new Dark(true, state, units, missing);
		}

		/// <summary>
		/// The controllers the cabinet configures that DOF did not start, such as one of a type libdof skips: they
		/// register no unit, so the gate state cannot tell about them. A controller the cabinet names is found by the name
		/// libdof gives it (<see cref="DofXml.ControllerName"/>); one libdof names itself, by its type among the started
		/// controllers no name matched.
		/// </summary>
		private static IReadOnlyList<string> NotStarted(IDofTestNative native, DofCatalog catalog)
		{
			if (catalog.Controllers.Count == 0) {
				return Array.Empty<string>();
			}
			List<DofController> started;
			try {
				started = new List<DofController>(native.Controllers());
			} catch (DofException) {
				return Array.Empty<string>();
			}
			var missing = new List<string>();
			foreach (var named in new[] { true, false }) {
				foreach (var controller in catalog.Controllers) {
					if (controller.Named != named) {
						continue;
					}
					var index = started.FindIndex(c => named
						? string.Equals(c.Name, controller.Name, StringComparison.Ordinal)
						: string.Equals(c.Type, controller.Type, StringComparison.Ordinal));
					if (index >= 0) {
						started.RemoveAt(index);
					} else if (!missing.Contains(controller.Name)) {
						missing.Add(controller.Name);
					}
				}
			}
			return missing;
		}

		/// <summary>The units' names for a message, or <paramref name="none"/> without names.</summary>
		private static string Units(IReadOnlyList<string> units, string none)
		{
			if (units.Count == 0) {
				return none;
			}
			if (units.Count == 1) {
				return units[0];
			}
			var names = new string[units.Count - 1];
			for (var i = 0; i < names.Length; i++) {
				names[i] = units[i];
			}
			return string.Join(", ", names) + " and " + units[units.Count - 1];
		}

		private static string Their(IReadOnlyList<string> units) => units.Count > 1 ? "their" : "its";
	}

	/// <summary>The calls of a port test; <see cref="DofInstance"/> outside tests.</summary>
	internal interface IDofTestNative : IDisposable
	{
		void SetConnectTests(bool allowed);
		void Init(string tableFile, string romName);
		/// <summary>The controllers DOF started (<see cref="DofInstance.Describe"/>), after init.</summary>
		IReadOnlyList<DofController> Controllers();
		long SetOutputGate(bool open);
		DofGateState GetGateState(long generation);
		IReadOnlyList<string> GetGatePending(long generation);
		DofStatus TestOutput(int device, int port, int value);
		void Finish();
	}

	internal sealed class DofTestInstance : IDofTestNative
	{
		private readonly DofInstance _instance;
		private DofTestInstance(DofInstance instance) { _instance = instance; }
		public static IDofTestNative Create(string basePath) => new DofTestInstance(DofInstance.Create(basePath));
		public void SetConnectTests(bool allowed) => _instance.SetConnectTests(allowed);
		public void Init(string tableFile, string romName) => _instance.Init(tableFile, romName);
		public IReadOnlyList<DofController> Controllers() => _instance.Describe().Controllers;
		public long SetOutputGate(bool open) => _instance.SetOutputGate(open);
		public DofGateState GetGateState(long generation) => _instance.GetGateState(generation);
		public IReadOnlyList<string> GetGatePending(long generation) => _instance.GetGatePending(generation);
		public DofStatus TestOutput(int device, int port, int value) => _instance.TestOutput(device, port, value);
		public void Finish() => _instance.Finish();
		public void Dispose() => _instance.Dispose();
	}
}
