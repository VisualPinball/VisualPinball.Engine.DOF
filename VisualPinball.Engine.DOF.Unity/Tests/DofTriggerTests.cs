// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using VisualPinball.Engine.DOF.Player;
using VisualPinball.Engine.DOF.Session;
using VisualPinball.Engine.Game.Engines;
using VisualPinball.Engine.Math;
using VisualPinball.Plugins;
using VisualPinball.Unity;
using Color = UnityEngine.Color;
using Object = UnityEngine.Object;

namespace VisualPinball.Engine.DOF.Unity.Tests
{
	/// <summary>How a table's DOF triggers turn switches, coils and lamps into events and named elements.</summary>
	public class DofTriggerTests
	{
		private GameObject _table;
		private DofTriggerComponent _component;

		[SetUp]
		public void SetUp()
		{
			_table = new GameObject("DOF trigger test");
			_component = _table.AddComponent<DofTriggerComponent>();
		}

		[TearDown]
		public void TearDown() => Object.DestroyImmediate(_table);

		private static DofTrigger Event(DofTriggerSource source, string id, int number, DofTriggerMode mode)
			=> new DofTrigger { Source = source, ElementId = id, Target = DofTriggerTarget.Event, EventNumber = number, Mode = mode };

		private static DofTrigger Named(DofTriggerSource source, string id, string name, DofTriggerMode mode)
			=> new DofTrigger { Source = source, ElementId = id, Target = DofTriggerTarget.Named, Name = name, Mode = mode };

		[Test]
		public void FollowsPulsesAndLatches()
		{
			_component.Triggers = new List<DofTrigger> {
				Event(DofTriggerSource.Switch, "sw_left_sling", 10, DofTriggerMode.Follow),
				Named(DofTriggerSource.Coil, "c_knocker", " $Knocker ", DofTriggerMode.Pulse),
				Event(DofTriggerSource.Lamp, "l_shoot_again", 11, DofTriggerMode.On),
				Event(DofTriggerSource.Lamp, "l_shoot_again", 12, DofTriggerMode.Off),
			};
			var triggers = new RecordingTriggers();
			using (var binding = _component.Bind(triggers, null)) {
				Assert.That(binding.Count, Is.EqualTo(4));
				binding.Apply(DofTriggerSource.Switch, "sw_left_sling", true);
				binding.Apply(DofTriggerSource.Switch, "sw_left_sling", true);
				binding.Apply(DofTriggerSource.Switch, "sw_left_sling", false);
				binding.Apply(DofTriggerSource.Coil, "c_knocker", true);
				binding.Apply(DofTriggerSource.Coil, "c_knocker", false);
				binding.Apply(DofTriggerSource.Lamp, "l_shoot_again", true);
				binding.Apply(DofTriggerSource.Lamp, "l_shoot_again", false);
				binding.Apply(DofTriggerSource.Lamp, "l_shoot_again", true);
				// Same ID, other kind: not mapped.
				binding.Apply(DofTriggerSource.Coil, "sw_left_sling", true);
			}
			// Follow passes every report on (DOF drops repeats itself); a name goes without the dollar sign.
			Assert.That(triggers.Calls, Is.EqualTo(new[] {
				"E10=1", "E10=1", "E10=0",
				"$Knocker pulse",
				"E11=1", "E12=0",
				"E11=1", "E12=0",
			}));
		}

		[Test]
		public void MatchesEverySpellingOfANumberForPinMame()
		{
			_component.Triggers = new List<DofTrigger> {
				Event(DofTriggerSource.Lamp, "01", 20, DofTriggerMode.Follow),
				Event(DofTriggerSource.Switch, "1", 21, DofTriggerMode.Follow),
				Event(DofTriggerSource.Coil, "02", 22, DofTriggerMode.Pulse),
				Event(DofTriggerSource.Lamp, "l_name", 23, DofTriggerMode.Follow),
			};
			// PinMAME sends lamp 1 for a table that maps both 1 and 01, and a switch with the ID its mapping has.
			var triggers = new RecordingTriggers();
			using (var binding = _component.Bind(triggers, null, null, numericIds: true)) {
				binding.Apply(DofTriggerSource.Lamp, "1", true);
				binding.Apply(DofTriggerSource.Lamp, "01", false);
				binding.Apply(DofTriggerSource.Switch, "01", true);
				binding.Apply(DofTriggerSource.Coil, "2", true);
				binding.Apply(DofTriggerSource.Coil, "2", false);
				binding.Apply(DofTriggerSource.Lamp, "l_name", true);
				binding.Apply(DofTriggerSource.Lamp, "001", true);
				binding.Apply(DofTriggerSource.Switch, "2", true);
			}
			Assert.That(triggers.Calls, Is.EqualTo(new[] { "E20=1", "E20=0", "E21=1", "E22 pulse", "E23=1", "E20=1" }));

			// Other engines may tell 1 from 01: the ID must be the one the trigger names.
			var exact = new RecordingTriggers();
			using (var binding = _component.Bind(exact, null)) {
				binding.Apply(DofTriggerSource.Lamp, "1", true);
				binding.Apply(DofTriggerSource.Lamp, "01", true);
				binding.Apply(DofTriggerSource.Switch, "01", true);
			}
			Assert.That(exact.Calls, Is.EqualTo(new[] { "E20=1" }));
		}

		[Test]
		public void SkipsTriggersThatCannotDriveAnything()
		{
			_component.Triggers = new List<DofTrigger> {
				Event(DofTriggerSource.Switch, "sw1", 0, DofTriggerMode.Follow),
				Named(DofTriggerSource.Switch, "sw2", " $ ", DofTriggerMode.Follow),
				Event(DofTriggerSource.Switch, "", 5, DofTriggerMode.Follow),
				null,
			};
			var triggers = new RecordingTriggers();
			using (var binding = _component.Bind(triggers, null)) {
				Assert.That(binding.Count, Is.Zero);
				binding.Apply(DofTriggerSource.Switch, "sw1", true);
				binding.Apply(DofTriggerSource.Switch, "sw2", true);
			}
			Assert.That(triggers.Calls, Is.Empty);
		}

		[Test]
		public void FollowsLampsThePlayerSetsWithoutTheEnginesEvents()
		{
			_component.Triggers = new List<DofTrigger> {
				Event(DofTriggerSource.Lamp, "l_jackpot", 20, DofTriggerMode.Follow),
				Event(DofTriggerSource.GI, "gi_1", 21, DofTriggerMode.Follow),
			};
			var lamps = new LampPlayer();
			var triggers = new RecordingTriggers();
			using (var binding = _component.Bind(triggers, null, lamps)) {
				// PinMAME's threaded playback sets the lamps on the player (Player.SetLamp), without the engine's lamp
				// events, and GI strings the same way.
				lamps.HandleLampEvent("l_jackpot", 255f, LampSource.Lamp);
				lamps.HandleLampEvent("l_jackpot", 128f, LampSource.Lamp);
				lamps.HandleLampEvent("l_jackpot", LampStatus.Off, LampSource.Lamp);
				lamps.HandleLampEvent("gi_1", 8f, LampSource.GI);
				lamps.HandleLampEvent("l_jackpot", LampStatus.Blinking, LampSource.Lamp);
			}
			lamps.HandleLampEvent("l_jackpot", 0f, LampSource.Lamp);
			Assert.That(triggers.Calls, Is.EqualTo(new[] { "E20=1", "E20=1", "E20=0", "E21=1", "E20=1" }));
		}

		[Test]
		public void TellsGiStringsFromLampsOfTheSameId()
		{
			_component.Triggers = new List<DofTrigger> {
				Event(DofTriggerSource.Lamp, "1", 30, DofTriggerMode.Follow),
				Event(DofTriggerSource.GI, "1", 31, DofTriggerMode.Follow),
			};
			var lamps = new LampPlayer();
			var triggers = new RecordingTriggers();
			using (_component.Bind(triggers, null, lamps)) {
				lamps.HandleLampEvent("1", 255f, LampSource.Lamp);
				// GI string 1 going off must not turn off what lamp 1 drives.
				lamps.HandleLampEvent("1", 0f, LampSource.GI);
			}
			Assert.That(triggers.Calls, Is.EqualTo(new[] { "E30=1", "E31=0" }));
		}

		[Test]
		public void IgnoresTheStateTheLampsStartWith()
		{
			var root = new GameObject("DOF lamp start");
			try {
				var player = root.AddComponent<global::VisualPinball.Unity.Player>();
				var table = root.AddComponent<TableComponent>();
				var engine = root.AddComponent<DefaultGamelogicEngine>();
				var light = new GameObject("Shield");
				light.transform.SetParent(root.transform);
				var device = light.AddComponent<LightComponent>();
				foreach (var (id, channel) in new[] { ("60", ColorChannel.Red), ("61", ColorChannel.Green), ("62", ColorChannel.Blue) }) {
					table.MappingConfig.Lamps.Add(new LampMapping { Id = id, Source = LampSource.Lamp, Device = device, Type = LampType.RgbMulti, Channel = channel, FadingSteps = 255 });
				}
				table.MappingConfig.Lamps.Add(new LampMapping { Id = "70", Source = LampSource.Lamp, Device = device, Type = LampType.SingleOnOff });
				var lamps = new LampPlayer();
				lamps.Awake(player, table, engine);
				typeof(LampPlayer).GetMethod("RegisterLamp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(lamps, new object[] { device, new SilentLamp() });

				_component.Triggers = new List<DofTrigger> {
					Event(DofTriggerSource.Lamp, "60", 40, DofTriggerMode.On),
					Event(DofTriggerSource.Lamp, "61", 41, DofTriggerMode.Follow),
					Event(DofTriggerSource.Lamp, "70", 42, DofTriggerMode.Follow),
				};
				var triggers = new RecordingTriggers();
				using (_component.Bind(triggers, null, lamps)) {
					// The player turns the channels of RGB lamps on at zero and the other lamps off: no change of the game.
					lamps.OnStart();
					Assert.That(triggers.Calls, Is.Empty);
					engine.SetLamp("60", 255f);
					engine.SetLamp("70", 1f);
				}
				lamps.OnDestroy();
				Assert.That(triggers.Calls, Is.EqualTo(new[] { "E40=1", "E42=1" }), "the engine's lamp events go through");
			} finally {
				Object.DestroyImmediate(root);
			}
		}

		[Test]
		public void FollowsTheEnginesLampsOfATableWithoutMappedLamps()
		{
			var root = new GameObject("DOF lamps without mappings");
			try {
				var engine = root.AddComponent<DefaultGamelogicEngine>();
				var lamps = new LampPlayer();
				lamps.Awake(root.AddComponent<global::VisualPinball.Unity.Player>(), root.AddComponent<TableComponent>(), engine);
				lamps.OnStart();
				_component.Triggers = new List<DofTrigger> { Event(DofTriggerSource.Lamp, "5", 50, DofTriggerMode.Follow) };
				var triggers = new RecordingTriggers();
				using (_component.Bind(triggers, null, lamps)) {
					engine.SetLamp("5", 1f);
					engine.SetLamp("5", 0f);
				}
				lamps.OnDestroy();
				Assert.That(triggers.Calls, Is.EqualTo(new[] { "E50=1", "E50=0" }));
			} finally {
				Object.DestroyImmediate(root);
			}
		}

		[Test]
		public void EndsEveryTargetOfAnElementOnItsLastReport()
		{
			// One switch drives two events.
			_component.Triggers = new List<DofTrigger> {
				Event(DofTriggerSource.Switch, "sw_sling", 10, DofTriggerMode.Follow),
				Event(DofTriggerSource.Switch, "sw_sling", 11, DofTriggerMode.Follow),
			};
			var native = new BlockingNative();
			var session = new DofGameSession(new DofSessionOptions { BasePath = "unused", GameSession = 3, WaitForSnapshot = false, NativeFactory = _ => native });
			try {
				Assert.That(session.StartAsync().Wait(TimeSpan.FromSeconds(5)), Is.True);
				Assert.That(session.SetPausedAsync(true).Wait(TimeSpan.FromSeconds(5)), Is.True);
				var held = new HoldingTriggers(session);
				using (var binding = _component.Bind(held, null)) {
					// The switch closes on one thread (the main thread's debug shortcuts, say), which is held after the
					// first event got the closing and before the second did. It opens on the simulation thread, and
					// nothing more comes: a switch that stays open sends no further event.
					var closing = new Thread(() => binding.Apply(DofTriggerSource.Switch, "sw_sling", true));
					closing.Start();
					Assert.That(held.Holding.Wait(TimeSpan.FromSeconds(5)), Is.True);
					var opening = new Thread(() => binding.Apply(DofTriggerSource.Switch, "sw_sling", false));
					opening.Start();
					Assert.That(opening.Join(TimeSpan.FromSeconds(5)), Is.True, "the simulation thread does not wait for the held one");
					held.Release.Set();
					Assert.That(closing.Join(TimeSpan.FromSeconds(5)), Is.True);
				}
				var resumed = session.SetPausedAsync(false);
				Assert.That(resumed.Wait(TimeSpan.FromSeconds(5)), Is.True, "the resume");
				Assert.That(resumed.Result.Settled, Is.True, resumed.Result.Message);
				var calls = native.Calls;
				Assert.That(calls.Where(c => c.StartsWith("E10=") || c.StartsWith("E11=")).ToArray(), Is.EqualTo(new[] { "E10=1", "E11=1", "E10=0", "E11=0" }),
					"both events get the closing, then the opening");
				Assert.That(calls.LastIndexOf("gate:open"), Is.GreaterThan(calls.LastIndexOf("E11=0")), "the resume opens on the last change");
			} finally {
				session.StopAsync().Wait(TimeSpan.FromSeconds(5));
			}
		}

		[Test]
		public void EndsTheTriggersOfEveryComponentOnTheLastReport()
		{
			// Two components of the table trigger on the same switch, bound together for the game.
			var second = new GameObject("DOF trigger test, second");
			try {
				_component.Triggers = new List<DofTrigger> { Event(DofTriggerSource.Switch, "sw_sling", 10, DofTriggerMode.Follow) };
				var other = second.AddComponent<DofTriggerComponent>();
				other.Triggers = new List<DofTrigger> { Named(DofTriggerSource.Switch, "sw_sling", "$Sling", DofTriggerMode.Follow) };
				var inner = new RecordingTriggers();
				var held = new HoldingTriggers(inner);
				using (var binding = DofTriggerComponent.BindAll(new[] { _component, other }, held, null)) {
					Assert.That(binding.Count, Is.EqualTo(2));
					// The closing is held after the first component's event got it, before the second component's
					// element did; the opening comes on another thread meanwhile, and nothing after it.
					var closing = new Thread(() => binding.Apply(DofTriggerSource.Switch, "sw_sling", true));
					closing.Start();
					Assert.That(held.Holding.Wait(TimeSpan.FromSeconds(5)), Is.True);
					var opening = new Thread(() => binding.Apply(DofTriggerSource.Switch, "sw_sling", false));
					opening.Start();
					Assert.That(opening.Join(TimeSpan.FromSeconds(5)), Is.True, "the opening does not wait for the held closing");
					held.Release.Set();
					Assert.That(closing.Join(TimeSpan.FromSeconds(5)), Is.True);
				}
				Assert.That(inner.Calls, Is.EqualTo(new[] { "E10=1", "$Sling=1", "E10=0", "$Sling=0" }), "both components end on the opening");
			} finally {
				Object.DestroyImmediate(second);
			}
		}

		[Test]
		public void SeesTheRisingEdgeOfAFastPulseFromAnotherThread()
		{
			_component.Triggers = new List<DofTrigger> {
				Event(DofTriggerSource.Coil, "c_knocker", 12, DofTriggerMode.Pulse),
				Event(DofTriggerSource.Coil, "c_trigger", 13, DofTriggerMode.Follow),
			};
			var inner = new RecordingTriggers();
			var held = new HoldingTriggers(inner);
			using (var binding = _component.Bind(held, null)) {
				// While a thread is held resolving another element, the knocker fires and releases on another.
				var other = new Thread(() => binding.Apply(DofTriggerSource.Coil, "c_trigger", true));
				other.Start();
				Assert.That(held.Holding.Wait(TimeSpan.FromSeconds(5)), Is.True);
				var pulse = new Thread(() => {
					binding.Apply(DofTriggerSource.Coil, "c_knocker", true);
					binding.Apply(DofTriggerSource.Coil, "c_knocker", false);
				});
				pulse.Start();
				Assert.That(pulse.Join(TimeSpan.FromSeconds(5)), Is.True, "the pulse does not wait");
				held.Release.Set();
				Assert.That(other.Join(TimeSpan.FromSeconds(5)), Is.True);
			}
			Assert.That(inner.Calls, Is.EqualTo(new[] { "E13=1", "E12 pulse" }));
		}

		/// <summary>Holds the first change that turns an event on until released, as a preempted thread would.</summary>
		private sealed class HoldingTriggers : IPluginCabinetTriggers
		{
			private readonly IPluginCabinetTriggers _inner;
			private int _held;
			public readonly ManualResetEventSlim Holding = new ManualResetEventSlim(false);
			public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);

			public HoldingTriggers(IPluginCabinetTriggers inner) { _inner = inner; }

			public void SetEvent(int number, int value)
			{
				if (value != 0 && Interlocked.Exchange(ref _held, 1) == 0) {
					Holding.Set();
					Release.Wait();
				}
				_inner.SetEvent(number, value);
			}

			public void PulseEvent(int number) => _inner.PulseEvent(number);
			public void SetNamed(string name, int value) => _inner.SetNamed(name, value);
			public void PulseNamed(string name) => _inner.PulseNamed(name);
		}

		private sealed class SilentLamp : IApiLamp
		{
#pragma warning disable CS0067
			public event EventHandler Init;
#pragma warning restore CS0067
			public void OnChange(bool enabled) { }
			public void OnLamp(LampStatus newStatus) { }
			public void OnLamp(float intensity) { }
			public void OnLamp(Color color) { }
			public void OnInit(BallManager ballManager) { }
			public void OnDestroy() { }
		}

		[Test]
		public void StopsWhenDisposed()
		{
			_component.Triggers = new List<DofTrigger> { Event(DofTriggerSource.Switch, "sw1", 1, DofTriggerMode.Follow) };
			var triggers = new RecordingTriggers();
			var binding = _component.Bind(triggers, null);
			binding.Dispose();
			binding.Apply(DofTriggerSource.Switch, "sw1", true);
			Assert.That(triggers.Calls, Is.Empty);
		}

		[Test]
		public void SurvivesAPackageRoundTrip()
		{
			_component.GameName = "myoriginal";
			_component.Triggers = new List<DofTrigger> {
				Named(DofTriggerSource.Lamp, "l_jackpot", "Jackpot", DofTriggerMode.Follow),
				Event(DofTriggerSource.Coil, "c_shaker", 102, DofTriggerMode.Pulse),
			};
			var copy = new GameObject("DOF trigger copy");
			try {
				var other = copy.AddComponent<DofTriggerComponent>();
				other.Unpack(_component.Pack());
				Assert.That(other.GameName, Is.EqualTo("myoriginal"));
				Assert.That(other.Triggers.Count, Is.EqualTo(2));
				Assert.That((other.Triggers[0].Source, other.Triggers[0].ElementId, other.Triggers[0].Target, other.Triggers[0].Name, other.Triggers[0].Mode),
					Is.EqualTo((DofTriggerSource.Lamp, "l_jackpot", DofTriggerTarget.Named, "Jackpot", DofTriggerMode.Follow)));
				Assert.That((other.Triggers[1].Source, other.Triggers[1].ElementId, other.Triggers[1].Target, other.Triggers[1].EventNumber, other.Triggers[1].Mode),
					Is.EqualTo((DofTriggerSource.Coil, "c_shaker", DofTriggerTarget.Event, 102, DofTriggerMode.Pulse)));
			} finally {
				Object.DestroyImmediate(copy);
			}
		}
	}
}
