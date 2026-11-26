// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using VisualPinball.Engine.Game.Engines;
using VisualPinball.Plugins;
using VisualPinball.Unity;

namespace VisualPinball.Engine.DOF.Unity
{
	/// <summary>What a <see cref="DofTrigger"/> listens to.</summary>
	public enum DofTriggerSource
	{
		Switch,
		Coil,
		Lamp,
		/// <summary>A GI string, which the gamelogic engine reports as a lamp; its IDs can be the same as a lamp's.</summary>
		GI,
	}

	/// <summary>What a <see cref="DofTrigger"/> drives.</summary>
	public enum DofTriggerTarget
	{
		/// <summary>A numbered event, <c>E</c> in DOF configurations.</summary>
		Event,
		/// <summary>A named element, <c>$name</c> in DOF configurations.</summary>
		Named,
	}

	/// <summary>How a <see cref="DofTrigger"/> turns its source into a value.</summary>
	public enum DofTriggerMode
	{
		/// <summary>On while the source is on, off while it is off.</summary>
		Follow,
		/// <summary>On and off again each time the source turns on, as VPX's <c>DOFPulse</c>.</summary>
		Pulse,
		/// <summary>Turns on each time the source turns on, and stays on.</summary>
		On,
		/// <summary>Turns off each time the source turns on.</summary>
		Off,
	}

	/// <summary>One mapping of a switch, coil or lamp of the table to a DOF event or named element.</summary>
	[Serializable]
	public class DofTrigger
	{
		[Tooltip("The kind of element that triggers DOF. GI strings are separate from lamps, whose IDs they can share.")]
		public DofTriggerSource Source;

		[Tooltip("The ID of the switch, coil, lamp or GI string, as the gamelogic engine names it.")]
		public string ElementId = string.Empty;

		[Tooltip("Whether to drive a numbered event (E) or a named element ($name).")]
		public DofTriggerTarget Target;

		[Tooltip("The number of the event, as in E101.")]
		public int EventNumber;

		[Tooltip("The name of the element, with or without the dollar sign.")]
		public string Name = string.Empty;

		[Tooltip("How the source's state becomes the DOF value.")]
		public DofTriggerMode Mode;

		// Methods rather than properties, so the package's JSON carries the fields only.

		/// <summary>The name without the dollar sign and surrounding spaces.</summary>
		public string ElementName() => (Name ?? string.Empty).Trim().TrimStart('$').Trim();

		/// <summary>Why the trigger cannot drive anything, or null if it can.</summary>
		public string Problem()
		{
			if (string.IsNullOrWhiteSpace(ElementId)) {
				return "No element is selected.";
			}
			if (Target == DofTriggerTarget.Event && EventNumber <= 0) {
				return "Event numbers start at 1.";
			}
			if (Target == DofTriggerTarget.Named && ElementName().Length == 0) {
				return "The name is empty.";
			}
			return null;
		}
	}

	/// <summary>
	/// Drives DOF events and named elements from the table's switches, coils and lamps, as VPX tables do from their scripts
	/// with <c>DOF</c> calls. It also gives tables without a ROM the game name their DOF configuration rows use.
	/// </summary>
	/// <remarks>
	/// The DOF plugin binds the component when a game starts and drives the cabinet-trigger capability
	/// (<see cref="CabinetTriggerCapability"/>) with it. Without the plugin, or without an imported configuration, the
	/// component does nothing.
	/// </remarks>
	[PackAs("DofTriggers")]
	[AddComponentMenu("Pinball/Cabinet/DOF Triggers")]
	[DisallowMultipleComponent]
	public class DofTriggerComponent : MonoBehaviour, IPackable
	{
		[Tooltip("The name the DOF configuration rows use for this table, for tables without a ROM. Leave empty to use the ROM or the table mappings.")]
		public string GameName = string.Empty;

		[Tooltip("The switches, coils and lamps that drive DOF events and named elements.")]
		public List<DofTrigger> Triggers = new List<DofTrigger>();

		/// <summary>
		/// Starts driving <paramref name="triggers"/> from the engine's events with this component's triggers. Dispose the
		/// result to stop. Engine events may arrive on any thread, the simulation thread included, and none of them waits
		/// for another (<see cref="DofTriggerBinding"/>). A game with several components binds them together
		/// (<see cref="BindAll"/>).
		/// </summary>
		/// <param name="triggers">Where the values go.</param>
		/// <param name="engine">The table's gamelogic engine for its switches and coils (and its lamps without
		/// <paramref name="lamps"/>), or null to drive the binding only through <see cref="DofTriggerBinding.Apply"/>.</param>
		/// <param name="lamps">The table's lamps, which report every lamp change whichever path it takes (PinMAME's threaded
		/// playback sets lamps without the engine's lamp events), or null to follow the engine's lamp events.</param>
		/// <param name="numericIds">Whether the engine numbers its elements as PinMAME does, where <c>1</c> and <c>01</c>
		/// are the same switch, coil or lamp and either can come: a numeric ID then matches every spelling of its number.</param>
		public DofTriggerBinding Bind(IPluginCabinetTriggers triggers, IGamelogicEngine engine, LampPlayer lamps = null, bool numericIds = false)
			=> new DofTriggerBinding(Triggers, triggers, engine, lamps, numericIds);

		/// <summary>
		/// Starts driving <paramref name="triggers"/> with the triggers of all <paramref name="components"/> of a game in
		/// one binding: one subscription to each engine event and one queue, so each report reaches the triggers of every
		/// component together. Bound one by one, a report held between two components could reach the second after a
		/// newer one did, and leave it on the older state. The parameters are those of <see cref="Bind"/>.
		/// </summary>
		public static DofTriggerBinding BindAll(IEnumerable<DofTriggerComponent> components, IPluginCabinetTriggers triggers, IGamelogicEngine engine,
			LampPlayer lamps = null, bool numericIds = false)
		{
			var all = new List<DofTrigger>();
			foreach (var component in components ?? Array.Empty<DofTriggerComponent>()) {
				if (component != null && component.Triggers != null) {
					all.AddRange(component.Triggers);
				}
			}
			return new DofTriggerBinding(all, triggers, engine, lamps, numericIds);
		}

		#region Packaging

		public byte[] Pack() => DofTriggersPackable.Pack(this);

		public byte[] PackReferences(Transform root, PackagedRefs refs, PackagedFiles files) => null;

		public void Unpack(byte[] bytes) => DofTriggersPackable.Unpack(bytes, this);

		public void UnpackReferences(byte[] bytes, Transform root, PackagedRefs refs, PackagedFiles files) { }

		#endregion
	}

	/// <summary>The packaged form of a <see cref="DofTriggerComponent"/>.</summary>
	public struct DofTriggersPackable
	{
		public string GameName;
		public List<DofTrigger> Triggers;

		public static byte[] Pack(DofTriggerComponent comp)
			=> PackageApi.Packer.Pack(new DofTriggersPackable {
				GameName = comp.GameName,
				Triggers = comp.Triggers,
			});

		public static void Unpack(byte[] bytes, DofTriggerComponent comp)
		{
			var data = PackageApi.Packer.Unpack<DofTriggersPackable>(bytes);
			comp.GameName = data.GameName ?? string.Empty;
			comp.Triggers = data.Triggers ?? new List<DofTrigger>();
		}
	}

	/// <summary>
	/// The running triggers of a game's <see cref="DofTriggerComponent"/>s, all in one: turns element changes into
	/// cabinet-trigger calls.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The engine's events come from any thread: the simulation thread, and the main thread for the same switch (the
	/// debug shortcuts, say). Each report, the element and its state, goes into one queue, without a lock, and one
	/// thread at a time resolves the queue in order: all the triggers of the element at once, the Follow triggers with
	/// the report's state and the Pulse, On and Off triggers on its rising edge. The thread that reports resolves the
	/// queue when no other thread does; when one does, it only queues, and that thread resolves its report after the
	/// ones before it. No thread waits for another, and the triggers of an element always end on its last report.
	/// </para>
	/// <para>
	/// A fast on and off are two reports, so a Pulse trigger still sees the rising edge. DOF's own last value drops the
	/// repeats Follow triggers pass on.
	/// </para>
	/// </remarks>
	public sealed class DofTriggerBinding : IDisposable
	{
		private readonly Dictionary<(DofTriggerSource, string), int[]> _bySource = new Dictionary<(DofTriggerSource, string), int[]>();
		// With numeric IDs, the triggers of a numeric ID by its number; they are not in _bySource then.
		private readonly Dictionary<(DofTriggerSource, int), int[]> _byNumber;
		private readonly DofTriggerTarget[] _targets;
		private readonly DofTriggerMode[] _modes;
		private readonly int[] _numbers;
		// Named targets without the dollar sign, as DOF's session takes them.
		private readonly string[] _names;
		// Whether the element was on in the last report resolved; only the resolving thread uses it.
		private readonly int[] _on;
		// The reports to resolve, by the triggers of their element, in the order they came.
		private readonly ConcurrentQueue<(int[] indices, bool on)> _reports = new ConcurrentQueue<(int[], bool)>();
		// 1 while a thread resolves the reports.
		private int _resolving;
		private readonly IPluginCabinetTriggers _target;
		private readonly IGamelogicEngine _engine;
		private readonly LampPlayer _lamps;
		private int _disposed;

		internal DofTriggerBinding(IEnumerable<DofTrigger> triggers, IPluginCabinetTriggers target, IGamelogicEngine engine, LampPlayer lamps = null,
			bool numericIds = false)
		{
			_target = target ?? throw new ArgumentNullException(nameof(target));
			var usable = new List<DofTrigger>();
			var indices = new Dictionary<(DofTriggerSource, string), List<int>>();
			var numbered = numericIds ? new Dictionary<(DofTriggerSource, int), List<int>>() : null;
			foreach (var trigger in triggers ?? Array.Empty<DofTrigger>()) {
				if (trigger == null || trigger.Problem() != null) {
					continue;
				}
				var id = trigger.ElementId.Trim();
				List<int> list;
				if (numbered != null && TryNumber(id, out var number)) {
					if (!numbered.TryGetValue((trigger.Source, number), out list)) {
						numbered[(trigger.Source, number)] = list = new List<int>();
					}
				} else if (!indices.TryGetValue((trigger.Source, id), out list)) {
					indices[(trigger.Source, id)] = list = new List<int>();
				}
				list.Add(usable.Count);
				usable.Add(trigger);
			}
			_targets = new DofTriggerTarget[usable.Count];
			_modes = new DofTriggerMode[usable.Count];
			_numbers = new int[usable.Count];
			_names = new string[usable.Count];
			for (var i = 0; i < usable.Count; i++) {
				_targets[i] = usable[i].Target;
				_modes[i] = usable[i].Mode;
				_numbers[i] = usable[i].EventNumber;
				_names[i] = usable[i].ElementName();
			}
			_on = new int[usable.Count];
			foreach (var entry in indices) {
				_bySource[entry.Key] = entry.Value.ToArray();
			}
			if (numbered != null) {
				_byNumber = new Dictionary<(DofTriggerSource, int), int[]>();
				foreach (var entry in numbered) {
					_byNumber[entry.Key] = entry.Value.ToArray();
				}
			}
			if (usable.Count == 0) {
				return;
			}
			_engine = engine;
			_lamps = lamps;
			if (_engine != null) {
				_engine.OnCoilChanged += OnCoil;
				_engine.OnSwitchChanged += OnSwitch;
				if (_lamps == null) {
					_engine.OnLampChanged += OnLamp;
					_engine.OnLampsChanged += OnLamps;
				}
			}
			if (_lamps != null) {
				_lamps.LampChanged += OnLamp;
			}
		}

		/// <summary>The triggers that can drive something; the others are skipped.</summary>
		public int Count => _targets.Length;

		/// <summary>
		/// Reports the state of an element. Follow triggers pass it on; Pulse, On and Off react when the element turns on.
		/// Any thread, without waiting: the report is resolved after the ones before it, by this thread or by the one
		/// resolving them.
		/// </summary>
		public void Apply(DofTriggerSource source, string id, bool on)
		{
			if (id == null || Volatile.Read(ref _disposed) != 0) {
				return;
			}
			int[] indices;
			if (_byNumber != null && TryNumber(id, out var number)) {
				if (!_byNumber.TryGetValue((source, number), out indices)) {
					return;
				}
			} else if (!_bySource.TryGetValue((source, id), out indices)) {
				return;
			}
			_reports.Enqueue((indices, on));
			Resolve();
		}

		/// <summary>
		/// Resolves the queued reports unless another thread does. A report queued while that thread was finishing is
		/// found when it looks again after letting go, so none stays behind.
		/// </summary>
		private void Resolve()
		{
			while (Interlocked.CompareExchange(ref _resolving, 1, 0) == 0) {
				try {
					// TryDequeue can spin for a moment while another thread, preempted halfway through its Enqueue,
					// finishes publishing that entry. It is short and bounded by that thread's enqueue, and accepted here.
					while (_reports.TryDequeue(out var report)) {
						if (Volatile.Read(ref _disposed) == 0) {
							Resolve(report.indices, report.on);
						}
					}
				} finally {
					Volatile.Write(ref _resolving, 0);
				}
				if (_reports.IsEmpty) {
					return;
				}
			}
		}

		/// <summary>One report, for all the triggers of its element. Only the resolving thread.</summary>
		private void Resolve(int[] indices, bool on)
		{
			var state = on ? 1 : 0;
			foreach (var index in indices) {
				if (_modes[index] == DofTriggerMode.Follow) {
					Set(index, state);
					continue;
				}
				var was = _on[index];
				_on[index] = state;
				if (was == state || !on) {
					continue;
				}
				switch (_modes[index]) {
					case DofTriggerMode.On:
						Set(index, 1);
						break;
					case DofTriggerMode.Off:
						Set(index, 0);
						break;
					default:
						if (_targets[index] == DofTriggerTarget.Event) {
							_target.PulseEvent(_numbers[index]);
						} else {
							_target.PulseNamed(_names[index]);
						}
						break;
				}
			}
		}

		/// <summary>An ID's number as PinMAME reads it: <c>1</c>, <c>01</c> and <c>+1</c> are one.</summary>
		private static bool TryNumber(string id, out int number)
			=> int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

		private void Set(int index, int value)
		{
			if (_targets[index] == DofTriggerTarget.Event) {
				_target.SetEvent(_numbers[index], value);
			} else {
				_target.SetNamed(_names[index], value);
			}
		}

		private void OnCoil(object sender, CoilEventArgs e) => Apply(DofTriggerSource.Coil, e.Id, e.IsEnabled);

		private void OnLamp(object sender, LampEventArgs e) => Apply(SourceOf(e.Source), e.Id, e.Value > 0f);

		private void OnLamps(object sender, LampsEventArgs e)
		{
			if (e.LampsChanged == null) {
				return;
			}
			foreach (var lamp in e.LampsChanged) {
				Apply(SourceOf(lamp.Source), lamp.Id, lamp.Value > 0f);
			}
		}

		/// <summary>Lamps and GI strings are told apart by their source: GI 1 is not lamp 1.</summary>
		private static DofTriggerSource SourceOf(LampSource source) => source == LampSource.GI ? DofTriggerSource.GI : DofTriggerSource.Lamp;

		private void OnSwitch(object sender, SwitchEventArgs2 e) => Apply(DofTriggerSource.Switch, e.Id, e.IsEnabled);

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0) {
				return;
			}
			if (_engine != null) {
				_engine.OnCoilChanged -= OnCoil;
				_engine.OnSwitchChanged -= OnSwitch;
				if (_lamps == null) {
					_engine.OnLampChanged -= OnLamp;
					_engine.OnLampsChanged -= OnLamps;
				}
			}
			if (_lamps != null) {
				_lamps.LampChanged -= OnLamp;
			}
		}
	}
}
