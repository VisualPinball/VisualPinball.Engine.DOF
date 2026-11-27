// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VisualPinball.Engine.DOF.Native;

namespace VisualPinball.Engine.DOF
{
	/// <summary>libdof's log levels. As a threshold, by severity: Error passes errors, Warning adds warnings, Info adds
	/// information and Debug everything.</summary>
	public enum DofLogLevel
	{
		Info = 0,
		Warning = 1,
		Error = 2,
		Debug = 3,
	}

	/// <summary>One table row of an imported ini file: the device (N of directoutputconfigN.ini) and the ROM name.</summary>
	public readonly struct DofRomRow : IEquatable<DofRomRow>
	{
		public int Device { get; }
		public string Rom { get; }

		public DofRomRow(int device, string rom)
		{
			Device = device;
			Rom = rom;
		}

		public bool Equals(DofRomRow other) => Device == other.Device && string.Equals(Rom, other.Rom, StringComparison.Ordinal);
		public override bool Equals(object obj) => obj is DofRomRow other && Equals(other);
		public override int GetHashCode() => (Device * 397) ^ (Rom?.GetHashCode() ?? 0);
		public override string ToString() => $"{Device}|{Rom}";
	}

	/// <summary>
	/// An output controller of a cabinet config, and whether a session loads it or skips it, as libdof reads it.
	/// </summary>
	public readonly struct DofCabinetController
	{
		/// <summary>libdof's controller type, its XML element name (LedWiz, PacUIO, ArtNet, ...).</summary>
		public string Type { get; }
		/// <summary>Its name, or empty if it has none.</summary>
		public string Name { get; }
		/// <summary>Whether a session loads this definition. One it skips drives nothing.</summary>
		public bool Loaded { get; }
		/// <summary>Why a session skips it, in libdof's words ("no Id", ...); empty for one it loads.</summary>
		public string Reason { get; }

		public DofCabinetController(string type, string name, bool loaded, string reason)
		{
			Type = type;
			Name = name;
			Loaded = loaded;
			Reason = reason;
		}

		public override string ToString() => Loaded ? $"{Type} {Name}: loaded" : $"{Type} {Name}: skipped, {Reason}";
	}

	/// <summary>
	/// Process-wide functions of VpeDof: version checks, the log sink, the ROM catalog, the cabinet check and device
	/// enumeration.
	/// </summary>
	public static class DofLibrary
	{
		private static readonly object LogLock = new object();
		private static readonly DofNative.LogCallback LogCallback = OnLog;
		private static volatile Action<DofLogLevel, string> _logSink;
		[ThreadStatic]
		private static int t_inLogCallback;
		private static bool? _compatible;

		/// <summary>The ABI version of the loaded native library.</summary>
		public static int NativeAbiVersion => DofNative.GetAbiVersion();

		/// <summary>The version of libdof the native library was built from, such as "0.5.4".</summary>
		public static string LibdofVersion
		{
			get {
				EnsureCompatible();
				return Utf8.Decode(DofNative.GetLibdofVersion());
			}
		}

		/// <summary>
		/// Throws unless the native library loads and has the ABI version this binding was written for. Checked once per
		/// process, before any other call.
		/// </summary>
		/// <exception cref="DofException">The library is missing or has another ABI.</exception>
		public static void EnsureCompatible()
		{
			if (_compatible == true) {
				return;
			}
			int version;
			try {
				version = DofNative.GetAbiVersion();
			} catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException) {
				throw new DofException($"The native DOF library ({DofNative.Library}) could not be loaded: {e.Message}", e);
			}
			if (version != DofNative.AbiVersion) {
				throw new DofException(DofStatus.Error, $"The native DOF library has ABI version {version}, this binding needs {DofNative.AbiVersion}.");
			}
			_compatible = true;
		}

		/// <summary>
		/// Sets where libdof's log lines go, or null to drop them. The sink is called on libdof's threads, so it must be
		/// thread-safe and must not block: replacing it waits until no line is still being delivered to the previous one.
		/// Process-wide.
		/// </summary>
		/// <param name="level">The most detailed level passed; <see cref="DofLogLevel.Info"/> passes errors, warnings and
		/// information.</param>
		public static void SetLog(Action<DofLogLevel, string> sink, DofLogLevel level = DofLogLevel.Info)
		{
			EnsureCompatible();
			lock (LogLock) {
				_logSink = sink;
				DofNative.SetLog(sink != null ? LogCallback : null, IntPtr.Zero, (int)level);
			}
		}

		[MonoPInvokeCallback(typeof(DofNative.LogCallback))]
		private static void OnLog(int level, IntPtr message, IntPtr user)
		{
			var sink = _logSink;
			if (sink == null) {
				return;
			}
			t_inLogCallback++;
			try {
				sink((DofLogLevel)level, Utf8.Decode(message) ?? string.Empty);
			} catch {
				// An exception must not unwind into native code.
			} finally {
				t_inLogCallback--;
			}
		}

		/// <summary>Whether this thread is in the log sink, called for a line libdof wrote.</summary>
		internal static bool InLogCallback => t_inLogCallback > 0;

		/// <summary>
		/// Lists the table rows of the ini files libdof finds for a configuration folder, with their device numbers. Needs
		/// no instance and does not affect a running one.
		/// </summary>
		/// <param name="basePath">The folder that contains <c>directoutputconfig/</c>.</param>
		/// <param name="tableFile">The table file name the session will pass to <see cref="DofInstance.Init"/>, or null.</param>
		public static IReadOnlyList<DofRomRow> RomNames(string basePath, string tableFile = null)
		{
			EnsureCompatible();
			var nativeBase = Utf8.Encode(DofPaths.ToNative(basePath));
			var nativeTable = Utf8.Encode(tableFile == null ? string.Empty : DofPaths.ToNative(tableFile));
			var lines = StringCollector.Collect(user => DofNative.RomNames(nativeBase, nativeTable, StringCollector.Callback, user), out var count);
			if (count < 0) {
				throw new DofException(DofStatus.Error, "Reading the ROM names of the DOF configuration failed.");
			}
			var rows = new List<DofRomRow>(lines.Count);
			foreach (var line in lines) {
				var separator = line.IndexOf('|');
				if (separator > 0 && int.TryParse(line.Substring(0, separator), out var device)) {
					rows.Add(new DofRomRow(device, line.Substring(separator + 1)));
				}
			}
			return rows;
		}

		/// <summary>
		/// Reads the output controllers of the cabinet config a configuration folder's global config names, as a session
		/// would, and says which ones it loads and why it skips the others. Opens no device and starts no thread: needs no
		/// instance and does not affect a running one.
		/// </summary>
		/// <param name="basePath">The folder that contains <c>directoutputconfig/</c>.</param>
		/// <returns>The controllers in file order; none when the global config names no cabinet config.</returns>
		/// <exception cref="DofException">What fails a session's init: there is no global config or it cannot be read, or
		/// the cabinet config it names does not exist or is not valid XML.</exception>
		public static IReadOnlyList<DofCabinetController> CabinetControllers(string basePath)
		{
			EnsureCompatible();
			var nativeBase = Utf8.Encode(DofPaths.ToNative(basePath));
			var error = new byte[1024];
			var controllers = new List<DofCabinetController>();
			var handle = GCHandle.Alloc(controllers);
			int count;
			try {
				count = DofNative.CabinetControllers(nativeBase, ControllerCallback, GCHandle.ToIntPtr(handle), error, error.Length);
			} finally {
				handle.Free();
			}
			if (count < 0) {
				var message = Utf8.DecodeBuffer(error);
				throw new DofException(DofStatus.Error, string.IsNullOrEmpty(message) ? "Reading the DOF cabinet config failed." : message);
			}
			return controllers;
		}

		private static readonly DofNative.ControllerCallback ControllerCallback = OnController;

		[MonoPInvokeCallback(typeof(DofNative.ControllerCallback))]
		private static void OnController(int loaded, IntPtr type, IntPtr name, IntPtr reason, IntPtr user)
		{
			if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is List<DofCabinetController> controllers) {
				controllers.Add(new DofCabinetController(Utf8.Decode(type) ?? string.Empty, Utf8.Decode(name) ?? string.Empty, loaded != 0, Utf8.Decode(reason) ?? string.Empty));
			}
		}

		/// <summary>Lists connected HID and USB devices and serial ports without opening any of them for output.</summary>
		public static IReadOnlyList<DofDeviceInfo> EnumerateDevices()
		{
			EnsureCompatible();
			var lines = StringCollector.Collect(user => DofNative.EnumerateDevices(StringCollector.Callback, user), out var status);
			DofException.ThrowIfFailed(status, "Enumerating devices");
			var devices = new List<DofDeviceInfo>(lines.Count);
			foreach (var line in lines) {
				var device = DofDeviceInfo.Parse(line);
				if (device != null) {
					devices.Add(device);
				}
			}
			return devices;
		}
	}
}
