// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using VisualPinball.Engine.DOF.Configuration;
using VisualPinball.Engine.DOF.Player;
using VisualPinball.Engine.DOF.Session;
using VisualPinball.Plugins;
using VisualPinball.Unity;

namespace VisualPinball.Engine.DOF.Unity.Tests
{
	/// <summary>A player host in miniature for one DOF plugin and one game, in a temporary folder.</summary>
	internal sealed class DofTestHost : IDisposable
	{
		public const string Controller = "pinmame/controller/0";
		public const long GameSession = 7;

		public readonly string Root = Path.Combine(Path.GetTempPath(), "vpe-dof-plugin", Guid.NewGuid().ToString("N"));
		public readonly OwnerScheduler Scheduler = new OwnerScheduler();
		public readonly PluginCapabilities Capabilities = new PluginCapabilities();
		public readonly PluginOutputLeases Leases = new PluginOutputLeases();
		public readonly PluginResourceRegistry Resources = new PluginResourceRegistry();
		public readonly ResourceScope ApplicationLifetime = new ResourceScope();
		public readonly ResourceScope GameLifetime = new ResourceScope();
		public readonly List<string> Log = new List<string>();
		public readonly DofPluginFactory Factory;
		public readonly DofPlugin Plugin;
		public RealtimeOutputResourceSource Feed;
		private IDisposable _feedPublication;

		public DofTestHost(Func<string, IDofNative> nativeFactory = null)
		{
			Directory.CreateDirectory(Root);
			Capabilities.Publish(UnityPluginCapability.Outputs, Leases);
			var configuration = new PluginConfigurationStore(Path.Combine(Root, "plugins"));
			Factory = new DofPluginFactory(DofPluginFactory.ConfigurationRoot(Path.Combine(Root, "plugins", DofPluginFactory.Id))) { NativeFactory = nativeFactory };
			var application = new PluginApplicationContext(Factory.Descriptor, configuration, Scheduler, Capabilities, Resources, ApplicationLifetime, message => {
				lock (Log) {
					Log.Add(message);
				}
			});
			Plugin = (DofPlugin)Factory.Create(application);
		}

		/// <summary>Publishes the game's real-time outputs, as the gamelogic resources of the player do.</summary>
		public RealtimeOutputResourceSource PublishFeed(string numbering = RealtimeOutputNumbering.PinMame)
		{
			Feed = new RealtimeOutputResourceSource(numbering);
			_feedPublication = Resources.Publish(new PluginResourceDescriptor(Controller + "/outputs/realtime", "pinmame", Controller,
				"PinMAME real-time outputs", "realtime-outputs", ResourceKind.RealtimeOutputs, GameSession, 1), Feed);
			return Feed;
		}

		public PluginGameContext Game(string rom = "vpetest_l1", JObjectBuilder settings = null)
			=> new PluginGameContext(new GameIdentity("pinmame", rom), "", "Test Table", Controller, GameSession,
				new PluginSettingsSnapshot(1, settings?.Build()), Capabilities, Resources, GameLifetime, CancellationToken.None);

		public IPluginCabinetTriggers Triggers => Capabilities.Find<IPluginCabinetTriggers>(CabinetTriggerCapability.Id);

		/// <summary>Withdraws the game's real-time outputs, as when their producer goes away during the game.</summary>
		public void WithdrawFeed()
		{
			_feedPublication?.Dispose();
			_feedPublication = null;
		}

		/// <summary>
		/// Imports a cabinet: Art-Net on a universe, optionally a Teensy on a serial port, and ini rows (separated by
		/// CRLF) with an optional port header. DOF configures no connected boards itself unless <paramref name="autoConfig"/>.
		/// </summary>
		public DofImportReport Import(int universe, string row, string serialPort = null, string header = null, bool autoConfig = false)
		{
			var source = Path.Combine(Root, "source-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(source);
			var teensy = serialPort == null ? "" : $"<TeensyStripController><Name>Teensy</Name><ComPortName>{serialPort}</ComPortName><NumberOfLedsStrip1>8</NumberOfLedsStrip1></TeensyStripController>";
			File.WriteAllText(Path.Combine(source, "Cabinet.xml"), "<?xml version=\"1.0\"?>\r\n<Cabinet><Name>Test</Name><AutoConfigEnabled>" + (autoConfig ? "true" : "false") + "</AutoConfigEnabled>"
				+ $"<OutputControllers><ArtNet><Name>ArtNet 1</Name><Universe>{universe}</Universe><BroadcastAddress>127.0.0.1</BroadcastAddress></ArtNet>{teensy}</OutputControllers>"
				+ "<Toys><LedWizEquivalent><Name>LedWizEquivalent 1</Name><LedWizNumber>1</LedWizNumber><Outputs>"
				+ string.Concat(Enumerable.Range(1, 8).Select(p => $"<LedWizEquivalentOutput><OutputName>ArtNet 1.{p:00}</OutputName><LedWizEquivalentOutputNumber>{p}</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>"))
				+ "</Outputs></LedWizEquivalent></Toys></Cabinet>\r\n", new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(source, "directoutputconfig.ini"), "[version]\r\nversion=1\r\n\r\n[Colors DOF]\r\nWhite=#FFFFFFFF\r\n\r\n[Variables DOF]\r\n\r\n[Config DOF]\r\n" + (header != null ? "# " + header + "\r\n" : "") + row + "\r\n", new UTF8Encoding(false));
			var report = new DofImporter(Factory.Store).Import(source);
			Assert.That(report.Accepted, Is.True, report.ToString());
			return report;
		}

		/// <summary>Waits for a task without blocking the editor, pumping the owner scheduler, and rethrows its failure.</summary>
		public IEnumerator Await(Task task, string what, double timeoutSeconds = 10)
		{
			var watch = Stopwatch.StartNew();
			while (!task.IsCompleted) {
				Scheduler.Pump();
				if (watch.Elapsed.TotalSeconds > timeoutSeconds) {
					Assert.Fail(what + ": timed out");
				}
				yield return null;
			}
			Scheduler.Pump();
			if (task.IsFaulted) {
				ExceptionDispatchInfo.Capture(task.Exception.InnerException ?? task.Exception).Throw();
			}
		}

		public void Dispose()
		{
			Plugin.Dispose();
			GameLifetime.Dispose();
			_feedPublication?.Dispose();
			Feed?.Dispose();
			ApplicationLifetime.Dispose();
			Scheduler.Pump();
			// The plugin releases the configuration once DOF is freed; deleting the folder before that only fails.
			Plugin.Quiescence.Wait(TimeSpan.FromSeconds(10));
			try {
				Directory.Delete(Root, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}

	/// <summary>Settings values for a test game.</summary>
	internal sealed class JObjectBuilder
	{
		private readonly Newtonsoft.Json.Linq.JObject _values = new Newtonsoft.Json.Linq.JObject();
		public JObjectBuilder Set(string id, string value) { _values[id] = value; return this; }
		public Newtonsoft.Json.Linq.JObject Build() => (Newtonsoft.Json.Linq.JObject)_values.DeepClone();
	}

	/// <summary>A DOF instance that records what it gets and finishes only when told to.</summary>
	internal sealed class BlockingNative : IDofNative
	{
		private readonly object _lock = new object();
		private readonly List<string> _calls = new List<string>();
		private long _generation;

		public readonly ManualResetEventSlim FinishStarted = new ManualResetEventSlim(false);
		public readonly ManualResetEventSlim FinishAllowed = new ManualResetEventSlim(true);
		public volatile bool Disposed;

		public List<string> Calls
		{
			get {
				lock (_lock) {
					return new List<string>(_calls);
				}
			}
		}

		private void Record(string call)
		{
			lock (_lock) {
				_calls.Add(call);
			}
		}

		public void SetOutputMap(int device, IReadOnlyList<int> targets) => Record($"map:{device}:{string.Join(",", targets)}");
		public void Init(string tableFile, string romName) => Record("init:" + romName);
		public void Data(char type, int number, int value) => Record($"{type}{number}={value}");
		public void DataNamed(string name, int value) => Record($"{name}={value}");
		public int PendingInput => 0;

		public void Finish()
		{
			Record("finish");
			FinishStarted.Set();
			FinishAllowed.Wait();
		}

		public long SetOutputGate(bool open)
		{
			Record(open ? "gate:open" : "gate:closed");
			return Interlocked.Increment(ref _generation);
		}

		public long LatchOutputGate()
		{
			Record("gate:latched");
			return Interlocked.Increment(ref _generation);
		}

		public DofGateState GetGateState(long generation) => new DofGateState(true, 1, 0, 0);
		public IReadOnlyList<string> GetGatePending(long generation) => Array.Empty<string>();
		public DofCabinetDescription Describe() => null;

		public void Dispose()
		{
			Record("dispose");
			Disposed = true;
		}
	}

	/// <summary>Cabinet triggers that write down every call.</summary>
	internal sealed class RecordingTriggers : IPluginCabinetTriggers
	{
		public readonly List<string> Calls = new List<string>();
		public void SetEvent(int number, int value) => Calls.Add($"E{number}={value}");
		public void PulseEvent(int number) => Calls.Add($"E{number} pulse");
		public void SetNamed(string name, int value) => Calls.Add($"${name}={value}");
		public void PulseNamed(string name) => Calls.Add($"${name} pulse");
	}
}
