// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VisualPinball.Engine.DOF.Session
{
	public enum DofSessionState
	{
		Created,
		/// <summary>The worker creates and initialises the DOF instance.</summary>
		Starting,
		Running,
		/// <summary>The output gate is closed; events keep flowing to DOF.</summary>
		Paused,
		/// <summary>Something went wrong; the gate is closed and events are rejected until the session is stopped.</summary>
		Faulted,
		Stopping,
		/// <summary>The DOF instance is finished and freed.</summary>
		Stopped,
	}

	public sealed class DofSessionOptions
	{
		/// <summary>The revision folder (<see cref="Configuration.DofRevision.BasePath"/>).</summary>
		public string BasePath { get; set; }
		public string RomName { get; set; }
		/// <summary>The game session this DOF session belongs to; events published for another one are rejected.</summary>
		public int GameSession { get; set; }
		/// <summary>Size of the event ring, a power of two. A full ring faults the session.</summary>
		public int RingCapacity { get; set; } = 32768;
		/// <summary>
		/// Wait for the initial snapshot's boundary (<see cref="DofSession.PublishSnapshotBoundary"/>) before opening the
		/// gate, so outputs never show a partial state. Without a real-time feed nothing sends one: turn it off.
		/// </summary>
		public bool WaitForSnapshot { get; set; } = true;
		/// <summary>
		/// How long the snapshot may take before the session reports it missing (<see cref="DofSessionDiagnostics.SnapshotOverdue"/>
		/// and the log). The gate stays closed until the snapshot is in, however long that takes.
		/// </summary>
		public TimeSpan SnapshotTimeout { get; set; } = TimeSpan.FromSeconds(2);
		/// <summary>
		/// Longest wait for every controller unit to write a gate change, and for DOF to process its input before the
		/// gate opens (after which <see cref="DofSessionDiagnostics.DrainOverdue"/> reports it; the gate stays closed).
		/// </summary>
		public TimeSpan GateTimeout { get; set; } = TimeSpan.FromSeconds(1);
		/// <summary>
		/// How long finishing DOF may take, after the gate timeout, before <see cref="DofSession.StopOverdue"/> reports a
		/// stuck controller. The stop itself waits for DOF however long it takes.
		/// </summary>
		public TimeSpan FinishTimeout { get; set; } = TimeSpan.FromSeconds(5);
		/// <summary>Events DOF may have queued unprocessed before the session faults.</summary>
		public int BacklogLimit { get; set; } = 20000;
		/// <summary>Messages of the session itself (not libdof's log). Called on the worker thread.</summary>
		public Action<DofLogLevel, string> Log { get; set; }
		/// <summary>Creates the DOF instance; tests replace it.</summary>
		public Func<string, IDofNative> NativeFactory { get; set; }
		/// <summary>Tests turn the stop deadline's timer off to see a stop that ends late before the timer fires.</summary>
		internal bool StopDeadlineTimer { get; set; } = true;
	}

	/// <summary>The outcome of a gate change: whether every controller unit wrote it in time.</summary>
	public sealed class DofGateResult
	{
		/// <summary>The state the gate was set to: false if it was closed, or if an opening was held back (see <see cref="Reason"/>).</summary>
		public bool Open { get; }
		/// <summary>Every unit wrote the change or failed, within the deadline.</summary>
		public bool Settled { get; }
		public DofGateState State { get; }
		/// <summary>Units that did not write it (pending or failed), by name. All-off is not confirmed for them.</summary>
		public IReadOnlyList<string> Unconfirmed { get; }
		/// <summary>
		/// Why the gate did not change as asked, or null. A held opening (the snapshot is not in, DOF has not processed
		/// its input yet) happens later on its own; a refused one (stopping, faulted) does not.
		/// </summary>
		public string Reason { get; }

		internal DofGateResult(bool open, bool settled, DofGateState state, IReadOnlyList<string> unconfirmed, string reason = null)
		{
			Open = open;
			Settled = settled;
			State = state;
			Unconfirmed = unconfirmed;
			Reason = reason;
		}

		internal static readonly DofGateResult None = new DofGateResult(false, false, default, Array.Empty<string>(), "The session is not running.");

		internal static DofGateResult NotChanged(string reason) => new DofGateResult(false, false, default, Array.Empty<string>(), reason);

		public override string ToString() => $"{(Open ? "open" : "closed")}, {(Settled ? "settled" : "not settled")} ({State})"
			+ (Unconfirmed.Count > 0 ? ", unconfirmed: " + string.Join(", ", Unconfirmed) : string.Empty)
			+ (Reason != null ? ": " + Reason : string.Empty);
	}

	public sealed class DofStartResult
	{
		public bool Started { get; }
		public string Error { get; }
		public DofCabinetDescription Cabinet { get; }

		internal DofStartResult(bool started, string error, DofCabinetDescription cabinet)
		{
			Started = started;
			Error = error;
			Cabinet = cabinet;
		}
	}

	public sealed class DofStopResult
	{
		/// <summary>DOF finished and was freed. Always true: a stop completes only then.</summary>
		public bool Completed { get; }
		/// <summary>The stop took longer than its deadline (gate plus finish timeout); <see cref="DofSession.StopOverdue"/> was raised.</summary>
		public bool Overdue { get; }
		/// <summary>The all-off before finishing.</summary>
		public DofGateResult Gate { get; }
		/// <summary>An error finishing DOF, or null.</summary>
		public string Error { get; }

		internal DofStopResult(bool completed, DofGateResult gate, string error, bool overdue = false)
		{
			Completed = completed;
			Gate = gate;
			Error = error;
			Overdue = overdue;
		}
	}

	/// <summary>Counters for the diagnostics view. Read from any thread; values may be a moment old.</summary>
	public readonly struct DofSessionDiagnostics
	{
		public DofSessionState State { get; }
		public string Fault { get; }
		public long Published { get; }
		public long Delivered { get; }
		/// <summary>Events dropped because they repeated the element's last value.</summary>
		public long Unchanged { get; }
		/// <summary>Events of another game session, or after stop.</summary>
		public long Rejected { get; }
		public int Backlog { get; }
		public int MaxBacklog { get; }
		/// <summary>Hand-off latency from publication to the return of the DOF call, in microseconds.</summary>
		public double LatencyP50Us { get; }
		public double LatencyP99Us { get; }
		public double LatencyMaxUs { get; }
		/// <summary>The initial snapshot did not arrive within <see cref="DofSessionOptions.SnapshotTimeout"/>; the gate stays closed until it does.</summary>
		public bool SnapshotOverdue { get; }
		/// <summary>DOF did not process its input within <see cref="DofSessionOptions.GateTimeout"/>; the gate stays closed until it has.</summary>
		public bool DrainOverdue { get; }
		/// <summary>The stop passed its deadline and DOF is still finishing (or finished late).</summary>
		public bool StopOverdue { get; }

		internal DofSessionDiagnostics(DofSessionState state, string fault, long published, long delivered, long unchanged, long rejected,
			int backlog, int maxBacklog, double p50, double p99, double max, bool snapshotOverdue, bool drainOverdue, bool stopOverdue)
		{
			State = state;
			Fault = fault;
			Published = published;
			Delivered = delivered;
			Unchanged = unchanged;
			Rejected = rejected;
			Backlog = backlog;
			MaxBacklog = maxBacklog;
			LatencyP50Us = p50;
			LatencyP99Us = p99;
			LatencyMaxUs = max;
			SnapshotOverdue = snapshotOverdue;
			DrainOverdue = drainOverdue;
			StopOverdue = stopOverdue;
		}
	}

	/// <summary>
	/// One game's DOF: a worker thread that owns the DOF instance, creates and initialises it, feeds it the element
	/// changes of the game in order, opens and closes its output gate, and finishes it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The game logic publishes changes from the simulation thread (<see cref="Publish"/>, allocation-free, never
	/// blocks) into a bounded ring; named elements and events come from any thread (<see cref="PublishNamed"/>,
	/// <see cref="PublishEvent"/>). Publishing may start before <see cref="StartAsync"/>, so the initial snapshot is
	/// buffered until DOF is ready.
	/// </para>
	/// <para>
	/// The gate is closed before DOF initialises: the session starts dark, and opens once the snapshot is in and DOF
	/// has processed it. It closes on pause, on a fault and before finishing. A stop, an emergency stop and a fault
	/// latch it closed, in DOF too (<see cref="DofInstance.LatchOutputGate"/>), so an opening DOF has not applied yet
	/// never goes out and none follows. <see cref="StopAsync"/> and <see cref="Completion"/> complete only when DOF has finished
	/// and been freed, however long that takes; until then no other session can start in the process, and the
	/// session's owner keeps what DOF uses (device leases, the configuration). <see cref="StopOverdue"/> tells when
	/// that takes longer than it should.
	/// </para>
	/// </remarks>
	public sealed class DofSession
	{
		private const int BatchSize = 4096;
		private const int HousekeepingMs = 10;

		// _stopOverdue values.
		private const int StopPending = 0;
		private const int StopLate = 1;
		private const int StopDone = 2;

		private readonly DofSessionOptions _options;
		private readonly DofEventRing _ring;
		// Events and named elements; a pulse is one entry, which the worker sends as its value and then 0.
		private readonly ConcurrentQueue<(DofElementKind kind, int number, string name, int value, bool pulse)> _ingress = new ConcurrentQueue<(DofElementKind, int, string, int, bool)>();
		private readonly ConcurrentQueue<Command> _commands = new ConcurrentQueue<Command>();
		private readonly DofEventTranslator _translator = new DofEventTranslator();
		private readonly LatencyHistogram _latency = new LatencyHistogram();
		private readonly TaskCompletionSource<DofStartResult> _started = new TaskCompletionSource<DofStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource<DofStopResult> _completion = new TaskCompletionSource<DofStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		// Serialises the native handle between the worker and EmergencyStop, so the handle is never used after it is freed,
		// and makes the check of the stop latch and the opening of the gate one step.
		private readonly object _nativeLock = new object();
		// Serialises command admission with the worker's shutdown, so no command is queued after the last drain.
		private readonly object _commandLock = new object();

		private Thread _worker;
		private IDofNative _native;
		private int _state = (int)DofSessionState.Created;
		private volatile bool _admitting = true;
		private bool _commandsClosed;
		private int _stopRequested;
		private long _stopRequestedAt;
		private int _stopOverdue;
		private int _overflow;
		// A fault for the worker to apply: from outside (Fault), an overflow or a backlog found during a wait. The first wins.
		private string _pendingFault;
		// The gate must latch closed in DOF (a stop or a fault). Set before the latch is tried, so that the worker latches
		// when the thread that set it found the native handle in use.
		private int _latchWanted;
		// DOF's gate was latched by LatchSoon or the worker.
		private int _nativeLatched;
		private readonly string _overflowReason;
		private int _snapshotOverdue;
		private int _drainOverdue;
		private string _fault;
		private long _published;
		private long _delivered;
		private long _unchanged;
		private long _rejected;
		private int _backlog;
		private int _maxBacklog;

		// Worker state.
		private bool _snapshotDone;
		private bool _wantOpen;
		private bool _gateOpen;
		private long _snapshotDeadline;
		// When the worker started waiting for DOF to process its input before opening; 0 when it does not wait.
		private long _drainSince;

		public DofSession(DofSessionOptions options)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			if (string.IsNullOrEmpty(options.BasePath)) {
				throw new ArgumentException("A configuration folder is required.", nameof(options));
			}
			_ring = new DofEventRing(options.RingCapacity);
			// Made here: the simulation thread reports an overflow without allocating.
			_overflowReason = $"More than {_ring.Capacity} events waited for DOF; the event ring overflowed.";
		}

		public DofSessionState State => (DofSessionState)Volatile.Read(ref _state);
		public int GameSession => _options.GameSession;

		/// <summary>Completes when DOF has finished and been freed (or never started), not before.</summary>
		public Task<DofStopResult> Completion => _completion.Task;

		/// <summary>
		/// Raised once, on a thread-pool thread, when a stop has not completed within its deadline (gate plus finish
		/// timeout): a controller does not stop. The stop keeps waiting; report it, and keep what DOF uses until
		/// <see cref="Completion"/> completes. A stop that completes after its deadline raises it too, if the deadline's
		/// timer did not yet, so it may come after <see cref="Completion"/>.
		/// </summary>
		public event EventHandler StopOverdue;

		/// <summary>A stop passed its deadline before DOF finished; see <see cref="StopOverdue"/>.</summary>
		public bool IsStopOverdue => Volatile.Read(ref _stopOverdue) == StopLate;

		#region Producers

		/// <summary>
		/// Simulation thread: publishes one element change. Never allocates or blocks. Returns false if the event was
		/// rejected (another game session, stopping, or the ring is full, which faults the session).
		/// </summary>
		public bool Publish(int gameSession, DofElementKind kind, int number, int value)
		{
			if (gameSession != _options.GameSession || !_admitting || Volatile.Read(ref _overflow) != 0) {
				Interlocked.Increment(ref _rejected);
				return false;
			}
			if (!_ring.TryWrite(kind, number, value, Stopwatch.GetTimestamp())) {
				// Latched before the worker wakes: from here on the gate does not open, DOF's gate is latched closed, and
				// the worker faults the session.
				Volatile.Write(ref _overflow, 1);
				Interlocked.CompareExchange(ref _pendingFault, _overflowReason, null);
				LatchSoon();
				return false;
			}
			Interlocked.Increment(ref _published);
			return true;
		}

		/// <summary>Simulation thread: the initial snapshot is complete; what follows are changes.</summary>
		public bool PublishSnapshotBoundary(int gameSession) => Publish(gameSession, DofElementKind.SnapshotBoundary, 0, 0);

		/// <summary>
		/// Any thread: a named element (<c>$name</c> in DOF configs, without the dollar sign). The name always addresses
		/// a named element, never a numbered one ("S1" is the element named S1, not solenoid 1). DOF matches names
		/// ignoring case, with spaces as underscores, and ignores a value equal to the element's last one.
		/// </summary>
		public void PublishNamed(string name, int value)
		{
			if (string.IsNullOrEmpty(name) || !_admitting) {
				return;
			}
			_ingress.Enqueue((DofElementKind.Event, 0, name, value, false));
			_ring.Wake();
		}

		/// <summary>
		/// Any thread: turns a named element on and off again, as VPX's <c>DOFPulse</c>. The pulse is one entry, sent to DOF
		/// as 1 and then 0 back to back by the worker, so pulses from several threads never interleave into one (1, 1, 0, 0),
		/// and it is ordered with the other events, a pause or resume included, as they are.
		/// </summary>
		public void PulseNamed(string name)
		{
			if (string.IsNullOrEmpty(name) || !_admitting) {
				return;
			}
			_ingress.Enqueue((DofElementKind.Event, 0, name, 1, true));
			_ring.Wake();
		}

		/// <summary>Any thread: an <c>E</c> event.</summary>
		public void PublishEvent(int number, int value)
		{
			if (!_admitting) {
				return;
			}
			_ingress.Enqueue((DofElementKind.Event, number, null, value, false));
			_ring.Wake();
		}

		/// <summary>Any thread: turns an <c>E</c> event on and off again, as <see cref="PulseNamed"/> does a named element.</summary>
		public void PulseEvent(int number)
		{
			if (!_admitting) {
				return;
			}
			_ingress.Enqueue((DofElementKind.Event, number, null, 1, true));
			_ring.Wake();
		}

		#endregion

		#region Lifecycle

		/// <summary>Starts the worker, which creates and initialises DOF. Completes when DOF runs or failed to start.</summary>
		public Task<DofStartResult> StartAsync()
		{
			if (Interlocked.CompareExchange(ref _state, (int)DofSessionState.Starting, (int)DofSessionState.Created) != (int)DofSessionState.Created) {
				throw new InvalidOperationException("The session was started already.");
			}
			_worker = new Thread(Run) { Name = "DOF session", IsBackground = true };
			_worker.Start();
			return _started.Task;
		}

		/// <summary>Closes the output gate; DOF keeps processing events, so resuming shows the current state.</summary>
		public Task<DofGateResult> PauseAsync() => Post(CommandKind.Pause);

		/// <summary>
		/// Lets DOF process what was published during the pause with the gate still closed, then opens it. Pulses that
		/// ended during the pause do not show. If DOF has not processed it within the gate timeout, the result is held
		/// (<see cref="DofGateResult.Reason"/>) and the gate opens once DOF has.
		/// </summary>
		public Task<DofGateResult> ResumeAsync() => Post(CommandKind.Resume);

		/// <summary>
		/// Stops admission, closes the gate, waits (bounded) for the all-off, and finishes DOF on the worker. Completes
		/// when DOF has finished and been freed, however long that takes: the same task as <see cref="Completion"/>.
		/// <see cref="StopOverdue"/> is raised when that takes longer than the gate plus the finish timeout.
		/// </summary>
		public Task<DofStopResult> StopAsync()
		{
			RequestStop();
			return _completion.Task;
		}

		/// <summary>
		/// Any thread, returns at once: stops admission, latches the stop so the gate never opens again, latches DOF's
		/// gate closed (see <see cref="Fault"/> for how), and lets the worker finish as <see cref="StopAsync"/> does.
		/// </summary>
		public void EmergencyStop()
		{
			// The latch first, before the worker wakes for the stop.
			_admitting = false;
			LatchSoon();
			RequestStop();
		}

		/// <summary>
		/// Any thread, the simulation thread included: faults the session, as an overflow of the event ring does, for
		/// an upstream failure such as an overflow of the feed that publishes here. Rejects events from here on, latches
		/// DOF's gate closed at once (openings DOF has not applied yet apply as closings), and reports
		/// <paramref name="reason"/> through <see cref="DofSessionDiagnostics.Fault"/> and the log. If the worker is
		/// using the DOF instance at this moment, it latches the gate as soon as it lets go. The session stays faulted,
		/// with the gate closed, until it is stopped; stop it as usual (<see cref="StopAsync"/>). Never blocks beyond
		/// short native locks and allocates nothing: pass a preallocated reason. The first fault's reason is kept.
		/// </summary>
		public void Fault(string reason)
		{
			_admitting = false;
			Interlocked.CompareExchange(ref _pendingFault, reason ?? "The session was faulted.", null);
			LatchSoon();
		}

		/// <summary>
		/// Any thread, never waits: latches DOF's gate closed now if the native handle is free; otherwise the worker,
		/// which holds it, latches it when it lets go (<see cref="LatchIfWanted"/>).
		/// </summary>
		private void LatchSoon()
		{
			Interlocked.Exchange(ref _latchWanted, 1);
			if (Monitor.TryEnter(_nativeLock)) {
				try {
					LatchLocked();
				} finally {
					Monitor.Exit(_nativeLock);
				}
			}
			_ring.Wake();
		}

		/// <summary>
		/// Latches DOF's gate, the first time only: later stops and faults find it latched and leave DOF alone. Called
		/// with <see cref="_nativeLock"/> held. Returns the closing's generation, or -1.
		/// </summary>
		private long LatchLocked()
		{
			var native = _native;
			if (native == null || Interlocked.Exchange(ref _nativeLatched, 1) != 0) {
				return -1;
			}
			try {
				return native.LatchOutputGate();
			} catch (Exception) {
				// The worker reports native failures; the threads that latch must not throw.
				return -1;
			}
		}

		/// <summary>Worker: latches DOF's gate if a stop or a fault wants it and no one did yet.</summary>
		private void LatchIfWanted()
		{
			// A full fence: a thread that set the flag and found the lock held is seen after the worker let go of it.
			if (Interlocked.CompareExchange(ref _latchWanted, 1, 1) == 0 || Volatile.Read(ref _nativeLatched) != 0) {
				return;
			}
			lock (_nativeLock) {
				if (Volatile.Read(ref _nativeLatched) == 0) {
					LatchLocked();
				}
			}
		}

		private void RequestStop()
		{
			_admitting = false;
			// Pauses and closings still waiting stop waiting; the final closing latches.
			Interlocked.Exchange(ref _latchWanted, 1);
			if (Interlocked.Exchange(ref _stopRequested, 1) != 0) {
				_ring.Wake();
				return;
			}
			Volatile.Write(ref _stopRequestedAt, Stopwatch.GetTimestamp());
			if (Interlocked.CompareExchange(ref _state, (int)DofSessionState.Stopped, (int)DofSessionState.Created) == (int)DofSessionState.Created) {
				// Never started: nothing to finish.
				Volatile.Write(ref _stopOverdue, StopDone);
				_started.TrySetResult(new DofStartResult(false, "The session was stopped before it started.", null));
				_completion.TrySetResult(new DofStopResult(true, DofGateResult.None, null));
				return;
			}
			lock (_commandLock) {
				if (!_commandsClosed) {
					_commands.Enqueue(new Command(CommandKind.Stop));
				}
			}
			_ring.Wake();
			if (_options.StopDeadlineTimer) {
				Task.Delay(StopDeadline).ContinueWith(_ => MarkStopLate(), TaskScheduler.Default);
			}
		}

		private TimeSpan StopDeadline => _options.GateTimeout + _options.FinishTimeout;

		/// <summary>The stop passed its deadline: whichever finds it first (the timer or the worker) raises <see cref="StopOverdue"/>, once.</summary>
		private void MarkStopLate()
		{
			if (Interlocked.CompareExchange(ref _stopOverdue, StopLate, StopPending) != StopPending) {
				return;
			}
			ThreadPool.QueueUserWorkItem(_ => {
				try {
					StopOverdue?.Invoke(this, EventArgs.Empty);
				} catch {
					// A handler must not break the thread pool.
				}
			});
		}

		private Task<DofGateResult> Post(CommandKind kind)
		{
			var state = State;
			if (state == DofSessionState.Created || state == DofSessionState.Stopping || state == DofSessionState.Stopped) {
				return Task.FromResult(DofGateResult.None);
			}
			// Latched closed by a stop or a fault: nothing to wait for, and a queued command could wait for the latch's own
			// gate timeout.
			if (OpenRefusal() is string refusal) {
				return Task.FromResult(DofGateResult.NotChanged(refusal));
			}
			var command = new Command(kind);
			lock (_commandLock) {
				if (_commandsClosed) {
					return Task.FromResult(DofGateResult.None);
				}
				_commands.Enqueue(command);
			}
			_ring.Wake();
			return command.Result.Task;
		}

		public DofSessionDiagnostics Diagnostics => new DofSessionDiagnostics(State, Volatile.Read(ref _fault) ?? Volatile.Read(ref _pendingFault),
			Interlocked.Read(ref _published), Interlocked.Read(ref _delivered), Interlocked.Read(ref _unchanged),
			Interlocked.Read(ref _rejected), Volatile.Read(ref _backlog), Volatile.Read(ref _maxBacklog),
			_latency.Percentile(0.5), _latency.Percentile(0.99), _latency.Max,
			Volatile.Read(ref _snapshotOverdue) != 0, Volatile.Read(ref _drainOverdue) != 0, IsStopOverdue);

		#endregion

		#region Worker

		private void Run()
		{
			string error = null;
			var gate = DofGateResult.None;
			try {
				if (Initialise()) {
					Loop();
					gate = CloseGate(true, true);
				}
			} catch (Exception e) {
				error = e.Message;
				Log(DofLogLevel.Error, $"The DOF session failed: {e}");
			} finally {
				SetState(DofSessionState.Stopping);
				_admitting = false;
				lock (_commandLock) {
					_commandsClosed = true;
				}
				FailPendingCommands();
				error = Cleanup(error);
				SetState(DofSessionState.Stopped);
				_started.TrySetResult(new DofStartResult(false, error ?? "The session stopped while starting.", null));
				_completion.TrySetResult(new DofStopResult(true, gate, error, FinishedLate()));
			}
		}

		/// <summary>Whether the stop passed its deadline, raising <see cref="StopOverdue"/> if the timer did not yet; from here on the timer does not.</summary>
		private bool FinishedLate()
		{
			var requested = Volatile.Read(ref _stopRequestedAt);
			if (requested != 0 && Stopwatch.GetTimestamp() - requested > Ticks(StopDeadline)) {
				MarkStopLate();
			}
			Interlocked.CompareExchange(ref _stopOverdue, StopDone, StopPending);
			return Volatile.Read(ref _stopOverdue) == StopLate;
		}

		private bool Initialise()
		{
			IDofNative native;
			try {
				native = (_options.NativeFactory ?? DofInstanceNative.Create)(_options.BasePath);
			} catch (Exception e) {
				Fail($"DOF could not be created: {e.Message}");
				return false;
			}
			lock (_nativeLock) {
				_native = native;
			}
			// Dark from the start: whatever Init sets up writes zeros until the game runs.
			native.SetOutputGate(false);
			// A stop or a fault that came before the instance existed.
			LatchIfWanted();
			if (Volatile.Read(ref _stopRequested) != 0) {
				return false;
			}
			try {
				// No table file: libdof then looks up no table XML config, whatever the revision's global config names.
				// It reads a table config's effects but not the effects it assigns to the table elements, so a loaded one
				// leaves the table's effects without a trigger and can drop the ini rows (AddLedControlConfig).
				native.Init(null, _options.RomName);
			} catch (Exception e) {
				Fail($"DOF could not start: {e.Message}");
				return false;
			}
			DofCabinetDescription cabinet = null;
			try {
				cabinet = native.Describe();
			} catch (Exception e) {
				Log(DofLogLevel.Warning, $"Describing the DOF cabinet failed: {e.Message}");
			}
			_wantOpen = true;
			_snapshotDone = !_options.WaitForSnapshot;
			_snapshotDeadline = Stopwatch.GetTimestamp() + Ticks(_options.SnapshotTimeout);
			if (!SetState(DofSessionState.Running, DofSessionState.Starting)) {
				return State != DofSessionState.Stopped;
			}
			_started.TrySetResult(new DofStartResult(true, null, cabinet));
			return true;

			void Fail(string message)
			{
				Volatile.Write(ref _fault, message);
				Log(DofLogLevel.Error, message);
				_started.TrySetResult(new DofStartResult(false, message, null));
			}
		}

		private void Loop()
		{
			var nextHousekeeping = 0L;
			while (true) {
				LatchIfWanted();
				ApplyFault();
				while (_commands.TryDequeue(out var command)) {
					// A stop latches: the commands queued before it are not run (the rest complete when the worker ends).
					if (command.Kind == CommandKind.Stop || Volatile.Read(ref _stopRequested) != 0) {
						command.Result.TrySetResult(DofGateResult.None);
						return;
					}
					try {
						Execute(command);
					} catch (Exception e) {
						command.Result.TrySetResult(DofGateResult.NotChanged($"The DOF session failed: {e.Message}"));
						throw;
					}
				}
				if (Volatile.Read(ref _stopRequested) != 0) {
					return;
				}
				var busy = DrainRing() | DrainIngress();
				var now = Stopwatch.GetTimestamp();
				if (now >= nextHousekeeping) {
					Housekeeping();
					nextHousekeeping = now + Ticks(TimeSpan.FromMilliseconds(HousekeepingMs));
				}
				if (!busy && _commands.IsEmpty) {
					_ring.Wait(HousekeepingMs);
				}
			}
		}

		private void Execute(Command command)
		{
			var state = State;
			switch (command.Kind) {
				case CommandKind.Pause:
					_wantOpen = false;
					_drainSince = 0;
					Volatile.Write(ref _drainOverdue, 0);
					if (state == DofSessionState.Running) {
						SetState(DofSessionState.Paused);
					}
					// Latched closed already (a fault): nothing to wait for.
					command.Result.TrySetResult(OpenRefusal() is string latched ? DofGateResult.NotChanged(latched) : CloseGate(false));
					break;
				case CommandKind.Resume:
					if (OpenRefusal() is string refused) {
						command.Result.TrySetResult(DofGateResult.NotChanged(refused));
						break;
					}
					// What happened during the pause reaches DOF first, with the gate still closed.
					DrainAll();
					_wantOpen = true;
					SetState(DofSessionState.Running);
					command.Result.TrySetResult(TryOpen(true));
					break;
			}
		}

		private void Housekeeping()
		{
			if (OverBacklog()) {
				ApplyFault();
			}
			if (!_snapshotDone && Stopwatch.GetTimestamp() > _snapshotDeadline && Interlocked.Exchange(ref _snapshotOverdue, 1) == 0) {
				Log(DofLogLevel.Warning, $"The game's initial state did not arrive within {_options.SnapshotTimeout.TotalSeconds:0.#} s; the outputs stay off until it does.");
			}
			if (_wantOpen && !_gateOpen && State == DofSessionState.Running) {
				TryOpen(false);
			}
		}

		/// <summary>
		/// Samples DOF's backlog. Over the limit, it faults the session (applied by <see cref="ApplyFault"/>) and returns
		/// true. Called between batches wherever the worker forwards events, so a backlog cannot grow unchecked while it
		/// waits for a slow controller.
		/// </summary>
		private bool OverBacklog() => OverBacklog(_native.PendingInput);

		/// <summary>Checks a sample of DOF's backlog, as <see cref="OverBacklog()"/> does.</summary>
		private bool OverBacklog(int backlog)
		{
			Volatile.Write(ref _backlog, backlog);
			if (backlog > _maxBacklog) {
				Volatile.Write(ref _maxBacklog, backlog);
			}
			if (backlog <= _options.BacklogLimit) {
				return false;
			}
			if (Volatile.Read(ref _pendingFault) == null) {
				Fault($"DOF has {backlog} unprocessed events, more than {_options.BacklogLimit}: it cannot keep up with the game.");
			}
			return true;
		}

		/// <summary>Worker: applies a fault reported by <see cref="Fault"/>, an overflow or the backlog, once.</summary>
		private void ApplyFault()
		{
			if (State == DofSessionState.Faulted || !(Volatile.Read(ref _pendingFault) is string reason)) {
				return;
			}
			Volatile.Write(ref _fault, reason);
			Log(DofLogLevel.Error, reason);
			SetState(DofSessionState.Faulted);
			_wantOpen = false;
			_admitting = false;
			CloseGate(false, true);
		}

		/// <summary>Why the gate must not open any more, or null: a stop, an overflow or a fault latches it closed.</summary>
		private string OpenRefusal()
		{
			if (Volatile.Read(ref _stopRequested) != 0) {
				return "The session is stopping.";
			}
			if (Volatile.Read(ref _overflow) != 0 || State == DofSessionState.Faulted || Volatile.Read(ref _pendingFault) != null) {
				return "The session faulted" + ((Volatile.Read(ref _fault) ?? Volatile.Read(ref _pendingFault)) is string fault ? ": " + fault : ".");
			}
			// An emergency stop latches before it asks the worker to stop.
			if (Volatile.Read(ref _latchWanted) != 0) {
				return "The gate latches closed.";
			}
			return null;
		}

		/// <summary>
		/// Opens the gate if nothing holds it: the snapshot is in and DOF has processed its input. Otherwise reports why,
		/// and the housekeeping tries again.
		/// </summary>
		/// <param name="wait">Wait (bounded by the gate timeout) for DOF to process its input.</param>
		private DofGateResult TryOpen(bool wait)
		{
			var refusal = OpenRefusal();
			if (refusal != null) {
				return DofGateResult.NotChanged(refusal);
			}
			if (!_snapshotDone) {
				return DofGateResult.NotChanged("The game's initial state has not arrived yet; the gate opens when it has.");
			}
			if (!Drained(wait)) {
				refusal = OpenRefusal();
				return DofGateResult.NotChanged(refusal ?? "DOF has not processed its input yet; the gate opens when it has.");
			}
			return OpenGate();
		}

		/// <summary>
		/// Whether DOF has processed everything it was given. libdof runs its commands, the gate change among them,
		/// before it takes more input, so the gate opens only when its input queue is empty.
		/// </summary>
		private bool Drained(bool wait)
		{
			if (_drainSince == 0) {
				_drainSince = Stopwatch.GetTimestamp();
			}
			var deadline = _drainSince + Ticks(_options.GateTimeout);
			while (true) {
				// One sample per pass, checked against the backlog limit before anything ends the wait: whether it was
				// over when the wait started or the ring added to it since, it faults even on the pass that drains.
				var pending = _native.PendingInput;
				if (OverBacklog(pending)) {
					return false;
				}
				if (pending == 0) {
					_drainSince = 0;
					Volatile.Write(ref _drainOverdue, 0);
					return true;
				}
				if (Stopwatch.GetTimestamp() >= deadline) {
					if (Interlocked.Exchange(ref _drainOverdue, 1) == 0) {
						Log(DofLogLevel.Warning, $"DOF did not process its input within {_options.GateTimeout.TotalSeconds:0.#} s ({pending} events wait); the outputs stay off until it has.");
					}
					return false;
				}
				if (!wait || OpenRefusal() != null) {
					return false;
				}
				// Keep the ring moving meanwhile.
				if (!DrainRing()) {
					Thread.Sleep(1);
				}
			}
		}

		/// <summary>Delivers up to one batch of ring entries. Returns whether there were any.</summary>
		private bool DrainRing()
		{
			var any = false;
			for (var i = 0; i < BatchSize && _ring.TryRead(out var entry); i++) {
				any = true;
				if (entry.Kind == DofElementKind.SnapshotBoundary) {
					_snapshotDone = true;
					Volatile.Write(ref _snapshotOverdue, 0);
					continue;
				}
				if (Dropping) {
					continue;
				}
				var value = entry.Value;
				if (!_translator.Translate(entry.Kind, entry.Number, ref value)) {
					Interlocked.Increment(ref _unchanged);
					continue;
				}
				_native.Data(DofEventTranslator.TypeOf(entry.Kind), entry.Number, value);
				_latency.Record(Stopwatch.GetTimestamp() - entry.Timestamp);
				Interlocked.Increment(ref _delivered);
			}
			return any;
		}

		private bool DrainIngress()
		{
			var any = false;
			for (var i = 0; i < BatchSize && _ingress.TryDequeue(out var item); i++) {
				any = true;
				if (Dropping) {
					continue;
				}
				Deliver(item.kind, item.number, item.name, item.value);
				if (item.pulse) {
					Deliver(item.kind, item.number, item.name, 0);
				}
			}
			return any;
		}

		private void Deliver(DofElementKind kind, int number, string name, int value)
		{
			if (name != null) {
				// Not filtered here: DOF compares names its own way (ignoring case, spaces as underscores) and drops
				// repeated values itself, so a filter that compared them differently could drop an off.
				_native.DataNamed(name, value);
			} else {
				if (!_translator.Translate(kind, number, ref value)) {
					Interlocked.Increment(ref _unchanged);
					return;
				}
				_native.Data(DofEventTranslator.TypeOf(kind), number, value);
			}
			Interlocked.Increment(ref _delivered);
		}

		/// <summary>A faulted session, or one about to be, delivers nothing more.</summary>
		private bool Dropping => State == DofSessionState.Faulted || Volatile.Read(ref _pendingFault) != null;

		private void DrainAll()
		{
			while ((DrainRing() | DrainIngress()) && !OverBacklog()) {
			}
		}

		private DofGateResult OpenGate()
		{
			var result = ChangeGate(true);
			if (result.Reason != null) {
				// Refused, or a stop or a fault wants the gate latched: latch it now if no one did.
				LatchIfWanted();
				return result;
			}
			_gateOpen = true;
			// A stop or a fault came while the units wrote the opening: DOF's gate is latched closed (the opening applied
			// as a closing if DOF had not applied it yet), and the stop or the fault waits for the all-off.
			var refusal = OpenRefusal();
			if (refusal != null) {
				_gateOpen = false;
				LatchIfWanted();
				return DofGateResult.NotChanged(refusal);
			}
			return result;
		}

		/// <param name="final">Before finishing, for the log.</param>
		/// <param name="latch">Latch DOF's gate closed (a stop or a fault) instead of closing it.</param>
		private DofGateResult CloseGate(bool final, bool latch = false)
		{
			_gateOpen = false;
			var result = ChangeGate(false, latch);
			// A closing that stopped waiting because the gate latches is reported by the latch's own closing.
			if (!result.Settled && result.Reason == null) {
				Log(DofLogLevel.Warning, $"Not every DOF controller confirmed all outputs off{(final ? " before finishing" : string.Empty)}: {result}");
			}
			return result;
		}

		/// <summary>
		/// Changes the gate and waits (bounded) for every unit to write it, delivering events meanwhile. An opening is
		/// refused once a stop or a fault latched, checked under the same lock as the change. Every wait but the latch's
		/// own ends when a stop or a fault wants the gate latched.
		/// </summary>
		private DofGateResult ChangeGate(bool open, bool latch = false)
		{
			long generation;
			lock (_nativeLock) {
				if (open) {
					var refusal = OpenRefusal();
					if (refusal != null) {
						return DofGateResult.NotChanged(refusal);
					}
				}
				if (latch) {
					Volatile.Write(ref _nativeLatched, 1);
					generation = _native.LatchOutputGate();
				} else {
					generation = _native.SetOutputGate(open);
				}
			}
			// A stop or a fault that found the handle in use while this changed the gate.
			LatchIfWanted();
			if (generation < 0) {
				return open ? DofGateResult.NotChanged("DOF did not take the change.") : new DofGateResult(false, false, default, Array.Empty<string>());
			}
			var deadline = Stopwatch.GetTimestamp() + Ticks(_options.GateTimeout);
			DofGateState state;
			string reason = null;
			while (true) {
				// Not past the backlog limit, checked on every pass before anything ends the wait, whether it was over
				// when the wait started or the ring added to it since: that faults, and so latches, which ends this wait.
				var overBacklog = OverBacklog(_native.PendingInput);
				state = _native.GetGateState(generation);
				if (overBacklog && !latch) {
					reason = OpenRefusal() ?? "The gate latches closed.";
					break;
				}
				// Superseded: a latch changed the gate meanwhile; this change will never settle.
				if (state.Settled || state.Superseded || Stopwatch.GetTimestamp() >= deadline) {
					break;
				}
				if (!latch && Interlocked.CompareExchange(ref _latchWanted, 1, 1) != 0) {
					// The gate latches closed: the stop or the fault waits for that instead.
					reason = OpenRefusal() ?? "The gate latches closed.";
					break;
				}
				// Keep the ring moving while the units write.
				if (!DrainRing()) {
					Thread.Sleep(1);
				}
			}
			var unconfirmed = state.Settled && state.Failed == 0 ? (IReadOnlyList<string>)Array.Empty<string>() : _native.GetGatePending(generation);
			return new DofGateResult(open && reason == null, state.Settled && state.Failed == 0, state, unconfirmed, reason);
		}

		private string Cleanup(string error)
		{
			IDofNative native;
			lock (_nativeLock) {
				native = _native;
			}
			if (native == null) {
				return error;
			}
			var watch = Stopwatch.StartNew();
			try {
				native.Finish();
			} catch (Exception e) {
				error = error ?? $"Finishing DOF failed: {e.Message}";
				Log(DofLogLevel.Warning, $"Finishing DOF failed: {e.Message}");
			}
			if (watch.Elapsed > _options.FinishTimeout) {
				Log(DofLogLevel.Warning, $"Finishing DOF took {watch.Elapsed.TotalSeconds:0.0} s; a controller was slow to stop.");
			}
			lock (_nativeLock) {
				try {
					native.Dispose();
				} catch (Exception e) {
					error = error ?? $"Freeing DOF failed: {e.Message}";
				}
				_native = null;
			}
			return error;
		}

		private void FailPendingCommands()
		{
			while (_commands.TryDequeue(out var command)) {
				command.Result.TrySetResult(DofGateResult.None);
			}
		}

		private bool SetState(DofSessionState state, DofSessionState? expected = null)
		{
			while (true) {
				var current = Volatile.Read(ref _state);
				if (current == (int)DofSessionState.Stopped || (expected.HasValue && current != (int)expected.Value)) {
					return false;
				}
				// A fault sticks until the session stops.
				if (current == (int)DofSessionState.Faulted && state != DofSessionState.Stopping && state != DofSessionState.Stopped) {
					return false;
				}
				if (Interlocked.CompareExchange(ref _state, (int)state, current) == current) {
					return true;
				}
			}
		}

		private void Log(DofLogLevel level, string message)
		{
			try {
				_options.Log?.Invoke(level, message);
			} catch {
				// A log sink must not break the session.
			}
		}

		private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

		#endregion

		private enum CommandKind
		{
			Pause,
			Resume,
			Stop,
		}

		private sealed class Command
		{
			public readonly CommandKind Kind;
			public readonly TaskCompletionSource<DofGateResult> Result = new TaskCompletionSource<DofGateResult>(TaskCreationOptions.RunContinuationsAsynchronously);

			public Command(CommandKind kind) => Kind = kind;
		}

		/// <summary>Hand-off latencies in 10 µs buckets up to 20 ms, recorded by the worker only.</summary>
		private sealed class LatencyHistogram
		{
			private const double BucketUs = 10;
			private readonly long[] _buckets = new long[2001];
			private long _count;
			private long _maxTicks;

			public void Record(long ticks)
			{
				var us = ticks * 1_000_000.0 / Stopwatch.Frequency;
				var bucket = (int)Math.Min(_buckets.Length - 1, Math.Max(0, us / BucketUs));
				Interlocked.Increment(ref _buckets[bucket]);
				Interlocked.Increment(ref _count);
				if (ticks > Volatile.Read(ref _maxTicks)) {
					Volatile.Write(ref _maxTicks, ticks);
				}
			}

			public double Max => Volatile.Read(ref _maxTicks) * 1_000_000.0 / Stopwatch.Frequency;

			public double Percentile(double p)
			{
				var count = Interlocked.Read(ref _count);
				if (count == 0) {
					return 0;
				}
				var target = (long)Math.Ceiling(count * p);
				long seen = 0;
				for (var i = 0; i < _buckets.Length; i++) {
					seen += Interlocked.Read(ref _buckets[i]);
					if (seen >= target) {
						return (i + 1) * BucketUs;
					}
				}
				return _buckets.Length * BucketUs;
			}
		}
	}
}
