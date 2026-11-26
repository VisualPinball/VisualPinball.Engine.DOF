// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VisualPinball.Engine.DOF.Test;

namespace VisualPinball.Engine.DOF.Parity
{
	/// <summary>
	/// Drives C# DirectOutput and libdof (VpeDof) with the same configuration and scenario, both writing to Art-Net
	/// controllers on localhost, and compares the outputs sampled at the same points of the script.
	/// </summary>
	internal static class Program
	{
		private const int Ports = 64;
		private const string Rom = "vpetest";

		private static int _universeBase = 1000;

		private static int Main(string[] args)
		{
			var options = Options.Parse(args);
			if (options == null) {
				Console.Error.WriteLine("usage: DofParity --csharp <CSharpRunner.exe> [--scenarios <folder>] [--report <file.md>]");
				Console.Error.WriteLine("                 [--config <directoutputconfig folder> --rom <rom> [--events 300] [--seed 1] [--tolerance 0]]");
				return 2;
			}

			var scenarios = new List<(Scenario scenario, string configSource, string rom)>();
			if (options.Scenarios != null) {
				foreach (var file in Directory.GetFiles(options.Scenarios, "*.dofscript").OrderBy(f => f, StringComparer.Ordinal)) {
					scenarios.Add((Scenario.Load(file), null, Rom));
				}
			}
			if (options.Config != null) {
				var generated = GenerateScenario(options);
				scenarios.Add((Scenario.Load(generated), options.Config, options.Rom));
			}

			using var capture = new ArtNetCapture();
			var report = new StringBuilder();
			report.AppendLine("# DOF parity: C# DirectOutput vs libdof");
			report.AppendLine();
			report.AppendLine($"libdof {DofLibrary.LibdofVersion}, {DateTime.Now:yyyy-MM-dd HH:mm}. Samples are taken after each wait of a scenario; a sample differs when any port differs.");
			report.AppendLine();
			report.AppendLine("| Scenario | Samples | Differing | Ports | Tolerance |");
			report.AppendLine("| --- | --- | --- | --- | --- |");
			var details = new StringBuilder();
			var failed = 0;

			foreach (var (scenario, configSource, rom) in scenarios) {
				var devices = new List<int>();
				var csharp = RunEngine(capture, scenario, configSource, rom, devices, (root, table, sample) => RunCSharp(options.CSharp, root, table, rom, scenario, sample));
				var libdof = RunEngine(capture, scenario, configSource, rom, devices, (root, table, sample) => RunLibdof(root, table, rom, scenario, sample));

				var differing = new List<string>();
				// Several devices can differ at one sample; the summary counts samples.
				var failingSamples = new HashSet<int>();
				var ports = new SortedSet<string>();
				for (var s = 0; s < scenario.Samples; s++) {
					foreach (var device in devices) {
						var a = csharp.TryGetValue((s, device), out var fa) ? fa : null;
						var b = libdof.TryGetValue((s, device), out var fb) ? fb : null;
						// Every engine sends a frame per controller when it connects, so a missing sample is a difference, not
						// a dark device.
						if (a == null || b == null) {
							failingSamples.Add(s);
							differing.Add($"sample {s}: device {device} has no frame from {(a == null && b == null ? "either engine" : a == null ? "C#" : "libdof")}");
							ports.Add($"{device}.*");
							continue;
						}
						var diffs = new List<string>();
						for (var p = 0; p < Ports; p++) {
							int va = a[p], vb = b[p];
							if (Math.Abs(va - vb) > scenario.Tolerance) {
								diffs.Add($"{device}.{p + 1}: {va} vs {vb}");
								ports.Add($"{device}.{p + 1}");
							}
						}
						if (diffs.Count > 0) {
							failingSamples.Add(s);
							differing.Add($"sample {s}: " + string.Join(", ", diffs));
						}
					}
				}
				report.AppendLine($"| {scenario.Name} | {scenario.Samples} | {failingSamples.Count} | {(ports.Count == 0 ? "-" : string.Join(" ", ports))} | {scenario.Tolerance} |");
				if (differing.Count > 0) {
					failed++;
					details.AppendLine($"## {scenario.Name}");
					details.AppendLine();
					details.AppendLine($"`{Path.GetFileName(scenario.File)}`{(scenario.Row != null ? $", row `{scenario.Row}`" : string.Empty)}. Values are C# vs libdof.");
					details.AppendLine();
					foreach (var line in differing.Take(40)) {
						details.AppendLine("- " + line);
					}
					if (differing.Count > 40) {
						details.AppendLine($"- ... {differing.Count - 40} more");
					}
					details.AppendLine();
				}
				Console.WriteLine($"{scenario.Name}: {scenario.Samples - failingSamples.Count}/{scenario.Samples} samples match");
			}

			report.AppendLine();
			report.Append(details);
			if (options.Report != null) {
				File.WriteAllText(options.Report, report.ToString());
				Console.WriteLine($"Report: {options.Report}");
			} else {
				Console.WriteLine(report);
			}
			return failed == 0 ? 0 : 1;
		}

		/// <summary>Builds a configuration on fresh universes, runs one engine on it and returns its samples per device.</summary>
		private static Dictionary<(int sample, int device), byte[]> RunEngine(ArtNetCapture capture, Scenario scenario, string configSource, string rom,
			List<int> devices, Action<string, string, Action<int>> run)
		{
			var root = Path.Combine(Path.GetTempPath(), "vpe-dof-parity", Guid.NewGuid().ToString("N"));
			var configDir = Path.Combine(root, "directoutputconfig");
			Directory.CreateDirectory(configDir);
			try {
				var found = WriteConfig(configDir, scenario, configSource, rom);
				devices.Clear();
				devices.AddRange(found.Keys);
				var samples = new ConcurrentDictionary<(int, int), byte[]>();
				run(root, Path.Combine(root, "tables", "parity.vpx"), sample => {
					foreach (var pair in found) {
						var latest = capture.Latest(pair.Value);
						if (latest != null) {
							samples[(sample, pair.Key)] = (byte[])latest.Clone();
						}
					}
				});
				return new Dictionary<(int, int), byte[]>(samples);
			} finally {
				try {
					Directory.Delete(root, true);
				} catch (IOException) {
				}
			}
		}

		/// <summary>
		/// Writes the global config, the ini files and a cabinet that maps each ini device to an Art-Net controller with
		/// its own universe. Returns the universe per device.
		/// </summary>
		private static Dictionary<int, int> WriteConfig(string configDir, Scenario scenario, string configSource, string rom)
		{
			File.WriteAllText(Path.Combine(configDir, "GlobalConfig_B2SServer.xml"),
				"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<GlobalConfig>\n  <IniFilesPath>{GlobalConfigDir}</IniFilesPath>\n"
				+ "  <CabinetConfigFilePattern>{GlobalConfigDir}/Cabinet.xml</CabinetConfigFilePattern>\n  <TableConfigFilePatterns />\n"
				+ "  <EnableLogging>false</EnableLogging>\n</GlobalConfig>\n");

			var devices = new List<int>();
			if (scenario.Row != null) {
				File.WriteAllText(Path.Combine(configDir, "directoutputconfig.ini"),
					"[version]\r\nversion=1\r\n\r\n[Colors DOF]\r\nRed=#FF0000FF\r\nGreen=#00FF00FF\r\nBlue=#0000FFFF\r\nWhite=#FFFFFFFF\r\n"
					+ "Orange=#FF8000FF\r\nYellow=#FFFF00FF\r\nPurple=#8000FFFF\r\nCyan=#00FFFFFF\r\n\r\n"
					+ "[Variables DOF]\r\n\r\n[Config DOF]\r\n" + rom + "," + scenario.Row + "\r\n");
				devices.Add(1);
			} else {
				foreach (var file in Directory.GetFiles(configSource)) {
					var name = Path.GetFileName(file);
					var match = Regex.Match(name, @"^directoutputconfig(\d*)\.ini$", RegexOptions.IgnoreCase);
					if (match.Success) {
						devices.Add(match.Groups[1].Value.Length == 0 ? 1 : int.Parse(match.Groups[1].Value));
						File.Copy(file, Path.Combine(configDir, name));
					}
				}
			}

			var universes = new Dictionary<int, int>();
			var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Cabinet>\n  <Name>Parity</Name>\n  <AutoConfigEnabled>false</AutoConfigEnabled>\n  <OutputControllers>\n");
			foreach (var device in devices) {
				universes[device] = ++_universeBase;
				xml.Append($"    <ArtNet><Name>ArtNet{device}</Name><Universe>{universes[device]}</Universe><BroadcastAddress>127.0.0.1</BroadcastAddress></ArtNet>\n");
			}
			xml.Append("  </OutputControllers>\n  <Toys>\n");
			foreach (var device in devices) {
				xml.Append($"    <LedWizEquivalent><Name>LedWizEquivalent {device}</Name><LedWizNumber>{device}</LedWizNumber><Outputs>\n");
				for (var p = 1; p <= Ports; p++) {
					xml.Append($"      <LedWizEquivalentOutput><OutputName>ArtNet{device}.{p:00}</OutputName><LedWizEquivalentOutputNumber>{p}</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>\n");
				}
				xml.Append("    </Outputs></LedWizEquivalent>\n");
			}
			xml.Append("  </Toys>\n</Cabinet>\n");
			File.WriteAllText(Path.Combine(configDir, "Cabinet.xml"), xml.ToString());
			return universes;
		}

		private static void RunLibdof(string root, string table, string rom, Scenario scenario, Action<int> sample)
		{
			using var dof = DofInstance.Create(root);
			dof.Init(table, rom);
			scenario.Run(dof.Data, dof.DataNamed, sample);
			dof.Finish();
		}

		private static void RunCSharp(string runner, string root, string table, string rom, Scenario scenario, Action<int> sample)
		{
			var info = new ProcessStartInfo(runner) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(runner),
			};
			foreach (var arg in new[] { root, table, rom, scenario.File }) {
				info.ArgumentList.Add(arg);
			}
			using var process = Process.Start(info);
			var errors = new StringBuilder();
			process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
			process.BeginErrorReadLine();
			string line;
			while ((line = process.StandardOutput.ReadLine()) != null) {
				if (line.StartsWith("SAMPLE ", StringComparison.Ordinal)) {
					// The runner waits for the acknowledgement, so its script does not move on before the sample is taken.
					sample(int.Parse(line.Substring(7)));
					process.StandardInput.WriteLine("ACK");
					process.StandardInput.Flush();
				}
			}
			process.WaitForExit();
			if (process.ExitCode != 0) {
				throw new InvalidOperationException($"C# DOF runner failed ({process.ExitCode}): {errors}");
			}
		}

		/// <summary>
		/// A random script over the table elements the ROM's rows use: element changes with waits between them, from a
		/// fixed seed so both engines get the same script.
		/// </summary>
		private static string GenerateScenario(Options options)
		{
			var elements = new SortedSet<string>(StringComparer.Ordinal);
			foreach (var file in Directory.GetFiles(options.Config, "directoutputconfig*.ini")) {
				foreach (var line in File.ReadLines(file)) {
					if (!line.StartsWith(options.Rom + ",", StringComparison.OrdinalIgnoreCase)) {
						continue;
					}
					foreach (Match m in Regex.Matches(line, @"(?<![A-Za-z#@])([SLWGN])(\d{1,3})(?![\d])")) {
						elements.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
					}
				}
			}
			if (elements.Count == 0) {
				throw new InvalidOperationException($"No row for ROM \"{options.Rom}\" with table elements in {options.Config}.");
			}
			var list = elements.ToList();
			var random = new Random(options.Seed);
			var state = new Dictionary<string, int>();
			var script = new StringBuilder($"name: {options.Rom} (random, seed {options.Seed}, {list.Count} elements)\ntolerance: {options.Tolerance}\n");
			for (var i = 0; i < options.Events; i++) {
				var element = list[random.Next(list.Count)];
				var value = state.TryGetValue(element, out var v) && v != 0 ? 0 : 1;
				state[element] = value;
				script.Append($"{element} {value}\n");
				if (random.Next(3) == 0) {
					script.Append($"wait {random.Next(20, 250)}\n");
				}
			}
			foreach (var element in list) {
				script.Append($"{element} 0\n");
			}
			script.Append("wait 400\n");
			var path = Path.Combine(Path.GetTempPath(), $"vpe-dof-parity-{options.Rom}-{options.Seed}.dofscript");
			File.WriteAllText(path, script.ToString());
			return path;
		}

		private sealed class Options
		{
			public string CSharp;
			public string Scenarios;
			public string Report;
			public string Config;
			public string Rom;
			public int Events = 300;
			public int Seed = 1;
			public int Tolerance;

			public static Options Parse(string[] args)
			{
				var o = new Options();
				for (var i = 0; i + 1 < args.Length; i += 2) {
					switch (args[i]) {
						case "--csharp": o.CSharp = Path.GetFullPath(args[i + 1]); break;
						case "--scenarios": o.Scenarios = args[i + 1]; break;
						case "--report": o.Report = args[i + 1]; break;
						case "--config": o.Config = args[i + 1]; break;
						case "--rom": o.Rom = args[i + 1]; break;
						case "--events": o.Events = int.Parse(args[i + 1]); break;
						case "--seed": o.Seed = int.Parse(args[i + 1]); break;
						case "--tolerance": o.Tolerance = int.Parse(args[i + 1]); break;
						default: return null;
					}
				}
				if (o.CSharp == null || (o.Config != null) != (o.Rom != null)) {
					return null;
				}
				if (o.Scenarios == null && o.Config == null) {
					o.Scenarios = Path.Combine(AppContext.BaseDirectory, "scenarios");
				}
				return o;
			}
		}
	}
}
