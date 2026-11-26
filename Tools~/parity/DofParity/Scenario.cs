// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace VisualPinball.Engine.DOF.Parity
{
	/// <summary>
	/// A parity scenario: an ini row and a script of table element changes and waits. Both engines run the same script;
	/// the outputs are sampled at the end of every wait.
	/// </summary>
	/// <remarks>
	/// File format, one item per line. A line starting with '#' is a comment; a '#' anywhere else is part of the line,
	/// as in "I#80" or "#FF8000":
	/// <code>
	/// name: inverted solenoid
	/// row: S3 Invert,S3        (the ini columns after the ROM name)
	/// tolerance: 40            (largest difference per port still counted as equal, for timed effects like fades)
	/// S 3 1                    (type, number, value)
	/// $flipper 1               (named element)
	/// wait 150                 (sleep, then take a sample)
	/// </code>
	/// A scenario without a row runs against an existing configuration (see the orchestrator's --config option).
	/// </remarks>
	public sealed class Scenario
	{
		private abstract class Step { }
		private sealed class DataStep : Step { public char Type; public int Number; public int Value; }
		private sealed class NamedStep : Step { public string Name; public int Value; }
		private sealed class WaitStep : Step { public int Milliseconds; }

		/// <summary>Time between init and the first sample, so startup effects have settled.</summary>
		public const int SettleMs = 300;

		private readonly List<Step> _steps = new List<Step>();

		public string Name { get; private set; }
		public string Row { get; private set; }
		public int Tolerance { get; private set; }
		public string File { get; private set; }

		/// <summary>Samples taken: one after settling and one per wait.</summary>
		public int Samples { get; private set; } = 1;

		public static Scenario Load(string path)
		{
			var scenario = new Scenario { File = Path.GetFullPath(path), Name = Path.GetFileNameWithoutExtension(path) };
			var lineNumber = 0;
			foreach (var raw in System.IO.File.ReadAllLines(path)) {
				lineNumber++;
				var line = raw.Trim();
				if (line.Length == 0 || line[0] == '#') {
					continue;
				}
				if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase)) {
					scenario.Name = line.Substring(5).Trim();
					continue;
				}
				if (line.StartsWith("tolerance:", StringComparison.OrdinalIgnoreCase)) {
					scenario.Tolerance = Int(line.Substring(10).Trim(), path, lineNumber);
					continue;
				}
				if (line.StartsWith("row:", StringComparison.OrdinalIgnoreCase)) {
					scenario.Row = line.Substring(4).Trim();
					continue;
				}
				var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts[0].Equals("wait", StringComparison.OrdinalIgnoreCase) && parts.Length == 2) {
					scenario._steps.Add(new WaitStep { Milliseconds = Int(parts[1], path, lineNumber) });
					scenario.Samples++;
				} else if (parts[0].StartsWith("$", StringComparison.Ordinal) && parts.Length == 2) {
					scenario._steps.Add(new NamedStep { Name = parts[0].Substring(1), Value = Int(parts[1], path, lineNumber) });
				} else if (parts.Length == 3 && parts[0].Length == 1) {
					scenario._steps.Add(new DataStep {
						Type = char.ToUpperInvariant(parts[0][0]), Number = Int(parts[1], path, lineNumber), Value = Int(parts[2], path, lineNumber),
					});
				} else {
					throw new FormatException($"{path}:{lineNumber}: cannot read \"{line}\"");
				}
			}
			return scenario;
		}

		/// <summary>Runs the script, calling <paramref name="sample"/> with the sample number after settling and after each wait.</summary>
		public void Run(Action<char, int, int> data, Action<string, int> named, Action<int> sample)
		{
			Thread.Sleep(SettleMs);
			var n = 0;
			sample(n++);
			foreach (var step in _steps) {
				switch (step) {
					case DataStep d:
						data(d.Type, d.Number, d.Value);
						break;
					case NamedStep s:
						named(s.Name, s.Value);
						break;
					case WaitStep w:
						Thread.Sleep(w.Milliseconds);
						sample(n++);
						break;
				}
			}
		}

		private static int Int(string value, string path, int line)
		{
			if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)) {
				throw new FormatException($"{path}:{line}: \"{value}\" is not a number");
			}
			return result;
		}
	}
}
