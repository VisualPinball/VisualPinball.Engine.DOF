// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using System.Threading.Tasks;
using VisualPinball.Engine.DOF.Session;
using VisualPinball.Plugins;

namespace VisualPinball.Engine.DOF.Player
{
	/// <summary>
	/// One game's <see cref="DofSession"/>, as the plugin uses it: the receiver of the game's real-time feed, the
	/// cabinet-trigger capability, and the lifecycle calls with their results turned into messages.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Everything the plugin needs from the session goes through here, so the session's API is used in one place.
	/// <see cref="Receiver"/> runs on the simulation thread: it does not allocate, lock or touch Unity objects. It keeps
	/// the values of the game's initial snapshot in a preallocated buffer until the snapshot's boundary, so an attempt the
	/// game abandons (<see cref="RealtimeOutputKind.SnapshotAborted"/>) never reaches DOF, where a value could start a
	/// timed effect.
	/// </para>
	/// <para>
	/// The trigger calls come from any thread and go into the session's queue as they come. A pulse is one entry there,
	/// which the session's worker sends as its two edges in a row, so pulses of two threads never merge into one.
	/// </para>
	/// </remarks>
	internal sealed class DofGameSession : IPluginCabinetTriggers
	{
		/// <summary>How many values of the game's initial snapshot are kept until its boundary.</summary>
		internal const int SnapshotCapacity = 8192;

		private readonly DofSession _session;
		private readonly int _gameSession;
		private string _feedFault;

		// Simulation thread only: the snapshot values until the boundary, then live.
		private readonly DofElementKind[] _stagedKinds = new DofElementKind[SnapshotCapacity];
		private readonly int[] _stagedNumbers = new int[SnapshotCapacity];
		private readonly int[] _stagedValues = new int[SnapshotCapacity];
		private int _staged;
		private bool _live;

		/// <summary>The receiver for the game's real-time outputs; allocated once.</summary>
		public readonly RealtimeOutputReceiver Receiver;

		public DofGameSession(DofSessionOptions options)
		{
			_session = new DofSession(options);
			_gameSession = options.GameSession;
			Receiver = Receive;
		}

		#region Feed

		/// <summary>
		/// The DOF element of a real-time output kind. False for what is not an element (the snapshot boundary, an aborted
		/// snapshot and the overflow, which the receiver handles itself) and for kinds DOF does not know, which are dropped.
		/// </summary>
		internal static bool TryMap(RealtimeOutputKind kind, out DofElementKind element)
		{
			switch (kind) {
				case RealtimeOutputKind.Solenoid: element = DofElementKind.Solenoid; return true;
				case RealtimeOutputKind.Switch: element = DofElementKind.Switch; return true;
				case RealtimeOutputKind.Lamp: element = DofElementKind.Lamp; return true;
				case RealtimeOutputKind.GeneralIllumination: element = DofElementKind.GI; return true;
				case RealtimeOutputKind.Mech: element = DofElementKind.Mech; return true;
				default: element = default; return false;
			}
		}

		private const string OutputsLost = "The game lost solenoid and switch changes before they reached DOF.";
		private const string LampsLost = "The game lost lamp changes before they reached DOF.";
		private const string FeedLost = "The game's outputs stopped reaching DOF.";
		private const string SnapshotTooLarge = "The game's initial state has more values than DOF keeps.";

		/// <summary>The reason of a fault when the game's outputs went away while DOF followed them.</summary>
		internal const string FeedWithdrawn = "The game's outputs were withdrawn while DOF followed them.";

		// Simulation thread.
		private void Receive(RealtimeOutputKind kind, int number, int value)
		{
			switch (kind) {
				case RealtimeOutputKind.SnapshotBoundary:
					if (!_live) {
						for (var i = 0; i < _staged; i++) {
							_session.Publish(_gameSession, _stagedKinds[i], _stagedNumbers[i], _stagedValues[i]);
						}
						_staged = 0;
						_live = true;
					}
					_session.PublishSnapshotBoundary(_gameSession);
					return;
				case RealtimeOutputKind.SnapshotAborted:
					// The attempt's values are no state to show; the next attempt sends it all again.
					if (!_live) {
						_staged = 0;
					}
					return;
				case RealtimeOutputKind.Overflow:
					// Values were skipped, pulses among them: DOF can no longer be trusted to show the game.
					FeedFault(number == RealtimeOutputOverflow.Lamps ? LampsLost : OutputsLost);
					return;
			}
			if (!TryMap(kind, out var element)) {
				return;
			}
			if (_live) {
				_session.Publish(_gameSession, element, number, value);
			} else if (_staged < SnapshotCapacity) {
				_stagedKinds[_staged] = element;
				_stagedNumbers[_staged] = number;
				_stagedValues[_staged] = value;
				_staged++;
			} else {
				FeedFault(SnapshotTooLarge);
			}
		}

		/// <summary>
		/// Any thread: the game's feed lost values (it overflowed or failed), so DOF's state can no longer follow the game.
		/// The outputs go dark and stay dark for the rest of the game.
		/// </summary>
		/// <remarks>
		/// The session faults: DOF's gate latches closed at once and the session is stopped the normal way when the game
		/// ends. The reasons are preallocated, so a fault from the simulation thread does not allocate.
		/// </remarks>
		public void FeedFault(string reason)
		{
			if (Interlocked.CompareExchange(ref _feedFault, string.IsNullOrEmpty(reason) ? FeedLost : reason, null) != null) {
				return;
			}
			_session.Fault(_feedFault);
		}

		#endregion

		#region Cabinet triggers

		public void SetEvent(int number, int value) => _session.PublishEvent(number, value);

		public void PulseEvent(int number) => _session.PulseEvent(number);

		public void SetNamed(string name, int value)
		{
			var element = ElementName(name);
			if (element.Length > 0) {
				_session.PublishNamed(element, value);
			}
		}

		public void PulseNamed(string name)
		{
			var element = ElementName(name);
			if (element.Length > 0) {
				_session.PulseNamed(element);
			}
		}

		internal static string ElementName(string name) => (name ?? string.Empty).Trim().TrimStart('$').Trim();

		#endregion

		#region Lifecycle

		/// <summary>Starts DOF. Returns why it did not start (null when it runs) and a description of the cabinet it drives.</summary>
		public async Task<(string Error, string Cabinet)> StartAsync()
		{
			var result = await _session.StartAsync().ConfigureAwait(false);
			if (!result.Started) {
				return (result.Error ?? "DOF did not start.", null);
			}
			var cabinet = result.Cabinet;
			if (cabinet == null) {
				return (null, null);
			}
			var controllers = new string[cabinet.Controllers.Count];
			for (var i = 0; i < controllers.Length; i++) {
				var controller = cabinet.Controllers[i];
				controllers[i] = $"{controller.Name} ({controller.Type}, {controller.Outputs} outputs)";
			}
			var summary = controllers.Length == 0 ? "no output controllers" : string.Join(", ", controllers);
			return (null, cabinet.Toys.Count == 1 ? summary + ", 1 toy" : $"{summary}, {cabinet.Toys.Count} toys");
		}

		/// <summary>
		/// Closes the output gate (pause) or opens it once DOF caught up (resume). Returns what happened; once the session
		/// faulted or stops, the gate stays latched closed, which counts as settled.
		/// </summary>
		public async Task<(bool Settled, string Message)> SetPausedAsync(bool paused)
		{
			var result = await (paused ? _session.PauseAsync() : _session.ResumeAsync()).ConfigureAwait(false);
			if (result.Reason != null && !result.Open && IsEnding(_session.Diagnostics.State)) {
				return (true, result.ToString());
			}
			return (result.Settled && result.Reason == null, result.ToString());
		}

		private static bool IsEnding(DofSessionState state) => state == DofSessionState.Faulted || state == DofSessionState.Stopping || state == DofSessionState.Stopped;

		/// <summary>
		/// Stops DOF. Completes only once DOF has finished and been freed, however long that takes, so the caller can
		/// release what DOF used afterwards. Returns an error or a warning, or null.
		/// </summary>
		public async Task<string> StopAsync()
		{
			var result = await _session.StopAsync().ConfigureAwait(false);
			return Describe(result);
		}

		/// <summary>Completes when DOF has finished and been freed, after a stop, an emergency stop or a failed start.</summary>
		public Task Completion => _session.Completion;

		/// <summary>Any thread, returns at once: the outputs go dark, nothing opens them again, and DOF finishes.</summary>
		public void EmergencyStop() => _session.EmergencyStop();

		/// <summary>Raised once, on a thread-pool thread, when a stop takes longer than it should.</summary>
		public event EventHandler StopOverdue
		{
			add => _session.StopOverdue += value;
			remove => _session.StopOverdue -= value;
		}

		private static string Describe(DofStopResult result)
		{
			if (result.Error != null) {
				return result.Error;
			}
			if (result.Overdue) {
				return "DOF took longer than expected to finish.";
			}
			if (result.Gate != null && result.Gate.Unconfirmed.Count > 0) {
				return "Turning the outputs off was not confirmed by " + string.Join(", ", result.Gate.Unconfirmed) + ".";
			}
			return null;
		}

		#endregion

		#region Diagnostics

		/// <summary>The session's state, as the pause menu shows it.</summary>
		public string State
		{
			get {
				var feedFault = Volatile.Read(ref _feedFault);
				if (feedFault != null) {
					return "off: " + feedFault;
				}
				var diagnostics = _session.Diagnostics;
				switch (diagnostics.State) {
					case DofSessionState.Running: return diagnostics.SnapshotOverdue ? "waiting for the game's outputs" : "running";
					case DofSessionState.Paused: return "paused";
					case DofSessionState.Faulted: return "off: " + (diagnostics.Fault ?? "DOF failed");
					case DofSessionState.Created:
					case DofSessionState.Starting: return "starting";
					default: return diagnostics.State.ToString().ToLowerInvariant();
				}
			}
		}

		/// <summary>Adds the session's counters.</summary>
		public void Describe(Action<string, string> add)
		{
			var d = _session.Diagnostics;
			add("session.state", d.State.ToString());
			if (d.Fault != null) {
				add("session.fault", d.Fault);
			}
			var feedFault = Volatile.Read(ref _feedFault);
			if (feedFault != null) {
				add("feed.fault", feedFault);
			}
			add("events.published", d.Published.ToString());
			add("events.delivered", d.Delivered.ToString());
			add("events.unchanged", d.Unchanged.ToString());
			add("events.rejected", d.Rejected.ToString());
			add("backlog", $"{d.Backlog} (max {d.MaxBacklog})");
			add("latency.us", $"p50 {d.LatencyP50Us:0} / p99 {d.LatencyP99Us:0} / max {d.LatencyMaxUs:0}");
			if (d.SnapshotOverdue) {
				add("snapshot", "overdue");
			}
			if (d.StopOverdue) {
				add("stop", "overdue");
			}
		}

		#endregion
	}
}
