// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace VisualPinball.Engine.DOF.Test
{
	/// <summary>
	/// Receives the Art-Net DMX packets libdof sends to 127.0.0.1 and keeps every frame per universe, so tests can wait
	/// for an output state and check what was sent in between.
	/// </summary>
	public sealed class ArtNetCapture : IDisposable
	{
		public const int Port = 6454;

		private readonly UdpClient _client;
		private readonly Thread _thread;
		private readonly object _lock = new object();
		private readonly Dictionary<int, List<byte[]>> _frames = new Dictionary<int, List<byte[]>>();
		private volatile bool _running = true;
		private static readonly byte[] SyncMagic = { (byte)'V', (byte)'P', (byte)'E', (byte)'S', (byte)'Y', (byte)'N', (byte)'C', 0 };
		private int _syncSent;
		private int _syncReceived;

		public ArtNetCapture()
		{
			// The port is ours alone: a second test run or another Art-Net listener on it would take some of the packets.
			// libdof's Art-Net socket only sends, so it doesn't bind the port.
			_client = new UdpClient(AddressFamily.InterNetwork);
			try {
				if (OperatingSystem.IsWindows()) {
					_client.Client.ExclusiveAddressUse = true;
				}
				_client.Client.Bind(new IPEndPoint(IPAddress.Loopback, Port));
			} catch (SocketException e) {
				_client.Dispose();
				throw new InvalidOperationException($"The Art-Net capture can't listen on 127.0.0.1:{Port} ({e.SocketErrorCode}): another test run "
					+ "or Art-Net listener is using the port. Run one test process at a time, with nothing else listening on it.", e);
			}
			_client.Client.ReceiveTimeout = 100;
			_thread = new Thread(Receive) { IsBackground = true, Name = "Art-Net capture" };
			_thread.Start();
		}

		/// <summary>Every DMX frame received for a universe so far (the 512 channel values).</summary>
		public IReadOnlyList<byte[]> Frames(int universe)
		{
			lock (_lock) {
				return _frames.TryGetValue(universe, out var list) ? list.ToArray() : Array.Empty<byte[]>();
			}
		}

		public int Count(int universe)
		{
			lock (_lock) {
				return _frames.TryGetValue(universe, out var list) ? list.Count : 0;
			}
		}

		public byte[] Latest(int universe)
		{
			lock (_lock) {
				return _frames.TryGetValue(universe, out var list) && list.Count > 0 ? list[list.Count - 1] : null;
			}
		}

		/// <summary>
		/// The number of frames received for a universe once the packets queued on the capture socket ahead of a marker
		/// packet sent by this call have been processed. On loopback that includes, in practice, what libdof sent before
		/// the call; UDP gives no send order between two sockets, though, so this is no proof of ordering. Tests use it to
		/// keep stale frames out of an "after" window. A test whose result depends on the order of frames uses frame
		/// numbers instead (<see cref="WaitForIndex"/>): a frame its content identifies, and the frames after it.
		/// </summary>
		public int Mark(int universe, int timeoutMs = 3000)
		{
			// Waits for the marker: whatever was queued on the socket ahead of it has been processed by then.
			var id = Interlocked.Increment(ref _syncSent);
			var packet = new byte[SyncMagic.Length + 4];
			Buffer.BlockCopy(SyncMagic, 0, packet, 0, SyncMagic.Length);
			BitConverter.GetBytes(id).CopyTo(packet, SyncMagic.Length);
			using (var sender = new UdpClient(AddressFamily.InterNetwork)) {
				sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, Port));
			}
			var watch = Stopwatch.StartNew();
			while (Volatile.Read(ref _syncReceived) < id) {
				if (watch.ElapsedMilliseconds > timeoutMs) {
					throw new TimeoutException("the Art-Net capture didn't receive its own sync packet");
				}
				Thread.Sleep(1);
			}
			return Count(universe);
		}

		/// <summary>Waits until a frame received after frame number <paramref name="after"/> matches.</summary>
		public byte[] WaitFor(int universe, Func<byte[], bool> match, int after = 0, int timeoutMs = 3000)
		{
			var index = WaitForIndex(universe, match, after, timeoutMs);
			return index < 0 ? null : Frames(universe)[index];
		}

		/// <summary>Like <see cref="WaitFor"/>, but returns the matching frame's number, or -1.</summary>
		public int WaitForIndex(int universe, Func<byte[], bool> match, int after = 0, int timeoutMs = 3000)
		{
			var watch = Stopwatch.StartNew();
			while (watch.ElapsedMilliseconds < timeoutMs) {
				lock (_lock) {
					if (_frames.TryGetValue(universe, out var list)) {
						for (var i = Math.Max(0, after); i < list.Count; i++) {
							if (match(list[i])) {
								return i;
							}
						}
					}
				}
				Thread.Sleep(5);
			}
			return -1;
		}

		private void Receive()
		{
			var remote = new IPEndPoint(IPAddress.Any, 0);
			while (_running) {
				byte[] packet;
				try {
					packet = _client.Receive(ref remote);
				} catch (SocketException) {
					continue;
				} catch (ObjectDisposedException) {
					return;
				}
				if (IsSync(packet)) {
					Volatile.Write(ref _syncReceived, BitConverter.ToInt32(packet, SyncMagic.Length));
					continue;
				}
				// "Art-Net\0", OpDmx 0x5000 little-endian, protocol version, sequence, physical, universe (LE), length (BE).
				if (packet.Length < 18 || packet[0] != (byte)'A' || packet[7] != 0 || packet[8] != 0x00 || packet[9] != 0x50) {
					continue;
				}
				var universe = packet[14] | (packet[15] << 8);
				var length = (packet[16] << 8) | packet[17];
				var data = new byte[512];
				Buffer.BlockCopy(packet, 18, data, 0, Math.Min(length, Math.Min(512, packet.Length - 18)));
				lock (_lock) {
					if (!_frames.TryGetValue(universe, out var list)) {
						_frames[universe] = list = new List<byte[]>();
					}
					list.Add(data);
				}
			}
		}

		private static bool IsSync(byte[] packet)
		{
			if (packet.Length != SyncMagic.Length + 4) {
				return false;
			}
			for (var i = 0; i < SyncMagic.Length; i++) {
				if (packet[i] != SyncMagic[i]) {
					return false;
				}
			}
			return true;
		}

		public void Dispose()
		{
			_running = false;
			_client.Dispose();
			_thread.Join(1000);
		}
	}
}
