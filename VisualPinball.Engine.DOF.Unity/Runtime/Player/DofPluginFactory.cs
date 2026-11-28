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
	internal sealed class DofPluginFactory : IPlayerPluginFactory, IPluginSettingsProvider, IPluginSettingsInfoProvider, IPluginOutputMapProvider, IPluginOutputRearranger, IPluginSystemDefaults
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
		private readonly object _wiringLock = new object();
		private DofOutputWiring _wiring;
		private string _wiringRoot;
		private DateTime _wiringStamp;
		// one change of a wiring file at a time in the process, whichever factory makes it
		private static readonly object WiringFileLock = new object();

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

		/// <summary>The serial ports of this computer, with what they are; tests replace it.</summary>
		internal Func<IReadOnlyList<(string Port, string Description)>> SerialPortsFactory { get; set; } = LocalSerialPorts;

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
			new PluginSettingsGroup("Hardware", "The connected boards DOF knows, found without opening them, and what the configuration wires to their outputs. Testing an output turns it on for a moment, outside a game; All off turns every output off, such as one a game that crashed left on. Where your cabling differs from the configuration, rearrange the outputs: games and tests use it at once, with every configuration you import.",
				new PluginField("Devices", "Connected boards", SettingKind.Info) { Provider = "devices" },
				new PluginField("Detect", "Detect again", SettingKind.Action) { Provider = "detect", AttachTo = "Devices" },
				new PluginField("AllOff", "All off", SettingKind.Action) { Provider = "all-off", AttachTo = "Devices" },
				new PluginField("ResetWiring", "Undo rearranging", SettingKind.Action) { Provider = "reset-wiring", AttachTo = "Devices" },
				new PluginField("Outputs", "Outputs", SettingKind.Outputs) { Provider = "outputs", FullWidth = true }),
			new PluginSettingsGroup("Serial ports", "Changes the port of a controller in your cabinet file, for when the board got another port on this computer.",
				new PluginField("SerialControllers", "Serial controllers", SettingKind.Info) { Provider = "serial-ports" },
				new PluginField("SerialOverrides", "Ports", SettingKind.Collection) { Default = new JArray(), FullWidth = true, AddLabel = "Add a controller",
					Description = "A controller of your cabinet file, and the port to use. Applied to the active configuration and to every import.",
					Children = new[] {
						new PluginField("Controller", "Controller", SettingKind.Choice) { Default = "", Provider = "serial-controllers", Row = "port" },
						new PluginField("Port", "Port", SettingKind.Choice) { Default = "", Provider = "serial-port-names", Row = "port" },
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
				case "serial-controllers": {
					foreach (var controller in catalog?.Controllers.Where(c => c.UsesSerialPort) ?? Enumerable.Empty<DofCatalogController>()) {
						if (choices.All(c => c.Id != controller.Name)) {
							choices.Add(new PluginChoice(controller.Name, $"{controller.Name} ({controller.Type})",
								controller.SerialPort != null ? $"On {controller.SerialPort} in the active configuration." : "The cabinet file names no port for it yet."));
						}
					}
					// a port saved for a controller the cabinet no longer has stays, so removing it is the user's call
					foreach (var name in SerialOverrides(request.Settings).Keys.Where(n => choices.All(c => c.Id != n))) {
						choices.Add(new PluginChoice(name, name + " (not in the cabinet file)", "The active cabinet file has no serial controller of this name."));
					}
					return new PluginChoiceCatalog(id, choices) { EmptyMessage = "Your cabinet file has no controller on a serial port." };
				}
				case "serial-port-names": {
					foreach (var (port, description) in SerialPortsOrNone()) {
						choices.Add(new PluginChoice(port, description.Length > 0 && description != port ? $"{port} · {description}" : port, "A serial port of this computer."));
					}
					// a board that is unplugged now keeps its port
					foreach (var port in SerialOverrides(request.Settings).Values.Distinct().Where(p => choices.All(c => c.Id != p))) {
						choices.Add(new PluginChoice(port, port + " (not connected)", "This computer has no such serial port now."));
					}
					return new PluginChoiceCatalog("ports", choices) { EmptyMessage = "This computer has no serial ports." };
				}
				default:
					return new PluginChoiceCatalog("empty", Array.Empty<PluginChoice>());
			}
		}

		/// <summary>The serial ports DOF finds on this computer, by name.</summary>
		private static IReadOnlyList<(string Port, string Description)> LocalSerialPorts()
			=> DofLibrary.EnumerateDevices().Where(d => d.Kind == DofDeviceKind.Serial && d.Path.Length > 0)
				.Select(d => (d.Path, d.Description)).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();

		private IReadOnlyList<(string Port, string Description)> SerialPortsOrNone()
		{
			try {
				return SerialPortsFactory();
			} catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is DofException) {
				Logger.Warn(e, "[DOF] Listing the serial ports failed.");
				return Array.Empty<(string, string)>();
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
			=> Task.Run(() => Describe(provider, request, cancellation), cancellation);

		private PluginInfo Describe(string provider, PluginChoiceRequest request, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			switch (provider) {
				case "configuration": return DescribeConfiguration();
				case "devices": return DescribeDevices();
				case "serial-ports": return DescribeSerialPorts(request.Settings);
				default: return new PluginInfo("");
			}
		}

		/// <summary>
		/// The cabinet's controllers on serial ports, and whether this computer has their ports. Without any, and without saved
		/// ports, there is nothing to change, and the ports are hidden.
		/// </summary>
		private PluginInfo DescribeSerialPorts(PluginSettingsSnapshot settings)
		{
			using var lease = Store.AcquireForReading();
			var revision = lease?.Revision;
			var serial = revision != null ? CatalogOf(revision).Controllers.Where(c => c.UsesSerialPort).ToList() : new List<DofCatalogController>();
			// any record, also one still without its port, stays to be finished or removed
			var records = (settings.Value("SerialOverrides") as JArray)?.Count ?? 0;
			var hidden = serial.Count == 0 && records == 0 ? new[] { "SerialOverrides" } : Array.Empty<string>();
			if (revision == null) {
				return new PluginInfo("Import a DOF configuration first. Its cabinet file names the controllers on serial ports.", PluginInfoTone.Neutral) { HiddenFields = hidden };
			}
			if (serial.Count == 0) {
				return new PluginInfo("Your cabinet file has no controller on a serial port, so there is nothing to change here.", PluginInfoTone.Neutral) { HiddenFields = hidden };
			}
			var local = new HashSet<string>(SerialPortsOrNone().Select(p => p.Port), StringComparer.OrdinalIgnoreCase);
			var lines = serial.Select(c => Line(c.Name, c.SerialPort == null ? $"No port yet ({c.Type})"
				: local.Contains(c.SerialPort) ? $"{c.SerialPort} ({c.Type})" : $"{c.SerialPort} ({c.Type}), which this computer does not have")).ToList();
			var missing = serial.Count(c => c.SerialPort == null || !local.Contains(c.SerialPort));
			return missing == 0
				? new PluginInfo(serial.Count == 1 ? "1 controller on a serial port" : $"{serial.Count} controllers on serial ports", PluginInfoTone.Good, lines) { HiddenFields = hidden }
				: new PluginInfo(missing == 1 ? "1 controller has no port this computer has. Choose its port below." : $"{missing} controllers have no port this computer has. Choose their ports below.",
					PluginInfoTone.Warning, lines) { HiddenFields = hidden };
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
			var unavailable = new List<string>();
			if (Store.Active == null) {
				unavailable.Add("AllOff");
			}
			if (Wiring.IsEmpty) {
				unavailable.Add("ResetWiring");
			}
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

		#region Outputs

		/// <summary>The outputs of a LedWiz, which ini devices 1 to 16 stand for; the Config Tool's header ends at the last one in use.</summary>
		private const int LedWizOutputs = 32;

		/// <summary>
		/// Why the outputs can't be tested: a host that shares its device reservations neither with the settings page nor before a
		/// game (<see cref="DofPortTest.NoLeases"/>).
		/// </summary>
		internal const string NoTestsYet = "Start a game once to test the outputs.";

		/// <summary>
		/// The host's device reservations for a settings request, so the outputs can be tested before a game created the
		/// plugin: the ones a game gets, so a test and another plugin never open a board at once. A request with the host's
		/// capabilities brings its own, none when the host publishes none; one without has those of the game that created the
		/// plugin.
		/// </summary>
		private PluginOutputLeases LeasesFor(PluginChoiceRequest request)
			=> request?.Capabilities != null ? request.Capabilities.Find<PluginOutputLeases>(UnityPluginCapability.Outputs) : _leases;

		public Task<PluginOutputMap> DescribeOutputsAsync(string provider, PluginChoiceRequest request, CancellationToken cancellation)
		{
			var leases = LeasesFor(request);
			return Task.Run(() => DescribeOutputs(provider, leases, cancellation), cancellation);
		}

		private PluginOutputMap DescribeOutputs(string provider, PluginOutputLeases leases, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			if (provider != "outputs") {
				return new PluginOutputMap(Array.Empty<PluginOutputBoard>());
			}
			// leased while it is read: an import may activate another one meanwhile, and a prune would delete this one; the wiring
			// of the same store
			var store = Store;
			using var lease = store.AcquireForReading();
			var revision = lease?.Revision;
			if (revision == null) {
				return new PluginOutputMap(Array.Empty<PluginOutputBoard>(), "Import a DOF configuration to see what it assigns to the outputs.");
			}
			var catalog = CatalogOf(revision);
			var detected = DetectedBoards(catalog);
			var wiring = ReadWiring(store.Root) ?? DofOutputWiring.None;
			var boards = catalog.Devices.Select(device => Board(device, detected, Reach(catalog, detected, device.Number), wiring)).ToList();
			return new PluginOutputMap(boards, "The configuration has no ini files, so it assigns nothing to the outputs.", leases == null ? NoTestsYet : "");
		}

		/// <summary>
		/// The connected boards that are ini devices: only without a cabinet file, when DOF sets up the boards it finds. A
		/// cabinet's controllers or toys, such as Art-Net or a LedWiz equivalent, can take a board's number instead.
		/// </summary>
		private IReadOnlyList<DofDetectedDevice> DetectedBoards(DofCatalog catalog)
		{
			if (catalog.HasCabinet) {
				return Array.Empty<DofDetectedDevice>();
			}
			try {
				return Detection(false).Devices;
			} catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is DofException) {
				return Array.Empty<DofDetectedDevice>();
			}
		}

		/// <summary>
		/// The highest output DOF drives on an ini device with or without a row: what the cabinet file maps, or the outputs DOF
		/// sets up on a connected board of a fixed size.
		/// </summary>
		private static int Reach(DofCatalog catalog, IReadOnlyList<DofDetectedDevice> detected, int device)
		{
			var mapped = catalog.CabinetOutputs.TryGetValue(device, out var outputs) ? outputs : 0;
			var board = detected.FirstOrDefault(d => d.Device == device);
			return System.Math.Max(mapped, board == null ? 0 : FixedOutputs(board.Type));
		}

		// The outputs libdof's auto-configuration gives a board of a fixed size; 0 for one that reports its own, such as Pinscape.
		private static int FixedOutputs(string type)
		{
			switch (type) {
				case "LedWiz": return LedWizOutputs;
				case "PacDrive": return 16;
				case "PacLed64": return 64;
				case "PacUIO": return 96;
				case "FT245RBitbangController": return 8;
				default: return 0;
			}
		}

		/// <summary>
		/// The outputs of an ini device: what its header names on each port, and the three ports of each RGB toy as its red,
		/// green and blue, all named after the toy. Named after a board of its number when one is detected.
		/// </summary>
		/// <param name="mapped">The highest output DOF drives on the device with or without a row (<see cref="Reach"/>).</param>
		/// <param name="wiring">
		/// How the cabling differs from the configuration: each output shows the port that drives it, with a note where that is
		/// another one. The ids stay the ports', so a test of an output pulses the port that drives it.
		/// </param>
		internal static PluginOutputBoard Board(DofCatalogDevice device, IReadOnlyList<DofDetectedDevice> detected, int mapped = 0, DofOutputWiring wiring = null)
		{
			wiring ??= DofOutputWiring.None;
			var ports = Ports(device, mapped, wiring);
			var outputs = new List<PluginOutput>(ports.Count);
			for (var number = 1; number <= ports.Count; number++) {
				var port = wiring.PortOn(device.Number, number);
				var (name, channel) = ports[port - 1];
				outputs.Add(new PluginOutput(DofPortTest.Id(device.Number, port), number, name, channel) {
					Note = port != number ? $"Moved from {port}" : "",
				});
			}
			var board = detected.FirstOrDefault(d => d.Device == device.Number);
			return board != null
				? new PluginOutputBoard($"{board.Type} {device.Number}", $"Device {device.Number} · {device.FileName}", outputs) { Rearrangeable = true }
				: new PluginOutputBoard($"Device {device.Number}", device.FileName, outputs) { Rearrangeable = true };
		}

		/// <summary>What the configuration puts on each port of an ini device, from port 1: the header's name and the colour of an RGB toy's port.</summary>
		private static IReadOnlyList<(string Name, PluginOutputChannel Channel)> Ports(DofCatalogDevice device, int mapped, DofOutputWiring wiring)
		{
			var count = System.Math.Max(device.Number >= 1 && device.Number <= 16 ? System.Math.Max(device.PortCount, LedWizOutputs) : device.PortCount, mapped);
			count = System.Math.Max(count, wiring.Extent(device.Number));
			var rgb = new HashSet<int>(device.RgbPorts);
			var ports = new List<(string, PluginOutputChannel)>(count);
			for (var port = 1; port <= count; port++) {
				var name = port <= device.Ports.Count ? device.Ports[port - 1]?.Trim() ?? "" : "";
				if (rgb.Contains(port) && port + 2 <= count) {
					ports.Add((name, PluginOutputChannel.Red));
					ports.Add((name, PluginOutputChannel.Green));
					ports.Add((name, PluginOutputChannel.Blue));
					port += 2;
				} else {
					ports.Add((name, PluginOutputChannel.None));
				}
			}
			return ports;
		}

		public Task<PluginActionResult> TestOutputAsync(string provider, string output, PluginChoiceRequest request, CancellationToken cancellation)
		{
			var leases = LeasesFor(request);
			return Task.Run(() => {
				if (provider != "outputs") {
					return new PluginActionResult(false, "Unknown DOF outputs.");
				}
				try {
					return PulsePort(output, leases, cancellation);
				} catch (DofException e) {
					Logger.Warn(e, "[DOF] Testing output " + output + " failed.");
					return new PluginActionResult(false, e.Message);
				}
			}, cancellation);
		}

		/// <summary>
		/// The output wiring of the store's folder, read again when its file changed, such as by another settings page. Games
		/// and tests use it as they start; when it can't be read, every port drives its own output, and the log says so.
		/// </summary>
		internal DofOutputWiring Wiring => ReadWiring(Store.Root) ?? DofOutputWiring.None;

		// The next read reads the file, whatever its write time says: a file system may keep it too coarse to tell two saves apart.
		private void ForgetWiring()
		{
			lock (_wiringLock) {
				_wiring = null;
			}
		}

		/// <summary>The wiring of a folder, or null when its file can't be read, after a few tries.</summary>
		private DofOutputWiring ReadWiring(string root)
		{
			lock (_wiringLock) {
				var path = Path.Combine(root, DofOutputWiring.FileName);
				for (var attempt = 1; ; attempt++) {
					try {
						var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
						if (_wiring == null || _wiringRoot != root || _wiringStamp != stamp) {
							_wiring = DofOutputWiring.Load(root);
							_wiringRoot = root;
							_wiringStamp = stamp;
						}
						return _wiring;
					} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
						_wiring = null;
						if (attempt == 3) {
							Logger.Warn(e, "[DOF] The output wiring could not be read; until it can, every output gets what the configuration puts on it.");
							return null;
						}
						Thread.Sleep(20);
					}
				}
			}
		}

		public Task<PluginActionResult> SwapOutputsAsync(string provider, string first, string second, int count, PluginChoiceRequest request, CancellationToken cancellation)
			=> Task.Run(() => provider != "outputs" ? new PluginActionResult(false, "Unknown DOF outputs.") : SwapOutputs(first, second, count, cancellation), cancellation);

		/// <summary>
		/// Swaps what drives two runs of outputs of a device, by where they show: the ids are ports, which drive the outputs the
		/// wiring gives them. An RGB toy's three outputs move together or not at all, unless they are apart already.
		/// </summary>
		private PluginActionResult SwapOutputs(string first, string second, int count, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			if (!DofPortTest.TryParse(first, out var device, out var firstPort) || !DofPortTest.TryParse(second, out var other, out var secondPort)) {
				return new PluginActionResult(false, "Unknown output.");
			}
			if (device != other) {
				return new PluginActionResult(false, "Outputs move within their board only.");
			}
			if (device > DofOutputWiring.MaxDevice) {
				return new PluginActionResult(false, $"Device {device} can't be rearranged: DOF takes devices 1 to {DofOutputWiring.MaxDevice}.");
			}
			// one folder for the whole change, should the host move the store meanwhile
			var store = Store;
			using var lease = store.AcquireForReading();
			var revision = lease?.Revision;
			var catalog = revision != null ? CatalogOf(revision) : null;
			var entry = catalog?.Devices.FirstOrDefault(d => d.Number == device);
			if (entry == null) {
				return new PluginActionResult(false, "The configuration has no device " + device + ".");
			}
			lock (WiringFileLock) {
				var wiring = ReadWiring(store.Root);
				if (wiring == null) {
					return new PluginActionResult(false, "The output wiring could not be read, so nothing was changed. Try again.");
				}
				var ports = Ports(entry, Reach(catalog, DetectedBoards(catalog), device), wiring);
				var a = wiring.OutputOf(device, firstPort);
				var b = wiring.OutputOf(device, secondPort);
				if (count < 1 || firstPort > ports.Count || secondPort > ports.Count || a + count - 1 > ports.Count || b + count - 1 > ports.Count) {
					return new PluginActionResult(false, "The outputs are not all on the board.");
				}
				if (a < b + count && b < a + count) {
					return new PluginActionResult(false, "The outputs overlap.");
				}
				// An RGB toy on three outputs in a row: each run holds all three or none. One a new configuration put on outputs
				// apart moves an output at a time, so it can be put back together.
				for (var port = 1; port + 2 <= ports.Count; port++) {
					if (ports[port - 1].Channel != PluginOutputChannel.Red) {
						continue;
					}
					var start = wiring.OutputOf(device, port);
					if (wiring.OutputOf(device, port + 1) != start + 1 || wiring.OutputOf(device, port + 2) != start + 2) {
						continue;
					}
					foreach (var run in new[] { a, b }) {
						var inside = Enumerable.Range(start, 3).Count(o => o >= run && o < run + count);
						if (inside != 0 && inside != 3) {
							return new PluginActionResult(false, $"That would split {Toy(ports[port - 1].Name)}: its red, green and blue move together.");
						}
					}
				}
				try {
					wiring.Swap(device, a, b, count).Save(store.Root);
				} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentOutOfRangeException) {
					Logger.Warn(e, "[DOF] Saving the output wiring failed.");
					return new PluginActionResult(false, "Not rearranged: " + e.Message);
				} finally {
					ForgetWiring();
				}
				var moved = count == 1 ? $"outputs {a} and {b}" : $"outputs {a}–{a + count - 1} and {b}–{b + count - 1}";
				Logger.Info($"[DOF] Swapped {moved} of device {device}.");
				return new PluginActionResult(true, $"Swapped what drives {moved} of device {device}. {WhenItApplies()}");
			}
		}

		// A game keeps the wiring it started with.
		private static string WhenItApplies() => DofOwnership.InUse
			? "The running game keeps the wiring it started with: it applies from the next game."
			: "Games and tests use it from now on.";

		private static string Toy(string name) => name.Length > 0 ? name : "an RGB toy";

		#endregion

		#region Actions

		public Task<PluginActionResult> RunActionAsync(string action, PluginChoiceRequest request, CancellationToken cancellation)
		{
			var leases = LeasesFor(request);
			return Task.Run(() => RunAction(action, request, leases, cancellation), cancellation);
		}

		private PluginActionResult RunAction(string action, PluginChoiceRequest request, PluginOutputLeases leases, CancellationToken cancellation)
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
					case "all-off": return AllOff(leases, cancellation);
					case "reset-wiring": return ResetWiring();
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

		private PluginActionResult PulsePort(string id, PluginOutputLeases leases, CancellationToken cancellation)
		{
			if (!DofPortTest.TryParse(id, out var device, out var port)) {
				return new PluginActionResult(false, "Unknown output " + id + ".");
			}
			var store = Store;
			var (success, message) = DofPortTest.Pulse(store, leases, Id, ReadWiring(store.Root) ?? DofOutputWiring.None, device, port, PulseDuration, ConfirmTimeout, TestNativeFactory, cancellation);
			return new PluginActionResult(success, message);
		}

		private PluginActionResult AllOff(PluginOutputLeases leases, CancellationToken cancellation)
		{
			var store = Store;
			var (success, message) = DofPortTest.AllOff(store, leases, Id, ReadWiring(store.Root) ?? DofOutputWiring.None, ConfirmTimeout, TestNativeFactory, cancellation);
			return new PluginActionResult(success, message);
		}

		private PluginActionResult ResetWiring()
		{
			var root = Store.Root;
			lock (WiringFileLock) {
				var wiring = ReadWiring(root);
				if (wiring == null) {
					return new PluginActionResult(false, "The output wiring could not be read, so nothing was changed. Try again.");
				}
				if (wiring.IsEmpty) {
					return new PluginActionResult(false, "No output is rearranged.");
				}
				try {
					DofOutputWiring.None.Save(root);
				} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
					Logger.Warn(e, "[DOF] Removing the output wiring failed.");
					return new PluginActionResult(false, "Not undone: " + e.Message);
				} finally {
					ForgetWiring();
				}
			}
			Logger.Info("[DOF] The output wiring was reset.");
			return new PluginActionResult(true, "Every output has what the configuration puts on it again. " + WhenItApplies());
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
