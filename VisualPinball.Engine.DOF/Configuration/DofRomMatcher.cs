// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>
	/// The rows libdof uses for a ROM, as <c>LedControlConfigList::GetTableConfigDictionary</c> picks them: first the rows
	/// named exactly like the ROM (ignoring case), else rows whose name followed by "_" starts the ROM name (ignoring
	/// case), else rows whose name starts the ROM name (case-sensitive). The first tier with a match decides for every
	/// device, and each device takes its first matching row in file order.
	/// </summary>
	public static class DofRomMatcher
	{
		/// <param name="rows">The rows of all devices, in file order per device.</param>
		public static IReadOnlyList<DofRomRow> Match(IEnumerable<DofRomRow> rows, string rom)
		{
			if (string.IsNullOrEmpty(rom)) {
				return Array.Empty<DofRomRow>();
			}
			var list = rows as IList<DofRomRow> ?? rows.ToList();
			for (var tier = 0; tier < 3; tier++) {
				var matched = new List<DofRomRow>();
				foreach (var row in list) {
					if (Matches(tier, row.Rom, rom) && matched.All(m => m.Device != row.Device)) {
						matched.Add(row);
					}
				}
				if (matched.Count > 0) {
					return matched;
				}
			}
			return Array.Empty<DofRomRow>();
		}

		/// <summary>Every row that matches the ROM in any tier, whether or not libdof uses it.</summary>
		public static IReadOnlyList<DofRomRow> Candidates(IEnumerable<DofRomRow> rows, string rom)
		{
			if (string.IsNullOrEmpty(rom)) {
				return Array.Empty<DofRomRow>();
			}
			return rows.Where(r => Matches(0, r.Rom, rom) || Matches(1, r.Rom, rom) || Matches(2, r.Rom, rom)).ToList();
		}

		private static bool Matches(int tier, string row, string rom)
		{
			switch (tier) {
				case 0: return string.Equals(row, rom, StringComparison.OrdinalIgnoreCase);
				case 1: return rom.StartsWith(row + "_", StringComparison.OrdinalIgnoreCase);
				default: return rom.StartsWith(row, StringComparison.Ordinal);
			}
		}
	}
}
