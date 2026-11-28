// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using UnityEngine;
using VisualPinball.Engine.DOF.Configuration;
using VisualPinball.Engine.DOF.Detection;
using VisualPinball.Plugins;
using VisualPinball.Unity;
using Logger = NLog.Logger;

namespace VisualPinball.Engine.DOF.Player
{
	/// <summary>
	/// The DOF plugin: imports DOF configurations, runs DOF for each game and publishes the cabinet triggers, and
	/// describes the settings the Player renders.
	/// </summary>
	internal sealed class DofPluginFactory : IPlayerPluginFactory, IPluginSettingsProvider, IPluginSettingsInfoProvider, IPluginSystemDefaults
	{
		internal const string Id = "org.visualpinball.dof";
		private const string PinMameId = "org.visualpinball.pinmame";
		private const string PinUpId = "org.visualpinball.pinup";
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
		private static readonly Logger LibdofLogger = LogManager.GetLogger("DOF.libdof");
		private static int _libdofLog;
		private static int _libdofLogCalls;
		private static volatile bool _libdofLogClosed;

		public PluginDescriptor Descriptor { get; } = new PluginDescriptor(Id, "DOF",
			"Drives your cabinet's toys, such as flashers, contactors, shakers and LED strips, with a configuration from the DOF Config Tool.") {
			EnabledByDefault = true, Category = "Outputs", Order = 20,
			// PinMAME decides which ROM plays. PinUP's packs trigger DOF events from their first screens, so the triggers
			// are there before PinUP prepares.
			After = new[] { PinMameId }, Before = new[] { PinUpId },
			Provides = new[] { CabinetTriggerCapability.Id },
			// VpeDof is built for Windows x64, Linux x64 and macOS (arm64 and x86_64).
			Platforms = new[] { "windows", "linux", "macos" },
		};

		internal static readonly PluginSettingsSchema SettingsSchema = BuildSchema();
		public PluginSettingsSchema Schema => SettingsSchema;
		public IPluginSettingsProvider SettingsProvider => this;

		private readonly object _catalogLock = new object();
		private DofCatalog _catalog;
		private readonly object _detectionLock = new object();
		private (IReadOnlyList<DofDetectedDevice> Devices, DateTime When)? _detection;
		private volatile DofConfigStore _store;
		private volatile PluginOutputLeases _leases;

		/// <param name="root">The folder of the imported configurations; the Player's plugin folder when null.</param>
		internal DofPluginFactory(string root)
		{
			_store = new DofConfigStore(root);
		}

		/// <summary>The imported configurations. Shared by the settings and the games, so a game's revision lease holds.</summary>
		internal DofConfigStore Store => _store;

		/// <summary>Creates the DOF instance of games; tests replace it.</summary>
		internal Func<string, Session.IDofNative> NativeFactory { get; set; }

		/// <summary>Creates the DOF instance of port tests; tests replace it.</summary>
		internal Func<string, IDofTestNative> TestNativeFactory { get; set; }

		/// <summary>How long a port test keeps the output on.</summary>
		internal TimeSpan PulseDuration { get; set; } = TimeSpan.FromSeconds(1);

		/// <summary>How long a port test waits for every controller to confirm that its outputs are off.</summary>
		internal TimeSpan ConfirmTimeout { get; set; } = DofPortTest.DefaultConfirmTimeout;

		public IPlayerPlugin Create(PluginApplicationContext context)
		{
			// The settings can be used before the host creates the plugin, so the store starts where the Player keeps
			// plugin folders; the host's own folder wins once it is known.
			var root = ConfigurationRoot(context.Configuration.DirectoryFor(context.Plugin));
			if (!string.Equals(Path.GetFullPath(root), _store.Root, StringComparison.OrdinalIgnoreCase)) {
				_store = new DofConfigStore(root);
			}
			_leases = context.Capabilities.Find<PluginOutputLeases>(UnityPluginCapability.Outputs);
			ForwardLibdofLog();
			return new DofPlugin(context, this);
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Register() => PlayerPluginRegistry.Register(new DofPluginFactory(DefaultRoot()));

		internal static string ConfigurationRoot(string pluginDirectory) => Path.Combine(pluginDirectory, "config");

		// Where the Player keeps the plugin's folder (PlayerUserConfigStore.ConfigDirectory/plugins/<id>).
		private static string DefaultRoot()
		{
			var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (string.IsNullOrWhiteSpace(profile)) {
				profile = Application.persistentDataPath;
			}
			return ConfigurationRoot(Path.Combine(profile, Application.productName, "plugins", Id));
		}

		/// <summary>
		/// Sends libdof's log lines to the log, until the application quits or, in the editor, the scripts reload: the
		/// native library outlives the scripting domain, so it must not call into one that is unloading.
		/// </summary>
		private static void ForwardLibdofLog()
		{
			if (Interlocked.Exchange(ref _libdofLog, 1) != 0) {
				return;
			}
			try {
				_libdofLogClosed = false;
				DofLibrary.SetLog(OnLibdofLog, DofLogLevel.Info);
				Application.quitting += StopLibdofLog;
#if UNITY_EDITOR
				UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += StopLibdofLog;
#endif
			} catch (Exception e) {
				Logger.Warn(e, "[DOF] The native DOF library could not be loaded.");
			}
		}

		// libdof's threads.
		private static void OnLibdofLog(DofLogLevel level, string message)
		{
			Interlocked.Increment(ref _libdofLogCalls);
			try {
				if (!_libdofLogClosed) {
					LibdofLogger.Log(LogLevelOf(level), message);
				}
			} finally {
				Interlocked.Decrement(ref _libdofLogCalls);
			}
		}

		/// <summary>
		/// Main thread: takes the log sink back from libdof and waits, briefly, for lines it is still delivering. Before
		/// the scripts reload and when the application quits.
		/// </summary>
		internal static void StopLibdofLog()
		{
			if (Interlocked.Exchange(ref _libdofLog, 0) == 0) {
				return;
			}
			_libdofLogClosed = true;
			Application.quitting -= StopLibdofLog;
#if UNITY_EDITOR
			UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= StopLibdofLog;
#endif
			try {
				DofLibrary.SetLog(null);
			} catch (Exception e) {
				Logger.Warn(e, "[DOF] Removing the native DOF library's log sink failed.");
			}
			var waited = Stopwatch.StartNew();
			while (Volatile.Read(ref _libdofLogCalls) > 0 && waited.ElapsedMilliseconds < LogDrainTimeoutMs) {
				Thread.Sleep(1);
			}
		}

		private const int LogDrainTimeoutMs = 500;

		internal static NLog.LogLevel LogLevelOf(DofLogLevel level)
		{
			switch (level) {
				case DofLogLevel.Error: return NLog.LogLevel.Error;
				case DofLogLevel.Warning: return NLog.LogLevel.Warn;
				case DofLogLevel.Debug: return NLog.LogLevel.Debug;
				default: return NLog.LogLevel.Info;
			}
		}

		#region Schema

		private static PluginSettingsSchema BuildSchema() => new PluginSettingsSchema(1,
			new PluginSettingsGroup("Configuration", "Imports apply from the next game. VPE never contacts the DOF Config Tool.",
				new PluginField("ActiveConfig", "Active configuration", SettingKind.Info) { Provider = "configuration" },
				new PluginField("ShowImport", "Show details", SettingKind.Action) { Provider = "show-import", AttachTo = "ActiveConfig" },
				new PluginField("Rollback", "Undo last import", SettingKind.Action) { Provider = "rollback", AttachTo = "ActiveConfig" }),
			new PluginSettingsGroup("Import from the Config Tool", "Download your directoutputconfig.zip from the DOF Config Tool with your browser. Importing it replaces the files it contains and keeps the rest, such as your cabinet file.",
				new PluginField("ImportZip", "Zip or file", SettingKind.File) { Default = "", FullWidth = true,
					Description = "A directoutputconfig.zip, or a single file such as Cabinet.xml." },
				new PluginField("ImportZipAction", "Import", SettingKind.Action) { Provider = "import-zip", AttachTo = "ImportZip" }),
			new PluginSettingsGroup("Import an existing installation", "A complete DOF configuration, such as the Config folder of DirectOutput. Importing it replaces the whole configuration; the folder is copied, never changed.",
				new PluginField("ImportFolder", "Configuration folder", SettingKind.Folder) { Default = "", FullWidth = true },
				new PluginField("ImportFolderAction", "Import", SettingKind.Action) { Provider = "import-folder", AttachTo = "ImportFolder" }),
			new PluginSettingsGroup("Hardware", "The connected boards DOF knows, found without opening them. A test turns one output on for a moment, outside a game; All off turns every output off, such as one a game that crashed left on.",
				new PluginField("Devices", "Connected boards", SettingKind.Info) { Provider = "devices" },
				new PluginField("Detect", "Detect again", SettingKind.Action) { Provider = "detect", AttachTo = "Devices" },
				new PluginField("AllOff", "All off", SettingKind.Action) { Provider = "all-off", AttachTo = "Devices" },
				new PluginField("TestPort", "Test an output", SettingKind.Choice) { Default = "", Provider = "test-ports", FullWidth = true },
				new PluginField("PulsePort", "Test", SettingKind.Action) { Provider = "pulse-port", AttachTo = "TestPort" }),
			new PluginSettingsGroup("Serial ports", "Changes the port of a controller in your cabinet file, for when the board got another port on this computer.",
				new PluginField("SerialOverrides", "Ports", SettingKind.Collection) { Default = new JArray(), FullWidth = true,
					Description = "The controller's name in the cabinet file, and the port to use. Applied to the active configuration and to every import.",
					Children = new[] {
						new PluginField("Controller", "Controller", SettingKind.Text) { Default = "" },
						new PluginField("Port", "Port", SettingKind.Text) { Default = "" },
					} },
				new PluginField("ApplySerialOverrides", "Apply ports", SettingKind.Action) { Provider = "apply-serial-overrides", AttachTo = "SerialOverrides" }),
			new PluginSettingsGroup("This game", "Which configuration rows the game uses.",
				new PluginField("Rom", "Game name", SettingKind.Choice) { Default = DofGameSetup.Auto, Scope = ConfigurationScope.GameOnly, Provider = "roms", FullWidth = true,
					Description = "Automatic uses the ROM the game plays, or the name the table sets for DOF. A table mapping for the table's name is offered here." }));

		#endregion

		#region Catalogs

		/// <summary>The catalog of a revision, read once per revision.</summary>
		internal DofCatalog CatalogOf(DofRevision revision)
		{
			lock (_catalogLock) {
				if (_catalog == null || _catalog.Revision.Id != revision.Id) {
					_catalog = DofCatalog.Load(revision);
				}
				return _catalog;
			}
		}

		public Task<PluginChoiceCatalog> DiscoverAsync(string provider, PluginChoiceRequest request, CancellationToken cancellation)
			=> Task.Run(() => Discover(provider, request, cancellation), cancellation);

		private PluginChoiceCatalog Discover(string provider, PluginChoiceRequest request, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			// leased while it is read: an import may activate another one meanwhile, and a prune would delete this one
			using var lease = Store.AcquireForReading();
			var revision = lease?.Revision;
			var catalog = revision != null ? CatalogOf(revision) : null;
			var id = revision?.Id ?? "none";
			var choices = new List<PluginChoice>();
			switch (provider) {
				case "roms": {
					var game = request.Game;
					string automatic;
					if (catalog == null || game == null) {
						automatic = "Uses the ROM the game plays, or the name the table sets for DOF.";
					} else {
						var (rom, problem) = DofGameSetup.ResolveRom(DofGameSetup.Auto, game.Value, null, null, catalog);
						automatic = rom == null ? problem : problem ?? DescribeRows(catalog, rom, "Uses " + rom);
					}
					choices.Add(new PluginChoice(DofGameSetup.Auto, "Automatic", automatic));
					var mapping = DofGameSetup.TableMapping(catalog, DofGameSetup.TableNames(request.Package?.Path, request.Package?.Title));
					if (mapping != null) {
						// Table mappings go by the table's title, which another table can share: offered, never applied on their own.
						choices.Add(new PluginChoice(mapping.Value.Value, $"{mapping.Value.Value} (table mapping)",
							DescribeRows(catalog, mapping.Value.Value, $"The configuration maps \"{mapping.Value.Key}\" to it")));
					}
					if (catalog != null) {
						foreach (var group in catalog.Rows.GroupBy(r => r.Rom, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)) {
							if (mapping != null && string.Equals(group.Key, mapping.Value.Value, StringComparison.OrdinalIgnoreCase)) {
								continue;
							}
							choices.Add(new PluginChoice(group.Key, group.Key, "Rows on " + Devices(catalog, group.Select(r => r.Device))));
						}
					}
					return new PluginChoiceCatalog(id, choices) {
						EmptyMessage = "Import a DOF configuration first.",
						MissingSelectionMessage = "The configuration has no rows for this name any more.",
					};
				}
				case "test-ports": {
					if (catalog != null) {
						foreach (var device in catalog.Devices) {
							string toy = null;
							for (var i = 0; i < device.Ports.Count; i++) {
								var name = device.Ports[i]?.Trim();
								var label = string.IsNullOrEmpty(name)
									? toy != null ? $"{toy}, next port" : "(unnamed)"
									: name;
								if (!string.IsNullOrEmpty(name)) {
									toy = name;
								}
								choices.Add(new PluginChoice(DofPortTest.Id(device.Number, i + 1), $"Device {device.Number}, port {i + 1}: {label}", device.FileName));
							}
						}
					}
					// Not complete: a saved output that a new import dropped must not block saving the other settings.
					return new PluginChoiceCatalog(id, choices, isComplete: false) { EmptyMessage = "Import a DOF configuration with ini files first." };
				}
				default:
					return new PluginChoiceCatalog("empty", Array.Empty<PluginChoice>());
			}
		}

		private static string DescribeRows(DofCatalog catalog, string rom, string prefix)
		{
			var rows = catalog.Match(rom);
			return rows.Count == 0
				? $"{prefix}. The configuration has no rows for {rom}."
				: $"{prefix}: {rom} has rows on {Devices(catalog, rows.Select(r => r.Device))}.";
		}

		private static string Devices(DofCatalog catalog, IEnumerable<int> numbers)
			=> string.Join(", ", numbers.Distinct().OrderBy(n => n).Select(n => {
				var device = catalog.Devices.FirstOrDefault(d => d.Number == n);
				return device != null ? $"{n} ({device.FileName})" : n.ToString();
			}));

		#endregion

		#region Information

		public Task<PluginInfo> DescribeAsync(string provider, PluginChoiceRequest request, CancellationToken cancellation)
			=> Task.Run(() => Describe(provider, cancellation), cancellation);

		private PluginInfo Describe(string provider, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			switch (provider) {
				case "configuration": return DescribeConfiguration();
				case "devices": return DescribeDevices();
				default: return new PluginInfo("");
			}
		}

		private PluginInfo DescribeConfiguration()
		{
			// leased while it is read: an import may activate another one meanwhile, and a prune would delete this one
			using var lease = Store.AcquireForReading();
			var revision = lease?.Revision;
			if (revision == null) {
				return new PluginInfo("No configuration imported", PluginInfoTone.Problem, new[] {
					Line("Next", "Import the zip you downloaded from the DOF Config Tool, or an existing DirectOutput folder, below."),
				}, new[] { "ShowImport", "Rollback" });
			}
			var catalog = CatalogOf(revision);
			var warnings = revision.CountWarnings();
			var origin = revision.ReadOrigin();
			var games = catalog.Rows.Select(r => r.Rom).Distinct(StringComparer.OrdinalIgnoreCase).Count();
			var lines = new List<KeyValuePair<string, string>> {
				Line("Imported", When(revision) + (origin != null ? ", from " + origin : "")),
				Line("Games", games == 1 ? "1 game" : $"{games} games"),
				Line("Devices", catalog.Devices.Count == 0 ? "No ini files" : string.Join(", ", catalog.Devices.Select(d => $"{d.Number} ({d.FileName})"))),
				Line("Cabinet", DescribeCabinet(revision, catalog)),
			};
			if (catalog.TableConfigs.Count > 0) {
				lines.Add(Line("Table configs", $"{catalog.TableConfigs.Count}, kept but not loaded"));
			}
			var previous = Store.Previous;
			if (previous != null) {
				lines.Add(Line("Undo", "Goes back to the configuration of " + When(previous)));
			}
			// without an earlier configuration, Undo has nothing to go back to
			var unavailable = previous == null ? new[] { "Rollback" } : Array.Empty<string>();
			return warnings == 0
				? new PluginInfo("Active, without warnings", PluginInfoTone.Good, lines, unavailable)
				: new PluginInfo(warnings == 1 ? "Active, with 1 warning. Show details lists it." : $"Active, with {warnings} warnings. Show details lists them.", PluginInfoTone.Warning, lines, unavailable);
		}

		private PluginInfo DescribeDevices()
		{
			// All off drives the outputs of the active configuration, so there is nothing to turn off without one
			var unavailable = Store.Active == null ? new[] { "AllOff" } : Array.Empty<string>();
			IReadOnlyList<DofDetectedDevice> devices;
			DateTime when;
			try {
				(devices, when) = Detection(false);
			} catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is DofException) {
				return new PluginInfo("The native DOF library could not be loaded: " + e.Message, PluginInfoTone.Problem, null, unavailable);
			}
			var lines = new List<KeyValuePair<string, string>>();
			foreach (var device in devices) {
				var what = (device.Device > 0 ? $"Device {device.Device}" : $"Devices {device.DeviceRange}") + ": " + device.Info;
				lines.Add(Line(device.Type, device.Notes.Count == 0 ? what : what + ". " + string.Join(" ", device.Notes)));
			}
			lines.Add(Line("Checked", when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
			return devices.Count == 0
				? new PluginInfo("No board DOF knows is connected", PluginInfoTone.Neutral, lines, unavailable)
				: new PluginInfo(devices.Count == 1 ? "1 board connected" : $"{devices.Count} boards connected", PluginInfoTone.Good, lines, unavailable);
		}

		/// <summary>The boards last detected; detects them the first time, and again when asked to.</summary>
		private (IReadOnlyList<DofDetectedDevice> Devices, DateTime When) Detection(bool again)
		{
			lock (_detectionLock) {
				if (again || _detection == null) {
					_detection = (DofDetection.Detect(DofLibrary.EnumerateDevices()), DateTime.UtcNow);
				}
				return _detection.Value;
			}
		}

		private static string DescribeCabinet(DofRevision revision, DofCatalog catalog)
		{
			if (!catalog.HasCabinet) {
				return "No cabinet file: DOF sets up the boards it finds itself";
			}
			// as a game reads it: the cabinet file the global config names, its root and AutoConfigEnabled as libdof finds them
			var autoConfig = DofGameSetup.AutoConfigEnabled(revision);
			var controllers = string.Join(", ", catalog.Controllers.Select(c => c.SerialPort != null ? $"{c.Name} on {c.SerialPort}" : c.Name));
			if (catalog.Controllers.Count == 0) {
				return autoConfig
					? "A cabinet file without controllers: DOF sets up the boards it finds"
					: "A cabinet file without controllers, and automatic setup is off: DOF drives no board";
			}
			return autoConfig ? controllers + ", and the boards DOF finds itself" : controllers;
		}

		private static string When(DofRevision revision)
			=> revision.CreatedUtc?.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) ?? revision.Id;

		private static KeyValuePair<string, string> Line(string label, string value) => new KeyValuePair<string, string>(label, value);

		#endregion

		#region Actions

		public Task<PluginActionResult> RunActionAsync(string action, PluginChoiceRequest request, CancellationToken cancellation)
			=> Task.Run(() => RunAction(action, request, cancellation), cancellation);

		private PluginActionResult RunAction(string action, PluginChoiceRequest request, CancellationToken cancellation)
		{
			try {
				switch (action) {
					case "import-zip": return Import(request.Settings.String("ImportZip").Trim(), DofImportMode.Merge, request.Settings, "Choose the zip or file to import.");
					case "import-folder": return Import(request.Settings.String("ImportFolder").Trim(), DofImportMode.Replace, request.Settings, "Choose the folder to import.");
					case "show-import": return ShowImport();
					case "rollback":
						return Store.Rollback()
							? new PluginActionResult(true, "The previous configuration is active again. It applies from the next game.")
							: new PluginActionResult(false, "There is no previous configuration.");
					case "apply-serial-overrides": return ApplySerialOverrides(request.Settings);
					case "detect": return Detect();
					case "pulse-port": return PulsePort(request.Settings.String("TestPort"), cancellation);
					case "all-off": return AllOff(cancellation);
					default: return new PluginActionResult(false, "Unknown DOF action.");
				}
			} catch (DofException e) {
				Logger.Warn(e, "[DOF] " + action + " failed.");
				return new PluginActionResult(false, e.Message);
			}
		}

		private PluginActionResult Import(string source, DofImportMode mode, PluginSettingsSnapshot settings, string missing)
		{
			if (source.Length == 0) {
				return new PluginActionResult(false, missing);
			}
			// the ports go into the import itself, so undoing it restores the configuration before it
			var overrides = SerialOverrides(settings);
			var report = new DofImporter(Store).Import(source, mode, overrides);
			Logger.Info("[DOF] Import of " + source + ":\n" + report);
			if (!report.Accepted) {
				return new PluginActionResult(false, "Not imported: " + (report.RejectionReason ?? "the configuration could not be published."));
			}
			var message = new StringBuilder("Imported. It applies from the next game.");
			if (overrides.Count > 0) {
				message.Append(" Your serial ports were applied where the cabinet has those controllers.");
			}
			AppendWarnings(message, report);
			return new PluginActionResult(true, message.ToString());
		}

		private static void AppendWarnings(StringBuilder message, DofImportReport report)
		{
			var warnings = report.Entries.Where(e => e.Severity == DofReportSeverity.Warning).ToList();
			if (warnings.Count == 0) {
				return;
			}
			message.Append(warnings.Count == 1 ? " 1 warning: " : $" {warnings.Count} warnings, the first: ").Append(warnings[0].Message);
			if (warnings.Count > 1) {
				message.Append(" Show details lists them all.");
			}
		}

		private PluginActionResult ShowImport()
		{
			using var lease = Store.AcquireForReading();
			var revision = lease?.Revision;
			if (revision == null) {
				return new PluginActionResult(false, "No DOF configuration is imported.");
			}
			// The Active configuration block already sums it up: the details are the controllers' types and the import report.
			var catalog = CatalogOf(revision);
			var text = new StringBuilder();
			if (catalog.Controllers.Count > 0) {
				text.Append("Controllers: ").Append(string.Join(", ", catalog.Controllers.Select(c => c.SerialPort != null ? $"{c.Name} ({c.Type}, {c.SerialPort})" : $"{c.Name} ({c.Type})"))).Append(".\n");
			}
			var report = revision.ReadReport();
			text.Append(string.IsNullOrWhiteSpace(report) ? $"Revision {revision.Id} has no import report." : report.Trim());
			return new PluginActionResult(true, text.ToString());
		}

		private PluginActionResult ApplySerialOverrides(PluginSettingsSnapshot settings)
		{
			var overrides = SerialOverrides(settings);
			if (overrides.Count == 0) {
				return new PluginActionResult(false, "Add a controller and its port first.");
			}
			if (Store.Active == null) {
				return new PluginActionResult(false, "Import a DOF configuration first. The ports are applied to it.");
			}
			var report = new DofImporter(Store).ApplySerialOverrides(overrides);
			Logger.Info("[DOF] Serial ports:\n" + report);
			if (!report.Accepted) {
				return new PluginActionResult(false, "Not applied: " + report.RejectionReason);
			}
			var message = new StringBuilder("Applied. Save the settings to keep them for future imports. They apply from the next game.");
			AppendWarnings(message, report);
			return new PluginActionResult(true, message.ToString());
		}

		/// <summary>The serial overrides of the settings, by controller name.</summary>
		internal static IReadOnlyDictionary<string, string> SerialOverrides(PluginSettingsSnapshot settings)
		{
			var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
			if (settings.Value("SerialOverrides") is JArray records) {
				foreach (var record in records.OfType<JObject>()) {
					var controller = record.Value<string>("Controller")?.Trim();
					var port = record.Value<string>("Port")?.Trim();
					if (!string.IsNullOrEmpty(controller) && !string.IsNullOrEmpty(port)) {
						overrides[controller] = port;
					}
				}
			}
			return overrides;
		}

		// The list itself is the Connected boards information, which the page shows again after the action.
		private PluginActionResult Detect()
		{
			var (detected, _) = Detection(true);
			Logger.Info("[DOF] Detected " + (detected.Count == 0 ? "no boards." : string.Join("; ", detected.Select(d => d.ToString()))));
			return new PluginActionResult(true, detected.Count == 0 ? "No board DOF knows is connected." : detected.Count == 1 ? "Found 1 board." : $"Found {detected.Count} boards.");
		}

		private PluginActionResult PulsePort(string id, CancellationToken cancellation)
		{
			if (!DofPortTest.TryParse(id, out var device, out var port)) {
				return new PluginActionResult(false, "Choose an output first.");
			}
			var (success, message) = DofPortTest.Pulse(Store, _leases, Id, device, port, PulseDuration, ConfirmTimeout, TestNativeFactory, cancellation);
			return new PluginActionResult(success, message);
		}

		private PluginActionResult AllOff(CancellationToken cancellation)
		{
			var (success, message) = DofPortTest.AllOff(Store, _leases, Id, ConfirmTimeout, TestNativeFactory, cancellation);
			return new PluginActionResult(success, message);
		}

		#endregion

		#region Validation and defaults

		public IReadOnlyList<PluginValidationIssue> Validate(PluginChoiceRequest request)
		{
			var issues = new List<PluginValidationIssue>();
			if (request.Settings.Value("SerialOverrides") is JArray records) {
				var names = new HashSet<string>(StringComparer.Ordinal);
				foreach (var record in records.OfType<JObject>()) {
					var controller = record.Value<string>("Controller")?.Trim() ?? "";
					var port = record.Value<string>("Port")?.Trim() ?? "";
					if (controller.Length == 0 && port.Length == 0) {
						continue;
					}
					if (controller.Length == 0 || port.Length == 0) {
						issues.Add(new PluginValidationIssue("SerialOverrides", "Each port needs a controller name and a port."));
					} else if (!names.Add(controller)) {
						issues.Add(new PluginValidationIssue("SerialOverrides", $"{controller} has more than one port."));
					}
				}
			}
			return issues;
		}

		public IReadOnlyList<PluginSystemDefault> FindSystemDefaults(PluginSettingsSnapshot settings, CancellationToken cancellation)
		{
			var folder = ExistingDofFolder();
			return new[] {
				new PluginSystemDefault("DOF configuration", "DirectOutput") {
					Path = folder ?? "",
					FieldId = "ImportFolder",
					UsedWhenEmpty = false,
					Description = folder == null
						? "No DirectOutput installation found. Import your Config Tool zip."
						: "An existing DirectOutput configuration. Import it to use it in VPE; it is copied, never changed.",
				},
			};
		}

		private static string ExistingDofFolder()
		{
			var candidates = new List<string> { @"C:\DirectOutput\Config" };
			foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }) {
				var path = Environment.GetFolderPath(root);
				if (!string.IsNullOrEmpty(path)) {
					candidates.Add(Path.Combine(path, "DirectOutput", "Config"));
				}
			}
			foreach (var candidate in candidates) {
				try {
					if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "directoutputconfig*.ini").Any()) {
						return candidate;
					}
				} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
					// Not readable: not a candidate.
				}
			}
			return null;
		}

		#endregion

		/// <summary>The player's device leases, once the host created the plugin.</summary>
		internal PluginOutputLeases Leases => _leases;
	}
}
