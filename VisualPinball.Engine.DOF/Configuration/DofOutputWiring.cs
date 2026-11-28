// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>
	/// How the cabinet's cabling differs from what the configuration says: for each ini device, the output each of its ports
	/// drives. Port <c>p</c> of device <c>N</c> (directoutputconfigN.ini) drives what port <c>OutputOf(N, p)</c> drives in the
	/// cabinet file. Each device's map is a permutation, so every output stays reachable from exactly one port.
	/// </summary>
	/// <remarks>
	/// Kept next to the revisions (<c>&lt;root&gt;/output-wiring.txt</c>, one <c>device=output,output,...</c> line per device),
	/// for every configuration: it describes the cabinet, not one import. Immutable; changes return a new wiring.
	/// </remarks>
	public sealed class DofOutputWiring
	{
		public const string FileName = "output-wiring.txt";

		/// <summary>The highest output a map can name; the native layer takes no longer maps.</summary>
		public const int MaxOutput = DofCatalog.MaxCabinetOutput;

		/// <summary>The highest device a map can be for, as the native layer takes them.</summary>
		public const int MaxDevice = 999;

		public static readonly DofOutputWiring None = new DofOutputWiring(new Dictionary<int, int[]>());

		// By device: the output of port i + 1 at index i; trailing ports that drive their own output are left out.
		private readonly IReadOnlyDictionary<int, int[]> _maps;

		private DofOutputWiring(IReadOnlyDictionary<int, int[]> maps) { _maps = maps; }

		/// <summary>Whether every port drives its own output.</summary>
		public bool IsEmpty => _maps.Count == 0;

		/// <summary>The devices with a map, in order.</summary>
		public IEnumerable<int> Devices => _maps.Keys.OrderBy(d => d);

		/// <summary>The output port <paramref name="port"/> of the device drives.</summary>
		public int OutputOf(int device, int port)
			=> _maps.TryGetValue(device, out var map) && port >= 1 && port <= map.Length ? map[port - 1] : port;

		/// <summary>The port that drives output <paramref name="output"/> of the device.</summary>
		public int PortOn(int device, int output)
		{
			if (_maps.TryGetValue(device, out var map) && output >= 1 && output <= map.Length) {
				var index = Array.IndexOf(map, output);
				if (index >= 0) {
					return index + 1;
				}
			}
			return output;
		}

		/// <summary>The highest port or output the device's map names; 0 without a map.</summary>
		public int Extent(int device) => _maps.TryGetValue(device, out var map) ? map.Length : 0;

		/// <summary>The device's map for the native layer: the output of port i + 1 at index i; empty without a map.</summary>
		public IReadOnlyList<int> MapOf(int device) => _maps.TryGetValue(device, out var map) ? (int[])map.Clone() : Array.Empty<int>();

		/// <summary>Every device's map, as <see cref="MapOf"/> gives it.</summary>
		public IReadOnlyDictionary<int, IReadOnlyList<int>> Maps => Devices.ToDictionary(d => d, MapOf);

		/// <summary>
		/// Swaps what drives two runs of outputs of a device: <paramref name="count"/> outputs from <paramref name="first"/> on
		/// with as many from <paramref name="second"/> on, so the ports that drove the first run drive the second, in order, and
		/// the other way around.
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException">
		/// The runs overlap or leave 1 to <see cref="MaxOutput"/>, or the device is outside 1 to <see cref="MaxDevice"/>.
		/// </exception>
		public DofOutputWiring Swap(int device, int first, int second, int count)
		{
			if (device < 1 || device > MaxDevice) {
				throw new ArgumentOutOfRangeException(nameof(device), $"Device {device} can't be rearranged: DOF takes devices 1 to {MaxDevice}.");
			}
			if (count < 1 || first < 1 || second < 1 || first + count - 1 > MaxOutput || second + count - 1 > MaxOutput) {
				throw new ArgumentOutOfRangeException(nameof(count), "The outputs are outside the board.");
			}
			if (first < second + count && second < first + count) {
				throw new ArgumentOutOfRangeException(nameof(count), "The outputs overlap.");
			}
			var size = Math.Max(Extent(device), Math.Max(first, second) + count - 1);
			var map = new int[size];
			for (var port = 1; port <= size; port++) {
				map[port - 1] = OutputOf(device, port);
			}
			for (var k = 0; k < count; k++) {
				var a = PortOn(device, first + k);
				var b = PortOn(device, second + k);
				map[a - 1] = second + k;
				map[b - 1] = first + k;
			}
			return With(device, map);
		}

		/// <summary>The wiring without the device's map, so its ports drive their own outputs again.</summary>
		public DofOutputWiring Without(int device) => With(device, Array.Empty<int>());

		private DofOutputWiring With(int device, int[] map)
		{
			var maps = _maps.ToDictionary(e => e.Key, e => e.Value);
			var length = map.Length;
			while (length > 0 && map[length - 1] == length) {
				length--;
			}
			if (length == 0) {
				maps.Remove(device);
			} else {
				maps[device] = map.Take(length).ToArray();
			}
			return new DofOutputWiring(maps);
		}

		/// <summary>
		/// Reads the wiring of a store's folder; none when it has no file. A line that is not a device's permutation is left
		/// out, so its device drives its own outputs.
		/// </summary>
		public static DofOutputWiring Load(string root)
		{
			var path = Path.Combine(root, FileName);
			if (Directory.Exists(path)) {
				throw new IOException(path + " is a folder, not the output wiring.");
			}
			if (!File.Exists(path)) {
				return None;
			}
			var maps = new Dictionary<int, int[]>();
			foreach (var line in File.ReadAllLines(path)) {
				var separator = line.IndexOf('=');
				if (separator <= 0 || !int.TryParse(line.Substring(0, separator).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var device)
					|| device < 1 || device > MaxDevice) {
					continue;
				}
				var map = Parse(line.Substring(separator + 1));
				if (map != null) {
					maps[device] = map;
				}
			}
			var wiring = None;
			foreach (var entry in maps) {
				wiring = wiring.With(entry.Key, entry.Value);
			}
			return wiring;
		}

		// A permutation of 1 to its length, or null.
		private static int[] Parse(string text)
		{
			// an empty entry is no output, and leaves the line out
			var parts = text.Split(',');
			if (parts.Length > MaxOutput) {
				return null;
			}
			var map = new int[parts.Length];
			var seen = new bool[parts.Length + 1];
			for (var i = 0; i < parts.Length; i++) {
				if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var output)
					|| output < 1 || output > parts.Length || seen[output]) {
					return null;
				}
				seen[output] = true;
				map[i] = output;
			}
			return map;
		}

		/// <summary>Replaces the wiring file of a store's folder in one step; deletes it when every port drives its own output.</summary>
		public void Save(string root)
		{
			var path = Path.Combine(root, FileName);
			if (IsEmpty) {
				if (File.Exists(path)) {
					File.Delete(path);
				}
				return;
			}
			Directory.CreateDirectory(root);
			var text = new StringBuilder();
			foreach (var device in Devices) {
				text.Append(device.ToString(CultureInfo.InvariantCulture)).Append('=')
					.Append(string.Join(",", _maps[device].Select(o => o.ToString(CultureInfo.InvariantCulture)))).Append('\n');
			}
			var temp = path + ".tmp";
			File.WriteAllText(temp, text.ToString());
			if (File.Exists(path)) {
				File.Replace(temp, path, null);
			} else {
				File.Move(temp, path);
			}
		}
	}
}
