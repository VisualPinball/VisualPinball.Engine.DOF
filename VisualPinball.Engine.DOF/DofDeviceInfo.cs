// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VisualPinball.Engine.DOF
{
	public enum DofDeviceKind
	{
		Hid,
		Usb,
		Serial,
	}

	/// <summary>
	/// A connected device as the operating system describes it, from <see cref="DofLibrary.EnumerateDevices"/>. Fields
	/// a kind does not have are -1 or empty.
	/// </summary>
	public sealed class DofDeviceInfo
	{
		public DofDeviceKind Kind { get; private set; }
		/// <summary>USB vendor ID, or -1 (a serial port that is not on USB).</summary>
		public int VendorId { get; private set; } = -1;
		/// <summary>USB product ID, or -1.</summary>
		public int ProductId { get; private set; } = -1;
		/// <summary>HID usage page.</summary>
		public int UsagePage { get; private set; } = -1;
		/// <summary>HID usage.</summary>
		public int Usage { get; private set; } = -1;
		/// <summary>HID interface number, -1 when the platform does not report it.</summary>
		public int Interface { get; private set; } = -1;
		/// <summary>HID device release number (bcdDevice).</summary>
		public int Release { get; private set; } = -1;
		/// <summary>USB bus and address.</summary>
		public int Bus { get; private set; } = -1;
		public int Address { get; private set; } = -1;
		/// <summary>USB port numbers from the root hub, such as "1.4".</summary>
		public string PortPath { get; private set; } = string.Empty;
		public string Manufacturer { get; private set; } = string.Empty;
		public string Product { get; private set; } = string.Empty;
		public string SerialNumber { get; private set; } = string.Empty;
		/// <summary>The HID device path, or the serial port name (COM3, /dev/ttyACM0).</summary>
		public string Path { get; private set; } = string.Empty;
		/// <summary>Serial port description.</summary>
		public string Description { get; private set; } = string.Empty;
		/// <summary>Serial port transport: "usb", "bluetooth" or "native".</summary>
		public string Transport { get; private set; } = string.Empty;

		public override string ToString()
		{
			switch (Kind) {
				case DofDeviceKind.Serial: return $"serial {Path} ({Description})";
				case DofDeviceKind.Usb: return $"usb {VendorId:X4}:{ProductId:X4} bus {Bus} port {PortPath}";
				default: return $"hid {VendorId:X4}:{ProductId:X4} {Product} (usage {UsagePage:X4}:{Usage:X4}, interface {Interface})";
			}
		}

		internal static DofDeviceInfo Parse(string line)
		{
			var f = line.Split('|');
			switch (f[0]) {
				case "hid" when f.Length >= 11:
					return new DofDeviceInfo {
						Kind = DofDeviceKind.Hid,
						VendorId = Int(f[1]),
						ProductId = Int(f[2]),
						UsagePage = Int(f[3]),
						Usage = Int(f[4]),
						Interface = Int(f[5]),
						Release = Int(f[6]),
						Manufacturer = f[7],
						Product = f[8],
						SerialNumber = f[9],
						Path = f[10],
					};
				case "usb" when f.Length >= 6:
					return new DofDeviceInfo {
						Kind = DofDeviceKind.Usb,
						VendorId = Int(f[1]),
						ProductId = Int(f[2]),
						Bus = Int(f[3]),
						Address = Int(f[4]),
						PortPath = f[5],
					};
				case "serial" when f.Length >= 7:
					return new DofDeviceInfo {
						Kind = DofDeviceKind.Serial,
						Path = f[1],
						Description = f[2],
						Transport = f[3],
						VendorId = Int(f[4]),
						ProductId = Int(f[5]),
						SerialNumber = f[6],
					};
				default:
					return null;
			}
		}

		private static int Int(string value) => int.TryParse(value, out var result) ? result : -1;
	}
}
