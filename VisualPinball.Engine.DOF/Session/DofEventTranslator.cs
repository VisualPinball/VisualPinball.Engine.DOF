// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;

namespace VisualPinball.Engine.DOF.Session
{
	/// <summary>
	/// Turns element changes into DOF calls with VPX's value semantics: switches 0 or 1, solenoids, lamps and GI as the
	/// game logic reports them, mechs as integers. Only changes are passed on; a value equal to the last one sent for
	/// the element is dropped. Every element starts unknown, so the first value of each, zero included, is sent.
	/// </summary>
	/// <remarks>Used by the session worker only; not thread-safe.</remarks>
	internal sealed class DofEventTranslator
	{
		private const int DirectRange = 2048;
		private const int Unknown = int.MinValue;
		private const int Kinds = (int)DofElementKind.SnapshotBoundary;

		private readonly int[][] _direct = new int[Kinds][];
		private readonly Dictionary<long, int> _other = new Dictionary<long, int>();

		public DofEventTranslator()
		{
			for (var k = 0; k < Kinds; k++) {
				_direct[k] = new int[DirectRange];
				for (var i = 0; i < DirectRange; i++) {
					_direct[k][i] = Unknown;
				}
			}
		}

		public static char TypeOf(DofElementKind kind)
		{
			switch (kind) {
				case DofElementKind.Solenoid: return 'S';
				case DofElementKind.Switch: return 'W';
				case DofElementKind.Lamp: return 'L';
				case DofElementKind.GI: return 'G';
				case DofElementKind.Mech: return 'N';
				case DofElementKind.Event: return 'E';
				case DofElementKind.Display: return 'D';
				default: return '\0';
			}
		}

		/// <summary>Normalises the value and returns whether it differs from the last one sent for the element.</summary>
		public bool Translate(DofElementKind kind, int number, ref int value)
		{
			if (kind >= DofElementKind.SnapshotBoundary) {
				return false;
			}
			if (kind == DofElementKind.Switch) {
				value = value != 0 ? 1 : 0;
			}
			if (number >= 0 && number < DirectRange) {
				ref var last = ref _direct[(int)kind][number];
				if (last == value) {
					return false;
				}
				last = value;
				return true;
			}
			var key = ((long)kind << 32) | (uint)number;
			if (_other.TryGetValue(key, out var previous) && previous == value) {
				return false;
			}
			_other[key] = value;
			return true;
		}
	}
}
