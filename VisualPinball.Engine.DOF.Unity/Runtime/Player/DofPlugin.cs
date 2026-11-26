// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualPinball.Engine.DOF.Configuration;
using VisualPinball.Engine.DOF.Session;
using VisualPinball.Engine.DOF.Unity;
using VisualPinball.Plugins;
using VisualPinball.Unity;

namespace VisualPinball.Engine.DOF.Player
{
	/// <summary>
	/// Runs DOF for each game: one <see cref="DofSession"/> fed from the game's real-time outputs, with the cabinet
	/// triggers published for the table and other plugins.
	/// </summary>
	/// <remarks>
	/// <para>
	/// What DOF uses (the configuration revision, the device leases, the process's single DOF instance) is held from
	/// prepare until the session has finished and freed DOF, which <see cref="Quiescence"/> tells. A stop waits for that
	/// as long as it takes; when it takes longer than the host allows, the host emergency-stops the plugin and keeps it
	/// quarantined until then.
	/// </para>
	/// <para>
	/// The output gate follows the game: closed until DOF has processed the game's initial state, closed while paused,
	/// closed for good on a stop, and latched closed when the game's outputs fail or go away.
	/// </para>
	/// <para>
	/// What drives DOF during the game (the feed subscription, the published triggers, the table's trigger components)
	/// lives in one scope that ends when the game stops, and earlier when DOF does not start, so nothing keeps sending
	/// into a session that does not run.
	/// </para>
	/// </remarks>
	internal sealed class DofPlugin : PlayerPlugin, IPluginEmergencyStop, IPluginDeferredCleanup, IPluginStatusSource, IDiagnosticsSource, IPluginTick
	{
		private static readonly TimeSpan OwnershipTimeout = TimeSpan.FromSeconds(5);
		// The game's initial state comes once the ROM has initialised, which can take a few seconds.
		private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(15);
		// How often the pause menu's state is compared, and how often the live counters are written to the diagnostics.
		private const double StateInterval = 0.25;
		private const double CounterInterval = 5;

		private readonly PluginApplicationContext _application;
		private readonly DofPluginFactory _factory;

		private DofGameSession _session;
		private DofNativeHold _hold;
		private ResourceScope _activity;
		private IDisposable _triggers;
		private readonly List<string> _leaseKeys = new List<string>();
		private volatile string _status = "No game is running.";
		private volatile string _cabinet;
		private volatile bool _ending;
		private string _revision;
		private string _rom;
		private string _romProblem;
		private string _tableConfig;
		private string _feed = "none";
		private int _boundTriggers;

		private bool _diagnostics;
		private string _publishedState;
		private double _sinceState;
		private double _sinceCounters;

		public Task Quiescence { get; private set; } = Task.CompletedTask;

		internal DofPlugin(PluginApplicationContext application, DofPluginFactory factory)
		{
			_application = application;
			_factory = factory;
		}

		/// <summary>The running game's session, for tests.</summary>
		internal DofGameSession Session => _session;

		internal IReadOnlyList<string> LeaseKeys => _leaseKeys;

		/// <summary>Whether DOF follows the game's real-time outputs, rather than the table's triggers only.</summary>
		internal bool FollowsFeed => _feed == RealtimeFeed;

		private const string RealtimeFeed = "real-time outputs";
		private const string StoppingOverdue = "Stopping, overdue: a controller does not finish";

		#region Lifecycle

		public override async Task PrepareGameAsync(PluginGameContext game, CancellationToken cancellation)
		{
			_session = null;
			_hold = null;
			_activity = null;
			_triggers = null;
			_cabinet = null;
			_ending = false;
			_revision = _rom = _romProblem = _tableConfig = null;
			_leaseKeys.Clear();
			_feed = "none";
			_boundTriggers = 0;
			Quiescence = Task.CompletedTask;

			var lease = _factory.Store.Acquire();
			if (lease == null) {
				_status = "No DOF configuration is imported. Import one in the settings.";
				_application.Logger.Info("No DOF configuration is imported; DOF is off.");
				return;
			}
			var hold = new DofNativeHold(e => _application.Logger.Warn("Releasing what DOF used failed.", e));
			hold.Add(lease);
			DofGameSession session = null;
			ResourceScope activity = null;
			try {
				var revision = lease.Revision;
				var catalog = _factory.CatalogOf(revision);
				_revision = revision.Id;

				// What the game runs with. Everything that touches the scene happens before the first wait.
				var scene = game.Capabilities.Find<IUnityGameScene>(UnityPluginCapability.GameScene);
				var components = scene?.Table != null ? scene.Table.GetComponentsInChildren<DofTriggerComponent>(true) : Array.Empty<DofTriggerComponent>();
				var lamps = LampsOf(scene);
				var gameName = components.Select(c => c.GameName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
				var names = DofGameSetup.TableNames(game.PackagePath, game.Title).ToArray();
				_tableConfig = DofGameSetup.ResolveTableConfig(game.Settings.String("TableConfig", DofGameSetup.Auto), catalog.TableConfigs, names);
				var tableFile = revision.TableFileFor(_tableConfig, game.Title);
				// After the table config, whose effects respond without rows.
				(_rom, _romProblem) = DofGameSetup.ResolveRom(game.Settings.String("Rom", DofGameSetup.Auto), game.Identity, RunningRom(scene, game.Identity), gameName, catalog,
					_tableConfig);

				// One DOF instance per process: a previous game's may still be finishing.
				var ownership = DofOwnership.TryAcquire();
				if (ownership == null) {
					ownership = await DofOwnership.AcquireAsync(OwnershipTimeout, cancellation).ConfigureAwait(false);
					await _application.Scheduler.Switch();
				}
				if (ownership == null) {
					Off("The previous game's DOF is still finishing.");
					hold.Dispose();
					return;
				}
				hold.Add(ownership);

				if (!Reserve(game, revision, catalog, hold)) {
					hold.Dispose();
					return;
				}

				// The feed is bound before the session exists, so whether DOF waits for the game's initial state follows from
				// a source DOF can actually follow, not from one merely being there.
				activity = game.Lifetime.Own(new ResourceScope());
				var selection = new PluginResourceSelection(ResourceKind.RealtimeOutputs) { Controller = game.ControllerId, Session = game.Session };
				var outputs = activity.Own(game.Resources.Bind<IPluginRealtimeOutputSource>(selection));
				var feed = FeedOf(outputs);
				session = new DofGameSession(new DofSessionOptions {
					BasePath = revision.BasePath,
					TableFile = tableFile,
					RomName = _rom,
					GameSession = unchecked((int)game.Session),
					// Without a feed only the triggers drive DOF; there is no initial state to wait for.
					WaitForSnapshot = feed != null,
					SnapshotTimeout = SnapshotTimeout,
					Log = SessionLog,
					NativeFactory = _factory.NativeFactory,
				});
				var stopping = session;
				session.StopOverdue += (_, __) => {
					// Raised once, on a pool thread, and possibly after DOF finished anyway.
					if (stopping.Completion.IsCompleted) {
						_application.Logger.Info("DOF took longer than expected to finish.");
					} else {
						_application.Logger.Warn("DOF has not finished within its deadline; a controller may not respond. Its devices stay reserved until it does.");
						_application.Scheduler.Post(() => {
							if (_session == stopping && !stopping.Completion.IsCompleted) {
								_status = StoppingOverdue;
								PublishDiagnostics(true);
							}
						});
					}
				};

				if (feed != null) {
					activity.Own(outputs.Connect(source => Subscribe(source, session)));
				} else {
					outputs.Dispose();
					_application.Logger.Info($"DOF follows the table's triggers only: {_feed}.");
				}

				_triggers = activity.Own(game.Capabilities.Publish<IPluginCabinetTriggers>(CabinetTriggerCapability.Id, session));
				// PinMAME reads 1 and 01 as one element and sends either, as the table maps them.
				var numericIds = game.Identity.Provider == "pinmame";
				// All components in one binding, so each report reaches every component's triggers together.
				var binding = activity.Own(DofTriggerComponent.BindAll(components, session, scene?.Gamelogic, lamps, numericIds));
				_boundTriggers = binding.Count;

				_session = session;
				_hold = hold;
				_activity = activity;
				Quiescence = ReleaseWhenFinished(session, hold);
				game.Lifetime.Own(new Subscription(EmergencyStop));
				_status = "Ready";
				_application.Logger.Info($"DOF for {_rom ?? "no ROM"}{(_tableConfig != null ? ", table config " + _tableConfig : "")}, revision {_revision}"
					+ (_leaseKeys.Count > 0 ? ", reserved " + string.Join(", ", _leaseKeys) : "") + ".");
				if (_romProblem != null) {
					_application.Logger.Warn(_romProblem);
				}
				_diagnostics = true;
				PublishDiagnostics(true);
			} catch {
				// Never started: the stop completes at once and nothing waits for DOF.
				_ending = true;
				session?.EmergencyStop();
				_triggers = null;
				_session = null;
				_activity = null;
				DisposeQuietly(activity);
				hold.Dispose();
				throw;
			}
		}

		private bool Reserve(PluginGameContext game, DofRevision revision, DofCatalog catalog, DofNativeHold hold)
		{
			var leases = game.Capabilities.Find<PluginOutputLeases>(UnityPluginCapability.Outputs);
			if (leases == null) {
				_application.Logger.Warn("The player offers no device leases; DOF opens its devices without reserving them.");
				return true;
			}
			foreach (var key in DofGameSetup.LeaseKeys(revision, catalog)) {
				try {
					hold.Add(leases.Acquire(key, _application.Plugin.Id));
					_leaseKeys.Add(key);
				} catch (InvalidOperationException) {
					Off($"{DofGameSetup.Describe(key)} is in use by {leases.Owner(key) ?? "another plugin"}.");
					return false;
				}
			}
			return true;
		}

		/// <summary>The bound source DOF can follow, or null; says why not in <see cref="_feed"/>.</summary>
		private IPluginRealtimeOutputSource FeedOf(PluginResourceBinding<IPluginRealtimeOutputSource> outputs)
		{
			if (outputs.State != ResourceBindingState.Bound || outputs.Source == null) {
				_feed = outputs.State == ResourceBindingState.Blocked ? "none (" + outputs.Diagnostic + ")" : "none (the game reports no real-time outputs)";
				return null;
			}
			if (outputs.Source.Numbering != RealtimeOutputNumbering.PinMame) {
				_feed = $"none (the game's real-time outputs use the {outputs.Source.Numbering} numbering, which DOF does not know)";
				return null;
			}
			_feed = RealtimeFeed;
			return outputs.Source;
		}

		private IDisposable Subscribe(IPluginRealtimeOutputSource source, DofGameSession session)
		{
			if (source.Numbering != RealtimeOutputNumbering.PinMame) {
				// A replacement DOF cannot follow; the source it replaced already faulted the session when it went.
				return new Subscription(() => { });
			}
			var subscription = source.Subscribe(session.Receiver);
			return new Subscription(() => {
				subscription.Dispose();
				// The binding lets go of a source that went away or was replaced, and of every source when the game ends.
				// Only the first means DOF no longer follows the game: what it shows now would stay as it is.
				if (!_ending) {
					_application.Logger.Warn(DofGameSession.FeedWithdrawn + " The outputs are off for the rest of the game.");
					session.FeedFault(DofGameSession.FeedWithdrawn);
				}
			});
		}

		private Task ReleaseWhenFinished(DofGameSession session, DofNativeHold hold)
			=> session.Completion.ContinueWith(_ => hold.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

		public override async Task StartGameAsync(CancellationToken cancellation)
		{
			var session = _session;
			if (session == null) {
				return;
			}
			_status = "Starting";
			// Published before the wait: a start that hangs in a controller shows as starting, not as ready.
			PublishDiagnostics(true);
			var (error, cabinet) = await WithCancellation(session.StartAsync(), cancellation).ConfigureAwait(false);
			await _application.Scheduler.Switch();
			if (error != null) {
				_status = "DOF did not start: " + error;
				_application.Logger.Warn("DOF did not start: " + error);
				// The session has ended: PinUP and the table must not keep sending triggers into it, nor the feed keep a
				// receiver that drops everything.
				EndActivity();
				PublishDiagnostics(true);
				return;
			}
			_cabinet = cabinet;
			_status = "Running";
			_application.Logger.Info("DOF runs" + (cabinet != null ? ": " + cabinet : "") + ".");
			PublishDiagnostics(true);
		}

		public override void PauseChanged(bool paused)
		{
			var session = _session;
			if (session != null && !_ending) {
				_ = ReportGate(session, paused);
			}
		}

		private async Task ReportGate(DofGameSession session, bool paused)
		{
			try {
				var (settled, message) = await session.SetPausedAsync(paused).ConfigureAwait(false);
				if (settled) {
					_application.Logger.Debug((paused ? "Paused: " : "Resumed: ") + message);
				} else {
					_application.Logger.Warn((paused ? "Turning the outputs off for the pause: " : "Turning the outputs back on: ") + message);
				}
			} catch (Exception e) {
				_application.Logger.Warn("Changing the DOF output gate failed.", e);
			}
		}

		public override async Task StopGameAsync(CancellationToken cancellation)
		{
			var session = _session;
			EndActivity();
			if (session == null) {
				_hold?.Dispose();
				return;
			}
			_status = "Stopping";
			// Published before the wait, as the start is; an overdue stop is published when the session reports it.
			PublishDiagnostics(true);
			try {
				// Completes only when DOF has finished and been freed, which is what the stop is for. If that takes longer
				// than the host waits, it emergency-stops the plugin and waits for Quiescence; what DOF uses is released
				// then, not before.
				var problem = await session.StopAsync().ConfigureAwait(false);
				if (problem != null) {
					_application.Logger.Warn("Stopping DOF: " + problem);
				}
				_status = "Stopped";
			} finally {
				await _application.Scheduler.Switch();
				_diagnostics = false;
				DiagnosticsContext.Remove(DiagnosticsSection);
			}
		}

		public void EmergencyStop()
		{
			EndActivity();
			var session = _session;
			if (session != null) {
				session.EmergencyStop();
			} else {
				_hold?.Dispose();
			}
		}

		public override void Dispose() => EmergencyStop();

		/// <summary>Ends what drives DOF: the triggers are withdrawn, the feed unsubscribed, the table's components unbound.</summary>
		private void EndActivity()
		{
			_ending = true;
			var activity = _activity;
			_activity = null;
			_triggers = null;
			DisposeQuietly(activity);
		}

		private void DisposeQuietly(ResourceScope scope)
		{
			try {
				scope?.Dispose();
			} catch (Exception e) {
				_application.Logger.Warn("Ending DOF's bindings failed.", e);
			}
		}

		private void Off(string reason)
		{
			_status = reason + " DOF is off for this game.";
			_application.Logger.Warn(_status);
		}

		private void SessionLog(DofLogLevel level, string message)
		{
			switch (level) {
				case DofLogLevel.Error: _application.Logger.Write(PluginLogLevel.Error, message); break;
				case DofLogLevel.Warning: _application.Logger.Warn(message); break;
				case DofLogLevel.Debug: _application.Logger.Debug(message); break;
				default: _application.Logger.Info(message); break;
			}
		}

		/// <summary>The ROM the gamelogic engine plays, which PinMAME prepares first; null for other engines.</summary>
		private static string RunningRom(IUnityGameScene scene, GameIdentity game)
		{
			if (game.Provider != "pinmame" || scene?.Table == null) {
				return null;
			}
			var rom = scene.Table.GetComponentInChildren<IRomNameProvider>(true)?.RomName?.Trim().ToLowerInvariant();
			return string.IsNullOrEmpty(rom) || rom != Path.GetFileName(rom) || rom == "." || rom == ".." ? null : rom;
		}

		/// <summary>The table's lamps, which report every lamp change whichever path it takes; null without a player.</summary>
		private static LampPlayer LampsOf(IUnityGameScene scene)
		{
			var player = (scene?.Gamelogic as UnityEngine.Component)?.GetComponent<global::VisualPinball.Unity.Player>();
			if (player == null && scene?.Table != null) {
				player = scene.Table.GetComponentInChildren<global::VisualPinball.Unity.Player>(true);
			}
			return player != null ? player.LampPlayer : null;
		}

		private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellation)
		{
			if (!cancellation.CanBeCanceled || task.IsCompleted) {
				return await task.ConfigureAwait(false);
			}
			var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellation.Register(() => cancelled.TrySetResult(true))) {
				if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) {
					throw new OperationCanceledException(cancellation);
				}
			}
			return await task.ConfigureAwait(false);
		}

		#endregion

		#region Status

		/// <summary>What the pause menu shows as DOF's state.</summary>
		private string State
		{
			get {
				var session = _session;
				return session != null && _status == "Running" ? session.State : _status;
			}
		}

		public void DescribeStatus(PluginStatusWriter writer)
		{
			writer.Add("DOF", State);
			if (_rom != null || _revision != null) {
				writer.Add("Game name", _rom ?? "none");
			}
			if (_romProblem != null) {
				writer.Add("Rows", _romProblem);
			}
			if (_tableConfig != null) {
				writer.Add("Table config", _tableConfig);
			}
			var cabinet = _cabinet;
			if (cabinet != null) {
				writer.Add("Cabinet", cabinet);
			}
		}

		/// <summary>
		/// Owner thread, every frame of a running game: rewrites the diagnostics section when DOF's state changes (a fault
		/// on the simulation thread included), and refreshes its live counters now and then, without logging them.
		/// </summary>
		public void Tick(double unscaledSeconds)
		{
			if (!_diagnostics) {
				return;
			}
			_sinceCounters += unscaledSeconds;
			_sinceState += unscaledSeconds;
			if (_sinceState < StateInterval) {
				return;
			}
			_sinceState = 0;
			if (State != _publishedState) {
				PublishDiagnostics(true);
			} else if (_sinceCounters >= CounterInterval) {
				PublishDiagnostics(false);
			}
		}

		/// <summary>Owner thread: writes the diagnostics section, logging it on lifecycle changes.</summary>
		private void PublishDiagnostics(bool log)
		{
			if (!_diagnostics) {
				return;
			}
			_publishedState = State;
			_sinceCounters = 0;
			DiagnosticsContext.Set(this, log);
		}

		public string DiagnosticsSection => "DOF";

		public void Describe(DiagnosticsWriter writer)
		{
			writer.Add("plugin", DofPluginFactory.Id);
			writer.Add("status", State);
			writer.Add("revision", _revision ?? "none");
			writer.Add("rom", _rom ?? "none");
			if (_romProblem != null) {
				writer.Add("rom.problem", _romProblem);
			}
			writer.Add("table config", _tableConfig ?? "none");
			writer.Add("feed", _feed);
			writer.Add("triggers", _boundTriggers.ToString());
			writer.Add("leases", _leaseKeys.Count > 0 ? string.Join(", ", _leaseKeys) : "none");
			if (_cabinet != null) {
				writer.Add("cabinet", _cabinet);
			}
			_session?.Describe((key, value) => writer.Add(key, value));
		}

		#endregion
	}
}
