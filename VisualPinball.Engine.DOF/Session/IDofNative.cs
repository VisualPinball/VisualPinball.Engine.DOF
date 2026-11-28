// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;

namespace VisualPinball.Engine.DOF.Session
{
	/// <summary>The calls a <see cref="DofSession"/> makes on its DOF instance; <see cref="DofInstance"/> outside tests.</summary>
	public interface IDofNative : IDisposable
	{
		/// <summary>Rewires a device's ports before <see cref="Init"/>; see <see cref="DofInstance.SetOutputMap"/>.</summary>
		void SetOutputMap(int device, IReadOnlyList<int> targets);
		void Init(string tableFile, string romName);
		void Data(char type, int number, int value);
		/// <summary>A named element's change; <paramref name="name"/> is a name, never a numbered element like "S1".</summary>
		void DataNamed(string name, int value);
		int PendingInput { get; }
		void Finish();
		long SetOutputGate(bool open);
		/// <summary>
		/// Closes the gate at once and for good: queued openings are superseded, later changes apply as closings. See
		/// <see cref="DofInstance.LatchOutputGate"/>. Called from any thread, also while another call on the instance runs
		/// or it is disposed (then it returns -1), and never blocks.
		/// </summary>
		long LatchOutputGate();
		DofGateState GetGateState(long generation);
		IReadOnlyList<string> GetGatePending(long generation);
		DofCabinetDescription Describe();
	}

	/// <summary>The real thing: a <see cref="DofInstance"/>.</summary>
	internal sealed class DofInstanceNative : IDofNative
	{
		private readonly DofInstance _instance;

		public DofInstanceNative(DofInstance instance) => _instance = instance;

		public static IDofNative Create(string basePath) => new DofInstanceNative(DofInstance.Create(basePath));

		public void SetOutputMap(int device, IReadOnlyList<int> targets) => _instance.SetOutputMap(device, targets);
		public void Init(string tableFile, string romName) => _instance.Init(tableFile, romName);
		public void Data(char type, int number, int value) => _instance.Data(type, number, value);
		public void DataNamed(string name, int value) => _instance.DataNamed(name, value);
		public int PendingInput => _instance.PendingInput;
		public void Finish() => _instance.Finish();
		public long SetOutputGate(bool open) => _instance.SetOutputGate(open);
		public long LatchOutputGate() => _instance.LatchOutputGate();
		public DofGateState GetGateState(long generation) => _instance.GetGateState(generation);
		public IReadOnlyList<string> GetGatePending(long generation) => _instance.GetGatePending(generation);
		public DofCabinetDescription Describe() => _instance.Describe();
		public void Dispose() => _instance.Dispose();
	}
}
