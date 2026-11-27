// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace VisualPinball.Engine.DOF.Configuration
{
	public enum DofImportMode
	{
		/// <summary>
		/// Files of the import replace those of the active configuration, the rest is kept: a Config Tool zip (ini files,
		/// table mappings, shapes) keeps the cabinet and global config imported before. All ini files are replaced
		/// together, so a device the new set no longer has disappears.
		/// </summary>
		Merge,
		/// <summary>The import is the whole configuration.</summary>
		Replace,
	}

	/// <summary>Bounds that keep a hostile or mistaken import from filling the disk or walking a whole drive.</summary>
	public sealed class DofImportLimits
	{
		/// <summary>Largest single file, in bytes.</summary>
		public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;
		/// <summary>Largest sum of the imported files, in bytes.</summary>
		public long MaxTotalBytes { get; set; } = 512L * 1024 * 1024;
		/// <summary>Most entries of a zip, or files of a folder, looked at.</summary>
		public int MaxEntries { get; set; } = 5000;
		/// <summary>Deepest folder nesting inside a zip or folder.</summary>
		public int MaxDepth { get; set; } = 8;
	}

	/// <summary>
	/// Imports a DOF configuration (a Config Tool zip, a <c>directoutputconfig</c> folder of DOF or VPX, or a single
	/// file) into a new revision of a <see cref="DofConfigStore"/>, and makes it active if it is valid.
	/// </summary>
	/// <remarks>
	/// The import is staged and checked before anything changes: only DOF file types are taken, entries with absolute
	/// paths or <c>..</c> are rejected, sizes are bounded, every ini and XML file must parse (XML without DTDs). The
	/// revision gets a generated global config whose file patterns point into it (see <see cref="DofGlobalConfig"/>),
	/// and libdof reads its ini files and its cabinet's output controllers once before it is activated: a controller
	/// libdof would skip at the start of a session, such as a PacUIO without an Id, is reported with libdof's reason. A
	/// rejected import leaves the active configuration as it was.
	/// </remarks>
	public sealed class DofImporter
	{
		private static readonly string[] ImageExtensions = { ".png", ".gif", ".jpg", ".jpeg", ".bmp" };

		private static readonly HashSet<string> SupportedControllers = new HashSet<string>(StringComparer.Ordinal) {
			"ArtNet", "DudesCab", "FT245RBitbangController", "LedWiz", "NullOutputController", "PacDrive", "PacLed64", "PacUIO",
			"PinControl", "PinOne", "Pinscape", "PinscapePico", "TeensyStripController", "UMXController", "WemosD1MPStripController",
		};
		private static readonly HashSet<string> StubbedControllers = new HashSet<string>(StringComparer.Ordinal) {
			"WS2811StripController", "DirectStripController",
		};
		private static readonly HashSet<string> SupportedToys = new HashSet<string>(StringComparer.Ordinal) {
			"LedWizEquivalent", "LedStrip",
		};

		private readonly DofConfigStore _store;

		public DofImportLimits Limits { get; } = new DofImportLimits();

		/// <summary>Whether port names are checked as on Windows; the current platform unless a test says otherwise.</summary>
		internal bool WindowsPortNames { get; set; } = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

		/// <summary>Checks the revision with libdof before activating it. Tests that run without the native library turn it off.</summary>
		public bool ValidateWithLibdof { get; set; } = true;

		public DofImporter(DofConfigStore store)
		{
			_store = store ?? throw new ArgumentNullException(nameof(store));
		}

		/// <summary>Imports a zip file, a folder or a single configuration file.</summary>
		public DofImportReport Import(string source, DofImportMode mode = DofImportMode.Merge) => Import(source, mode, null);

		/// <summary>Imports a zip file, a folder or a single configuration file, with the serial ports of your controllers.</summary>
		/// <param name="source">The zip, folder or file.</param>
		/// <param name="mode">Whether the import replaces the active configuration or is merged into it.</param>
		/// <param name="serialOverrides">
		/// Controller name to serial port, applied to the imported cabinet before it becomes active, so the import is one
		/// revision and undoing it restores the configuration before it. A controller the cabinet doesn't have, or one
		/// without a serial port, is reported and skipped.
		/// </param>
		public DofImportReport Import(string source, DofImportMode mode, IReadOnlyDictionary<string, string> serialOverrides)
		{
			var report = new DofImportReport();
			if (string.IsNullOrEmpty(source) || (!File.Exists(source) && !Directory.Exists(source))) {
				report.Error($"\"{source}\" does not exist.");
				return report;
			}
			var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			// A drive's root has no name of its own: keep the whole path.
			if (name.Length == 0) {
				name = source;
			}
			report.Origin = Directory.Exists(source) ? $"the folder {name}" : name;
			var staging = _store.CreateStaging();
			try {
				var input = Path.Combine(staging, "in");
				Directory.CreateDirectory(input);
				if (Directory.Exists(source)) {
					StageFolder(source, input, report);
				} else if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
					StageZip(source, input, report);
				} else {
					StageFile(source, input, report);
				}
				if (report.HasErrors) {
					return report;
				}
				var imported = Classify(input, report);
				if (report.HasErrors) {
					return report;
				}
				var previous = mode == DofImportMode.Merge ? _store.Active : null;
				var config = Merge(imported, previous, report);
				if (report.HasErrors) {
					return report;
				}
				if (serialOverrides != null && serialOverrides.Count > 0) {
					if (config.Cabinet == null) {
						report.Warning(DofReportTopic.General, "The configuration has no cabinet file, so your serial ports were not applied.");
					} else {
						SetSerialPorts(config, serialOverrides, staging, report, false);
					}
				}
				ResolveShapes(config);
				Analyse(config, WindowsPortNames, report);
				if (report.HasErrors) {
					return report;
				}
				Write(config, report);
				return report;
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is DofException) {
				report.Error($"Reading the import failed: {e.Message}");
				return report;
			} finally {
				DofConfigStore.TryDelete(staging);
			}
		}

		/// <summary>
		/// Sets the serial port of cabinet controllers, by controller name, in a new revision made from the active one. A
		/// cabinet file written on Windows names ports like COM3, which other platforms call /dev/ttyACM0 and the like.
		/// </summary>
		/// <param name="ports">Controller name to serial port. Controllers not listed keep theirs.</param>
		public DofImportReport ApplySerialOverrides(IReadOnlyDictionary<string, string> ports)
		{
			var report = new DofImportReport();
			var active = _store.Active;
			if (active == null) {
				report.Error("No DOF configuration is imported.");
				return report;
			}
			report.Origin = "new serial ports";
			var staging = _store.CreateStaging();
			try {
				var config = Read(active);
				if (config.Cabinet == null) {
					report.Error("The DOF configuration has no cabinet file, so it has no serial ports to set.");
					return report;
				}
				if (!SetSerialPorts(config, ports, staging, report, true)) {
					return report;
				}
				ResolveShapes(config);
				Analyse(config, WindowsPortNames, report);
				if (!report.HasErrors) {
					Write(config, report);
				}
				return report;
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is DofException) {
				report.Error($"Writing the configuration failed: {e.Message}");
				return report;
			} finally {
				DofConfigStore.TryDelete(staging);
			}
		}

		/// <summary>
		/// Sets the serial port of the cabinet's controllers in a copy of the cabinet file in the staging folder. Strict, a
		/// controller that can't take its port is an error and nothing is set; otherwise it is a warning and skipped.
		/// </summary>
		private static bool SetSerialPorts(ConfigSet config, IReadOnlyDictionary<string, string> ports, string staging, DofImportReport report, bool strict)
		{
			var xml = new XDocument(config.Cabinet.Xml);
			// Found as libdof finds them, by the names as written (DofXml.FirstChild).
			var controllers = DofXml.FirstChild(xml.Root, "OutputControllers")?.Elements().ToList() ?? new List<XElement>();
			foreach (var pair in ports) {
				// By the name the catalog lists it with: libdof's, or the type for one libdof names itself.
				var controller = controllers.FirstOrDefault(c => string.Equals(DofXml.ControllerName(c) ?? DofXml.LiteralName(c), pair.Key, StringComparison.Ordinal));
				string problem = null;
				string element = null;
				if (controller == null) {
					problem = $"The cabinet has no controller named \"{pair.Key}\".";
				} else if ((element = DofXml.SerialPortElement(DofXml.LiteralName(controller))) == null) {
					problem = $"{pair.Key} is a {DofXml.LiteralName(controller)}, which has no serial port.";
				} else if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Any(c => c < 32 || c >= 128)) {
					problem = $"\"{pair.Value}\" is not a serial port name.";
				}
				if (problem != null) {
					if (strict) {
						report.Error(problem);
						return false;
					}
					report.Warning(DofReportTopic.General, problem + " Its port was not changed.");
					continue;
				}
				var current = DofXml.Value(controller, element);
				report.Info(DofReportTopic.General, string.IsNullOrEmpty(current) ? $"{pair.Key} uses {pair.Value}." : $"{pair.Key} uses {pair.Value} instead of {current}.");
				// The element libdof reads, in the controller's namespace when there is none yet.
				var port = DofXml.FirstChild(controller, element);
				if (port != null) {
					port.Value = pair.Value.Trim();
				} else {
					controller.Add(new XElement(controller.Name.Namespace + element, pair.Value.Trim()));
				}
			}
			var path = Path.Combine(staging, config.Cabinet.Name);
			DofXml.Save(xml, path);
			config.Cabinet = new ConfigFile { Path = path, Name = config.Cabinet.Name, Origin = config.Cabinet.Name, Xml = xml };
			return true;
		}

		#region Staging

		private void StageZip(string zipPath, string input, DofImportReport report)
		{
			using (var zip = ZipFile.OpenRead(zipPath)) {
				if (zip.Entries.Count > Limits.MaxEntries) {
					report.Error($"The zip has {zip.Entries.Count} entries, more than the {Limits.MaxEntries} a DOF configuration can have.");
					return;
				}
				long total = 0;
				foreach (var entry in zip.Entries) {
					var name = entry.FullName.Replace('\\', '/');
					if (name.EndsWith("/", StringComparison.Ordinal)) {
						continue;
					}
					var relative = SafeRelativePath(name, report);
					if (relative == null) {
						return;
					}
					if (Ignored(relative)) {
						continue;
					}
					if (!Wanted(relative)) {
						report.Info(DofReportTopic.Files, $"Skipped {relative}: not a DOF configuration file.");
						continue;
					}
					if (entry.Length > Limits.MaxFileBytes) {
						report.Error($"{relative} is {entry.Length} bytes, more than the {Limits.MaxFileBytes} a configuration file can have.");
						return;
					}
					var target = Path.Combine(input, relative.Replace('/', Path.DirectorySeparatorChar));
					if (!IsInside(input, target)) {
						report.Error($"The zip entry \"{name}\" leaves the zip's folder.");
						return;
					}
					Directory.CreateDirectory(Path.GetDirectoryName(target));
					using (var from = entry.Open())
					using (var to = File.Create(target)) {
						// The declared length can lie: count what really comes out.
						var copied = CopyBounded(from, to, Limits.MaxFileBytes);
						if (copied < 0) {
							report.Error($"{relative} unpacks to more than {Limits.MaxFileBytes} bytes.");
							return;
						}
						total += copied;
					}
					if (total > Limits.MaxTotalBytes) {
						report.Error($"The zip unpacks to more than {Limits.MaxTotalBytes} bytes.");
						return;
					}
				}
			}
		}

		private void StageFolder(string folder, string input, DofImportReport report)
		{
			var root = Path.GetFullPath(folder);
			var seen = 0;
			long total = 0;
			var pending = new Stack<(string dir, int depth)>();
			pending.Push((root, 0));
			while (pending.Count > 0) {
				var (dir, depth) = pending.Pop();
				foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos()) {
					if (++seen > Limits.MaxEntries) {
						report.Error($"{folder} has more than {Limits.MaxEntries} files and folders; choose the folder that holds the DOF configuration.");
						return;
					}
					// Links could lead out of the folder.
					if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
						report.Info(DofReportTopic.Files, $"Skipped {Relative(root, entry.FullName)}: links are not followed.");
						continue;
					}
					if (entry is DirectoryInfo sub) {
						if (depth + 1 > Limits.MaxDepth) {
							report.Info(DofReportTopic.Files, $"Skipped {Relative(root, sub.FullName)}: nested too deep.");
						} else if (!Ignored(sub.Name)) {
							pending.Push((sub.FullName, depth + 1));
						}
						continue;
					}
					var file = (FileInfo)entry;
					var relative = Relative(root, file.FullName);
					// Outside Windows a backslash is part of a file name, not a separator. No DOF file has one.
					// Checked first: the hidden-file filter splits at backslashes too, and would skip such a name silently.
					if (Path.DirectorySeparatorChar != '\\' && file.FullName.Substring(root.Length).IndexOf('\\') >= 0) {
						report.Info(DofReportTopic.Files, $"Skipped {relative}: the name has a backslash.");
						continue;
					}
					if (Ignored(relative)) {
						continue;
					}
					if (!Wanted(relative)) {
						report.Info(DofReportTopic.Files, $"Skipped {relative}: not a DOF configuration file.");
						continue;
					}
					if (file.Length > Limits.MaxFileBytes) {
						report.Error($"{relative} is {file.Length} bytes, more than the {Limits.MaxFileBytes} a configuration file can have.");
						return;
					}
					total += file.Length;
					if (total > Limits.MaxTotalBytes) {
						report.Error($"{folder} holds more than {Limits.MaxTotalBytes} bytes of configuration files.");
						return;
					}
					var target = Path.Combine(input, file.FullName.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
					if (!IsInside(input, target)) {
						report.Error($"{relative} would be copied outside the import's folder.");
						return;
					}
					Directory.CreateDirectory(Path.GetDirectoryName(target));
					file.CopyTo(target);
				}
			}
		}

		private void StageFile(string path, string input, DofImportReport report)
		{
			var name = Path.GetFileName(path);
			if (!Wanted(name)) {
				report.Error($"{name} is not a DOF configuration file (ini, XML, zip or image).");
				return;
			}
			var info = new FileInfo(path);
			if (info.Length > Limits.MaxFileBytes) {
				report.Error($"{name} is {info.Length} bytes, more than the {Limits.MaxFileBytes} a configuration file can have.");
				return;
			}
			info.CopyTo(Path.Combine(input, name));
		}

		/// <summary>A zip entry name as a relative path, or null (and an error) if it is absolute or leaves the folder.</summary>
		private string SafeRelativePath(string name, DofImportReport report)
		{
			var segments = name.Split('/');
			var unsafeName = name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":")
				|| segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
			if (unsafeName) {
				report.Error($"The zip entry \"{name}\" has an absolute path or leaves the zip's folder.");
				return null;
			}
			if (segments.Length - 1 > Limits.MaxDepth) {
				report.Error($"The zip entry \"{name}\" is nested deeper than {Limits.MaxDepth} folders.");
				return null;
			}
			return name;
		}

		private static bool Ignored(string relative)
		{
			// macOS resource forks and hidden files are never configuration.
			return relative.Split('/', '\\').Any(s => s.StartsWith(".", StringComparison.Ordinal) || s == "__MACOSX");
		}

		private static bool Wanted(string relative)
		{
			var name = Path.GetFileName(relative);
			var extension = Path.GetExtension(name).ToLowerInvariant();
			return DofIniFile.IsIniName(name) || extension == ".xml" || ImageExtensions.Contains(extension) || IsTableMappingsName(name);
		}

		/// <summary>libdof takes the table mappings from the first <c>tablemappings.*</c> file of the ini folder, whatever its extension.</summary>
		private static bool IsTableMappingsName(string name) => name.StartsWith("tablemappings.", StringComparison.OrdinalIgnoreCase);

		private static long CopyBounded(Stream from, Stream to, long max)
		{
			var buffer = new byte[81920];
			long copied = 0;
			int read;
			while ((read = from.Read(buffer, 0, buffer.Length)) > 0) {
				copied += read;
				if (copied > max) {
					return -1;
				}
				to.Write(buffer, 0, read);
			}
			return copied;
		}

		/// <summary>A path below root with '/' separators, for messages. Only the platform's separators are replaced.</summary>
		private static string Relative(string root, string path)
		{
			var relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return Path.DirectorySeparatorChar == '/' ? relative : relative.Replace(Path.DirectorySeparatorChar, '/');
		}

		/// <summary>Whether a path, once resolved, lies below a folder.</summary>
		internal static bool IsInside(string folder, string path)
		{
			var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			var full = Path.GetFullPath(path);
			var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			return full.Length > root.Length && full.StartsWith(root, comparison);
		}

		#endregion

		#region Classification

		/// <summary>The files of one configuration, by role. Paths point into staging or into the previous revision.</summary>
		private sealed class ConfigSet
		{
			public readonly SortedDictionary<int, ConfigFile> Inis = new SortedDictionary<int, ConfigFile>();
			public readonly List<ConfigFile> LegacyInis = new List<ConfigFile>();
			public ConfigFile GlobalConfig;
			/// <summary>The cabinet files of an import, of which <see cref="SelectCabinet"/> picks <see cref="Cabinet"/>.</summary>
			public readonly List<ConfigFile> Cabinets = new List<ConfigFile>();
			public ConfigFile Cabinet;
			public ConfigFile TableMappings;
			public ConfigFile Shapes;
			public ConfigFile ShapesBitmap;
			public readonly Dictionary<string, ConfigFile> TableConfigs = new Dictionary<string, ConfigFile>(StringComparer.OrdinalIgnoreCase);
			public readonly Dictionary<string, ConfigFile> Images = new Dictionary<string, ConfigFile>(StringComparer.OrdinalIgnoreCase);
		}

		private sealed class ConfigFile
		{
			/// <summary>Where the content is now.</summary>
			public string Path;
			/// <summary>The name in the revision.</summary>
			public string Name;
			/// <summary>Where it came from, for messages.</summary>
			public string Origin;
			public DofIniFile Ini;
			public XDocument Xml;
		}

		private ConfigSet Classify(string input, DofImportReport report)
		{
			var set = new ConfigSet();
			var byName = new Dictionary<string, ConfigFile>(StringComparer.OrdinalIgnoreCase);
			var globalConfigs = new List<ConfigFile>();
			var cabinets = set.Cabinets;
			var shapes = new List<ConfigFile>();
			var mappings = new List<ConfigFile>();

			foreach (var path in Directory.GetFiles(input, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)) {
				var origin = Relative(input, path);
				var name = System.IO.Path.GetFileName(path);
				if (name.Any(c => c >= 128)) {
					report.Warning(DofReportTopic.Files, $"Skipped {origin}: libdof cannot open file names with non-ASCII characters on every platform.");
					continue;
				}
				// One configuration is one folder: the same name twice must be the same file.
				if (byName.TryGetValue(name, out var existing)) {
					if (!File.ReadAllBytes(existing.Path).SequenceEqual(File.ReadAllBytes(path))) {
						report.Error($"The import has two different files named {name} ({existing.Origin} and {origin}). Import the folder of one configuration.");
						return set;
					}
					continue;
				}
				var file = new ConfigFile { Path = path, Name = name, Origin = origin };
				byName[name] = file;

				if (DofIniFile.IsIniName(name)) {
					var device = DofIniFile.DeviceOf(name, out var legacy);
					if (device < 0) {
						report.Warning(DofReportTopic.Files, $"Skipped {origin}: libdof only loads ini files whose name ends with a device number.");
						continue;
					}
					try {
						file.Ini = DofIniFile.Load(path);
					} catch (FormatException e) {
						report.Error(e.Message);
						return set;
					}
					if (legacy) {
						set.LegacyInis.Add(file);
					} else if (set.Inis.TryGetValue(device, out var other)) {
						report.Error($"{other.Origin} and {origin} are both device {device}; libdof would use only one of them.");
						return set;
					} else {
						set.Inis[device] = file;
					}
					continue;
				}

				var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
				if (extension != ".xml" && IsTableMappingsName(name)) {
					try {
						file.Xml = DofXml.Load(path);
					} catch (Exception e) when (e is System.Xml.XmlException || e is InvalidOperationException) {
						report.Info(DofReportTopic.Files, $"Skipped {origin}: not a table mapping XML document.");
						continue;
					}
					if (DofXml.KindOf(file.Xml) == DofXmlKind.TableMappings) {
						mappings.Add(file);
					} else {
						report.Info(DofReportTopic.Files, $"Skipped {origin}: not a table mapping XML document (root element <{file.Xml.Root?.Name.LocalName}>).");
					}
					continue;
				}
				if (extension != ".xml") {
					set.Images[name] = file;
					continue;
				}
				try {
					file.Xml = DofXml.Load(path);
				} catch (Exception e) when (e is System.Xml.XmlException || e is InvalidOperationException) {
					report.Error(DofXml.Describe(origin, e).Message);
					return set;
				}
				switch (DofXml.KindOf(file.Xml)) {
					case DofXmlKind.GlobalConfig: globalConfigs.Add(file); break;
					case DofXmlKind.Cabinet: cabinets.Add(file); break;
					case DofXmlKind.Shapes: shapes.Add(file); break;
					case DofXmlKind.TableMappings: mappings.Add(file); break;
					case DofXmlKind.Table:
						set.TableConfigs[System.IO.Path.GetFileNameWithoutExtension(name)] = file;
						break;
					default:
						report.Info(DofReportTopic.Files, $"Skipped {origin}: an XML file DOF does not use (root element <{file.Xml.Root?.Name.LocalName}>).");
						break;
				}
			}

			if (set.Inis.Count > 0 && set.LegacyInis.Count > 0) {
				report.Info(DofReportTopic.Files, $"Skipped {string.Join(", ", set.LegacyInis.Select(f => f.Origin))}: libdof reads ledcontrol ini files only when there is no directoutputconfig ini.");
				set.LegacyInis.Clear();
			}
			foreach (var legacy in set.LegacyInis) {
				if (set.Inis.ContainsKey(legacy.Ini.Device)) {
					report.Error($"Two legacy ini files are device {legacy.Ini.Device}.");
					return set;
				}
				set.Inis[legacy.Ini.Device] = legacy;
			}

			set.GlobalConfig = Pick(globalConfigs, "global config", f => f.Name.Equals(DofGlobalConfig.FileName, StringComparison.OrdinalIgnoreCase), report, false);
			// The cabinet is picked once the global config that applies is known, which a merge can keep (SelectCabinet).
			var shapesName = DofGlobalConfig.FileNameOf(DofXml.Value(set.GlobalConfig?.Xml.Root, "ShapeDefinitionFilePattern"));
			set.Shapes = Pick(shapes, "shape definition", f => f.Name.Equals(shapesName ?? "DirectOutputShapes.xml", StringComparison.OrdinalIgnoreCase), report, false);
			set.TableMappings = Pick(mappings, "table mapping", f => f.Name.Equals("tablemappings.xml", StringComparison.OrdinalIgnoreCase), report, false);
			if (set.TableMappings != null && !set.TableMappings.Name.Equals("tablemappings.xml", StringComparison.OrdinalIgnoreCase)) {
				report.Info(DofReportTopic.Files, $"{set.TableMappings.Origin} is imported as tablemappings.xml, the name libdof looks for.");
			}
			if (set.TableMappings != null) {
				set.TableMappings.Name = "tablemappings.xml";
			}
			return set;
		}

		/// <summary>
		/// Pairs the shape definitions with their bitmap, once the imported and the kept files are merged: libdof takes
		/// the bitmap with the XML's name and .png, whatever the XML names.
		/// </summary>
		private static void ResolveShapes(ConfigSet set)
		{
			if (set.ShapesBitmap != null) {
				set.Images[set.ShapesBitmap.Name] = set.ShapesBitmap;
				set.ShapesBitmap = null;
			}
			if (set.Shapes == null) {
				return;
			}
			var png = System.IO.Path.GetFileNameWithoutExtension(set.Shapes.Name) + ".png";
			if (set.Images.TryGetValue(png, out var bitmap)) {
				set.ShapesBitmap = bitmap;
				set.Images.Remove(png);
			}
		}

		/// <summary>
		/// Picks the cabinet from the imported cabinet files and the one a merge keeps, as libdof resolves the cabinet
		/// pattern of the global config that applies (the imported one, or the kept one) against the files the
		/// configuration ends with. Without a pattern, an imported cabinet replaces the kept one.
		/// </summary>
		/// <param name="retained">The previous configuration's cabinet, or null.</param>
		private static void SelectCabinet(ConfigSet set, ConfigFile retained, DofImportReport report)
		{
			var candidates = set.Cabinets.ToList();
			if (retained != null && !candidates.Any(f => f.Name.Equals(retained.Name, StringComparison.OrdinalIgnoreCase))) {
				candidates.Add(retained);
			}
			var pattern = DofGlobalConfig.FilePartOf(DofXml.Value(set.GlobalConfig?.Xml.Root, "CabinetConfigFilePattern"));
			if (pattern == null) {
				set.Cabinet = set.Cabinets.Count == 0
					? retained
					: Pick(set.Cabinets, "cabinet", f => f.Name.Equals("Cabinet.xml", StringComparison.OrdinalIgnoreCase), report, true);
				return;
			}
			// The global config says which cabinet: only the file it names or matches is it, never another one that
			// happens to be there. A placeholder resolves to nothing (the analysis reports it).
			var matching = pattern.IndexOfAny(new[] { '{', '}' }) >= 0
				? new List<ConfigFile>()
				: candidates.Where(f => DofGlobalConfig.Matches(pattern, f.Name)).ToList();
			if (matching.Count > 1) {
				report.Error($"The global config's cabinet pattern \"{pattern}\" matches several cabinet files ({string.Join(", ", matching.Select(f => f.Origin))}); DOF would take whichever it lists first. Name the cabinet file in the global config.");
			}
			set.Cabinet = matching.Count == 1 ? matching[0] : null;
			var skipped = set.Cabinets.Where(f => f != set.Cabinet).Select(f => f.Origin).ToList();
			if (skipped.Count > 0 && matching.Count <= 1) {
				report.Info(DofReportTopic.Files, $"Skipped {string.Join(", ", skipped)}: the global config's cabinet file is \"{pattern}\".");
			}
		}

		/// <summary>
		/// The files a configuration puts into its <c>directoutputconfig</c> folder, with their names there, the global
		/// config the import writes included (as a null file).
		/// </summary>
		private static IEnumerable<(ConfigFile File, string Name)> ConfigDirFiles(ConfigSet set)
		{
			foreach (var file in set.Inis.Values.Concat(new[] { set.Cabinet, set.TableMappings, set.Shapes }).Concat(set.Images.Values)) {
				if (file != null) {
					yield return (file, file.Name);
				}
			}
			if (set.Shapes != null && set.ShapesBitmap != null) {
				yield return (set.ShapesBitmap, System.IO.Path.GetFileNameWithoutExtension(set.Shapes.Name) + ".png");
			}
			yield return (null, DofGlobalConfig.FileName);
		}

		/// <summary>The names of the files a configuration puts into its <c>directoutputconfig</c> folder.</summary>
		private static IEnumerable<string> ConfigDirNames(ConfigSet set) => ConfigDirFiles(set).Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase);

		/// <summary>Rejects a configuration of which two files would get the same name in its folder, which a revision cannot hold.</summary>
		private static void CheckNames(ConfigSet set, DofImportReport report)
		{
			foreach (var group in ConfigDirFiles(set).GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) {
				var files = string.Join(" and ", group.Where(f => f.File != null).Select(f => f.File.Origin).Distinct());
				report.Error(group.Any(f => f.File == null)
					? $"{files} would be stored as {group.Key}, the name of the global config the player writes for DOF. Rename the file."
					: $"{files} would all be stored as {group.Key}. Rename all but one.");
			}
		}

		private static ConfigFile Pick(List<ConfigFile> files, string what, Func<ConfigFile, bool> preferred, DofImportReport report, bool ambiguousIsError)
		{
			if (files.Count <= 1) {
				return files.FirstOrDefault();
			}
			var pick = files.FirstOrDefault(preferred);
			if (pick != null) {
				report.Info(DofReportTopic.Files, $"Several {what} files: using {pick.Origin}, skipped {string.Join(", ", files.Where(f => f != pick).Select(f => f.Origin))}.");
				return pick;
			}
			var names = string.Join(", ", files.Select(f => f.Origin));
			if (ambiguousIsError) {
				report.Error($"The import has several {what} files ({names}) and nothing says which one to use.");
				return null;
			}
			report.Warning(DofReportTopic.Files, $"Several {what} files ({names}): using {files[0].Origin}.");
			return files[0];
		}

		#endregion

		#region Merge and analysis

		private static ConfigSet Merge(ConfigSet imported, DofRevision previous, DofImportReport report)
		{
			if (previous == null) {
				SelectCabinet(imported, null, report);
				if (imported.Inis.Count == 0) {
					report.Error("The import has no DOF ini file (directoutputconfig.ini or directoutputconfigN.ini), so DOF would have nothing to do.");
				}
				return imported;
			}
			var old = Read(previous);
			var kept = new List<string>();
			if (imported.Inis.Count == 0) {
				foreach (var pair in old.Inis) {
					imported.Inis[pair.Key] = pair.Value;
				}
				if (old.Inis.Count > 0) {
					kept.Add("the ini files");
				}
			}
			if (imported.GlobalConfig == null && old.GlobalConfig != null) {
				imported.GlobalConfig = old.GlobalConfig;
				kept.Add("the global config");
			}
			// After the global config: the one that applies says which cabinet.
			SelectCabinet(imported, old.Cabinet, report);
			if (imported.Cabinet != null && imported.Cabinet == old.Cabinet) {
				kept.Add(old.Cabinet.Name);
			}
			if (imported.TableMappings == null && old.TableMappings != null) {
				imported.TableMappings = old.TableMappings;
				kept.Add("the table mappings");
			}
			if (imported.Shapes == null && old.Shapes != null) {
				imported.Shapes = old.Shapes;
				kept.Add("the shape definitions");
			}
			foreach (var pair in old.TableConfigs) {
				if (!imported.TableConfigs.ContainsKey(pair.Key)) {
					imported.TableConfigs[pair.Key] = pair.Value;
				}
			}
			foreach (var pair in old.Images) {
				if (!imported.Images.ContainsKey(pair.Key)) {
					imported.Images[pair.Key] = pair.Value;
				}
			}
			if (kept.Count > 0) {
				report.Info(DofReportTopic.Files, $"Kept from the previous configuration: {string.Join(", ", kept)}.");
			}
			if (imported.Inis.Count == 0) {
				report.Error("The configuration has no DOF ini file (directoutputconfig.ini or directoutputconfigN.ini), so DOF would have nothing to do.");
			}
			return imported;
		}

		/// <summary>The files of an existing revision as a configuration set, for merging.</summary>
		private static ConfigSet Read(DofRevision revision)
		{
			var set = new ConfigSet();
			foreach (var path in Directory.GetFiles(revision.ConfigDir)) {
				var name = Path.GetFileName(path);
				var file = new ConfigFile { Path = path, Name = name, Origin = name };
				if (DofIniFile.IsIniName(name)) {
					var device = DofIniFile.DeviceOf(name, out _);
					if (device >= 0) {
						file.Ini = DofIniFile.Load(path);
						set.Inis[device] = file;
					}
					continue;
				}
				if (name.Equals(DofGlobalConfig.FileName, StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) {
					set.Images[name] = file;
					continue;
				}
				file.Xml = DofXml.Load(path);
				switch (DofXml.KindOf(file.Xml)) {
					case DofXmlKind.Cabinet: set.Cabinet = file; break;
					case DofXmlKind.Shapes: set.Shapes = file; break;
					case DofXmlKind.TableMappings: set.TableMappings = file; break;
				}
			}
			var source = Path.Combine(revision.BasePath, DofRevision.SourceFolder, DofGlobalConfig.FileName);
			if (File.Exists(source)) {
				set.GlobalConfig = new ConfigFile { Path = source, Name = DofGlobalConfig.FileName, Origin = DofGlobalConfig.FileName, Xml = DofXml.Load(source) };
			}
			if (Directory.Exists(revision.TablesDir)) {
				foreach (var path in Directory.GetFiles(revision.TablesDir, "*.xml")) {
					var name = Path.GetFileName(path);
					set.TableConfigs[Path.GetFileNameWithoutExtension(name)] = new ConfigFile { Path = path, Name = name, Origin = name };
				}
			}
			return set;
		}

		private static void Analyse(ConfigSet config, bool windowsPorts, DofImportReport report)
		{
			CheckNames(config, report);
			foreach (var ini in config.Inis.Values) {
				if (!ini.Ini.HasColorSection) {
					report.Warning(DofReportTopic.General, $"{ini.Name} has no colours ([Colors DOF] section), so DOF skips the whole file and device {ini.Ini.Device} stays dark.");
				}
				if (!ini.Ini.HasVersionSection) {
					report.Warning(DofReportTopic.General, $"{ini.Name} has no [version] section; it may be an old or hand-made file.");
				}
				if (ini.Ini.MinDofVersion != null) {
					report.Info(DofReportTopic.General, $"{ini.Name} asks for DOF {ini.Ini.MinDofVersion} or newer; the player uses libdof, which reads the same format.");
				}
				if (ini.Ini.UnknownSections.Count > 0) {
					report.Info(DofReportTopic.General, $"{ini.Name}: libdof ignores the sections {string.Join(", ", ini.Ini.UnknownSections)}.");
				}
			}

			var global = config.GlobalConfig?.Xml;
			foreach (var (element, pattern) in DofGlobalConfig.Patterns(global)) {
				switch (element) {
					case "CabinetConfigFilePattern": {
						// Resolved as libdof resolves it, against the files the merge ends with: a cabinet kept from the previous
						// import is not the one asked for when the global config names, or matches, another file.
						var filePart = DofGlobalConfig.FilePartOf(pattern);
						var resolved = filePart == null || filePart.IndexOfAny(new[] { '{', '}' }) >= 0
							? new List<string>()
							: ConfigDirNames(config).Where(n => DofGlobalConfig.Matches(filePart, n)).ToList();
						var matches = config.Cabinet != null && resolved.Count == 1 && resolved[0].Equals(config.Cabinet.Name, StringComparison.OrdinalIgnoreCase);
						report.Info(DofReportTopic.Relocation, matches
							? $"Cabinet file \"{pattern}\" is {config.Cabinet.Name} of this configuration."
							: $"Cabinet file \"{pattern}\" is not part of this configuration.");
						if (!matches) {
							// Without its cabinet DOF would configure whatever is connected, or use another cabinet, neither of which
							// is what this configuration describes. Only a configuration that names no cabinet gets auto-configuration.
							report.Error(resolved.Count > 1
								? $"The global config's cabinet file \"{pattern}\" matches several files of the configuration ({string.Join(", ", resolved)}); DOF would take whichever it lists first. Name the cabinet file in the global config."
								: resolved.Count == 1 && config.Cabinet != null
									? $"The global config's cabinet file \"{pattern}\" is {resolved[0]}, but the configuration's cabinet is {config.Cabinet.Name}. Import the cabinet file with it."
									: resolved.Count == 1
										? $"The global config's cabinet file \"{pattern}\" is {resolved[0]}, which is not a cabinet file."
										: config.Cabinet == null
											? $"The global config names the cabinet file \"{pattern}\", which the configuration does not have. Import the cabinet file with it."
											: $"The global config names the cabinet file \"{pattern}\", but the configuration's cabinet is {config.Cabinet.Name}. Import the cabinet file with it.");
						}
						break;
					}
					case "TableConfigFilePatterns":
						report.Info(DofReportTopic.Relocation, $"Table config pattern \"{pattern}\": the player loads no table configs; the ini rows drive the outputs.");
						break;
					case "LogFilePattern":
						report.Info(DofReportTopic.Relocation, "The log goes to the player's log instead of a file.");
						break;
					case "ShapeDefinitionFilePattern":
						if (config.Shapes == null) {
							report.Warning(DofReportTopic.Unresolved, $"The global config names the shapes file \"{pattern}\", which the import does not have.");
						}
						break;
					case "IniFilesPath":
						report.Info(DofReportTopic.Relocation, $"Ini folder \"{pattern}\" is this configuration's folder.");
						break;
				}
			}

			if (config.Shapes != null) {
				var stem = Path.GetFileNameWithoutExtension(config.Shapes.Name);
				var bitmap = DofGlobalConfig.FileNameOf(DofXml.Value(config.Shapes.Xml.Root, "BitmapFilePattern"));
				if (bitmap != null && !Path.GetFileNameWithoutExtension(bitmap).Equals(stem, StringComparison.OrdinalIgnoreCase)) {
					report.Warning(DofReportTopic.Unresolved, $"{config.Shapes.Name} names the bitmap {bitmap}, but libdof always uses {stem}.png.");
				}
				if (config.ShapesBitmap == null) {
					report.Warning(DofReportTopic.Unresolved, $"{stem}.png is missing, so shape effects of {config.Shapes.Name} show nothing.");
				}
			}

			if (config.Cabinet != null) {
				AnalyseCabinet(config.Cabinet, windowsPorts, report);
			}
			foreach (var table in config.TableConfigs.Values) {
				report.Warning(DofReportTopic.Unsupported, $"Table config {table.Name} is kept but not loaded: the player does not load table configs yet, so the ini rows drive the outputs instead.");
			}
		}

		private static void AnalyseCabinet(ConfigFile cabinet, bool windowsPorts, DofImportReport report)
		{
			var root = cabinet.Xml.Root;
			if (DofXml.FirstChild(root, "TableOverrideSettings")?.HasElements == true) {
				report.Warning(DofReportTopic.Unsupported, $"{cabinet.Name} has table override settings, which libdof ignores.");
			}
			var controllers = DofXml.FirstChild(root, "OutputControllers");
			if (controllers != null) {
				foreach (var controller in controllers.Elements()) {
					var type = DofXml.LiteralName(controller);
					var name = DofXml.ControllerName(controller) ?? type;
					var port = DofXml.SerialPortOf(controller);
					if (!string.IsNullOrEmpty(port) && !windowsPorts && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) {
						report.Warning(DofReportTopic.Unresolved, $"{cabinet.Name}: {name} uses {port}, a Windows port name; set its serial port for this computer.");
					}
					if (StubbedControllers.Contains(type)) {
						report.Warning(DofReportTopic.Unsupported, $"{cabinet.Name}: {name} is a {type}, which libdof does not implement; it never finds its device.");
					} else if (!SupportedControllers.Contains(type)) {
						report.Warning(DofReportTopic.Unsupported, $"{cabinet.Name}: {name} is a {type}, which libdof does not support; it is skipped.");
					}
				}
			}
			var toys = DofXml.FirstChild(root, "Toys");
			if (toys != null) {
				var skipped = toys.Elements().Select(DofXml.LiteralName).Where(t => !SupportedToys.Contains(t)).Distinct().ToList();
				if (skipped.Count > 0) {
					report.Warning(DofReportTopic.Unsupported, $"{cabinet.Name} defines toys libdof only builds from the ini files and skips here: {string.Join(", ", skipped)}.");
				}
			}
		}

		/// <summary>
		/// libdof takes the rows of the first matching tier for all devices: an exact ROM match anywhere ends the search,
		/// so a device with only a shorter row for that ROM stays dark for it. VPX behaves the same; the report says
		/// where it happens.
		/// </summary>
		private static void AnalyseRomMatching(IReadOnlyList<DofRomRow> rows, DofImportReport report)
		{
			// One device cannot shadow itself.
			if (rows.Select(r => r.Device).Distinct().Count() < 2) {
				return;
			}
			// Every row that can match a ROM is named like a prefix of it (ignoring case), so the candidates of a ROM are
			// the rows named like one of its prefixes, kept in file order.
			var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i < rows.Count; i++) {
				if (!byName.TryGetValue(rows[i].Rom, out var indices)) {
					byName[rows[i].Rom] = indices = new List<int>();
				}
				indices.Add(i);
			}
			// Only the lengths some row has can name a prefix, which keeps a long imported name from costing a lookup per
			// character.
			var lengths = byName.Keys.Select(k => k.Length).Where(l => l > 0).Distinct().OrderBy(l => l).ToArray();
			var examples = new List<string>();
			var count = 0;
			var candidateIndices = new List<int>();
			foreach (var rom in byName.Keys) {
				candidateIndices.Clear();
				foreach (var length in lengths) {
					if (length > rom.Length) {
						break;
					}
					if (byName.TryGetValue(rom.Substring(0, length), out var indices)) {
						candidateIndices.AddRange(indices);
					}
				}
				candidateIndices.Sort();
				var candidates = candidateIndices.Select(i => rows[i]).ToList();
				var matched = DofRomMatcher.Match(candidates, rom);
				var silenced = DofRomMatcher.Candidates(candidates, rom).Where(r => matched.All(m => m.Device != r.Device)).ToList();
				if (silenced.Count == 0) {
					continue;
				}
				count++;
				if (examples.Count < 10) {
					examples.Add($"{rom} (device {string.Join(", ", silenced.Select(s => $"{s.Device} row {s.Rom}"))})");
				}
			}
			if (count > 0) {
				report.Info(DofReportTopic.RomMatching, $"{count} ROM names match a row on one device exactly and only a shorter row on another, which then stays dark for that ROM: {string.Join("; ", examples)}{(count > examples.Count ? ", ..." : string.Empty)}.");
			}
		}

		#endregion

		#region Writing

		private void Write(ConfigSet config, DofImportReport report)
		{
			var revision = _store.CreateRevision();
			var published = false;
			try {
				Directory.CreateDirectory(revision.ConfigDir);
				Directory.CreateDirectory(revision.TablesDir);
				foreach (var ini in config.Inis.Values) {
					Copy(ini, revision.ConfigDir);
				}
				string cabinetName = null;
				if (config.Cabinet != null) {
					cabinetName = Copy(config.Cabinet, revision.ConfigDir);
				}
				if (config.TableMappings != null) {
					Copy(config.TableMappings, revision.ConfigDir);
				}
				string shapesName = null;
				if (config.Shapes != null) {
					shapesName = Copy(config.Shapes, revision.ConfigDir);
					if (config.ShapesBitmap != null) {
						File.Copy(config.ShapesBitmap.Path, Path.Combine(revision.ConfigDir, Path.GetFileNameWithoutExtension(shapesName) + ".png"));
					}
				}
				foreach (var image in config.Images.Values) {
					Copy(image, revision.ConfigDir);
				}
				var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var pair in config.TableConfigs.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) {
					var stem = DofRevision.SafeName(pair.Key);
					while (!used.Add(stem)) {
						stem += "_";
					}
					if (stem != pair.Key) {
						report.Info(DofReportTopic.Files, $"Table config {pair.Value.Name} is named {stem} in the player.");
					}
					File.Copy(pair.Value.Path, Path.Combine(revision.TablesDir, stem + ".xml"));
				}
				if (config.GlobalConfig != null) {
					var source = Path.Combine(revision.BasePath, DofRevision.SourceFolder);
					Directory.CreateDirectory(source);
					File.Copy(config.GlobalConfig.Path, Path.Combine(source, DofGlobalConfig.FileName));
				}
				XDocument global;
				try {
					global = DofGlobalConfig.Build(config.GlobalConfig?.Xml, revision.ConfigDir, revision.TablesDir, cabinetName, shapesName);
				} catch (DofException e) {
					report.Error(e.Message);
					return;
				}
				DofXml.Save(global, revision.GlobalConfigFile);

				IReadOnlyList<DofRomRow> rows;
				if (ValidateWithLibdof) {
					rows = Validate(revision, config, report);
					if (rows == null) {
						return;
					}
					// What libdof uses, for the catalog: it skips files VPE reads (a device without colours, for one).
					revision.WriteLibdofRows(rows);
				} else {
					rows = config.Inis.SelectMany(p => p.Value.Ini.Roms.Select(r => new DofRomRow(p.Key, r))).ToList();
				}
				AnalyseRomMatching(rows, report);
				File.WriteAllText(Path.Combine(revision.BasePath, DofRevision.ReportFile), report.Format(revision.Id));
				if (!string.IsNullOrEmpty(report.Origin)) {
					File.WriteAllText(Path.Combine(revision.BasePath, DofRevision.OriginFile), report.Origin, new UTF8Encoding(false));
				}
				_store.Activate(revision);
				published = true;
				report.Revision = revision.Id;
			} finally {
				if (!published) {
					_store.Discard(revision);
				}
			}
		}

		private static string Copy(ConfigFile file, string dir)
		{
			File.Copy(file.Path, Path.Combine(dir, file.Name));
			return file.Name;
		}

		/// <summary>
		/// Has libdof read the ini files of the revision, and compares its rows with VPE's reading. Returns libdof's rows,
		/// or null if the revision is rejected.
		/// </summary>
		private static IReadOnlyList<DofRomRow> Validate(DofRevision revision, ConfigSet config, DofImportReport report)
		{
			IReadOnlyList<DofRomRow> rows;
			try {
				rows = DofLibrary.RomNames(revision.BasePath);
			} catch (DofException e) {
				report.Error($"libdof could not read the configuration: {e.Message}");
				return null;
			}
			var expected = config.Inis.Sum(p => p.Value.Ini.Roms.Count);
			if (rows.Count == 0 && expected > 0) {
				report.Error("libdof found no table rows in the ini files.");
				return null;
			}
			if (config.Cabinet != null && !ValidateCabinet(revision, config.Cabinet, report)) {
				return null;
			}
			var devices = rows.Select(r => r.Device).Distinct().Count();
			report.Info(DofReportTopic.General, $"libdof reads {rows.Count} table rows for {devices} device{(devices == 1 ? string.Empty : "s")}.");
			foreach (var pair in config.Inis) {
				var read = rows.Count(r => r.Device == pair.Key);
				if (pair.Value.Ini.Roms.Count > 0 && read == 0) {
					report.Warning(DofReportTopic.General, $"libdof reads no table rows from {pair.Value.Name}; device {pair.Key} stays dark.");
				} else if (read != pair.Value.Ini.Roms.Count) {
					report.Info(DofReportTopic.General, $"libdof reads {read} of the {pair.Value.Ini.Roms.Count} table rows of {pair.Value.Name}.");
				}
			}
			return rows;
		}

		/// <summary>
		/// Has libdof read the controllers of the revision's cabinet config as a session will, and warns about each one it
		/// would skip. A type libdof does not support is reported by <see cref="AnalyseCabinet"/> already. Returns false if
		/// libdof cannot read the global config or the cabinet config, which would fail every session.
		/// </summary>
		private static bool ValidateCabinet(DofRevision revision, ConfigFile cabinet, DofImportReport report)
		{
			IReadOnlyList<DofCabinetController> controllers;
			try {
				controllers = DofLibrary.CabinetControllers(revision.BasePath);
			} catch (DofException e) {
				report.Error($"libdof could not read the configuration: {e.Message}");
				return false;
			}
			foreach (var controller in controllers) {
				if (controller.Loaded || !SupportedControllers.Contains(controller.Type)) {
					continue;
				}
				var name = string.IsNullOrEmpty(controller.Name) ? controller.Type : controller.Name;
				var reason = controller.Reason.TrimEnd('.');
				report.Warning(DofReportTopic.Unsupported, $"{cabinet.Name}: {name} is a {controller.Type} libdof can't read ({reason}); this controller definition is skipped.");
			}
			return true;
		}

		#endregion
	}
}
