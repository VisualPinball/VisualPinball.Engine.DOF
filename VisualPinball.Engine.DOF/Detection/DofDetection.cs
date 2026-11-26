// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace VisualPinball.Engine.DOF.Detection
{
	/// <summary>A connected device DOF can drive, as libdof's auto-configurators would see it.</summary>
	public sealed class DofDetectedDevice
	{
		/// <summary>libdof's controller type: LedWiz, Pinscape, PinscapePico, PacLed64, PacDrive, PacUIO, PinOne, DudesCab, FT245RBitbangController.</summary>
		public string Type { get; }
		/// <summary>
		/// The ini device it gets (the N of directoutputconfigN.ini), or -1 when only the device itself knows its unit
		/// number (Pinscape, which reports it whatever IDs it uses, Pinscape Pico, Dude's Cab, PinOne), in which case
		/// <see cref="DeviceRange"/> says where it lies.
		/// </summary>
		public int Device { get; }
		/// <summary>A description of the ini numbers the type uses, such as "51-66".</summary>
		public string DeviceRange { get; }
		public DofDeviceInfo Info { get; }
		/// <summary>Things the user may have to do for DOF to open it on this platform.</summary>
		public IReadOnlyList<string> Notes { get; }

		internal DofDetectedDevice(string type, int device, string range, DofDeviceInfo info, IReadOnlyList<string> notes)
		{
			Type = type;
			Device = device;
			DeviceRange = range;
			Info = info;
			Notes = notes;
		}

		public override string ToString() => $"{Type} {(Device > 0 ? $"device {Device}" : $"devices {DeviceRange}")}: {Info}";
	}

	/// <summary>
	/// Maps enumerated devices (<see cref="DofLibrary.EnumerateDevices"/>) to DOF controller types and ini device
	/// numbers with the IDs libdof's auto-configurators use. Opens nothing.
	/// </summary>
	public static class DofDetection
	{
		private const int LedWizVendor = 0xFAFA;
		private const int PinscapeVendor = 0x1209;
		private const int PinscapeProduct = 0xEAEA;
		private const int UltimarcVendor = 0xD209;
		private const int PinOneVendor = 0x39F5;
		// PinOne's first boards, a serial port (libdof's PinOneAutoConfigurator.LegacyHardwareId).
		private const int PinOneLegacyVendor = 0x0E8F;
		private const int PinOneLegacyProduct = 0x9207;
		private const int DudesCabVendor = 0x2E8A;
		private const int DudesCabProduct = 0x106F;
		private const int FtdiVendor = 0x0403;
		private const int Ft245RProduct = 0x6001;

		public static IReadOnlyList<DofDetectedDevice> Detect(IEnumerable<DofDeviceInfo> devices)
		{
			var result = new List<DofDetectedDevice>();
			var ftdiIndex = 0;
			// hidapi lists a device once per interface and libusb lists it again. One entry per physical device, from the
			// HID list where there is one, since it has the usage and the product string.
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var info in devices.OrderBy(d => d.Kind == DofDeviceKind.Hid ? 0 : 1)) {
				var detected = Classify(info, ref ftdiIndex);
				if (detected != null && seen.Add($"{detected.Type}|{info.VendorId}|{info.ProductId}|{Identity(detected, info)}")) {
					result.Add(detected);
				}
			}
			return result;
		}

		/// <summary>
		/// What tells two physical devices of one type apart: the DOF device number where the IDs give one, the USB port
		/// for FT245R boards (libusb lists no serial number), the serial number otherwise. A device without a serial
		/// number is told apart by its path (HID, serial) or USB port, so two anonymous boards are never merged; the
		/// interface filters of <see cref="Classify"/> keep one entry per board for those.
		/// </summary>
		private static string Identity(DofDetectedDevice detected, DofDeviceInfo info)
		{
			if (detected.Type == "FT245RBitbangController") {
				return $"usb:{info.Bus}:{info.PortPath}";
			}
			if (detected.Device > 0) {
				return $"device:{detected.Device}";
			}
			if (!string.IsNullOrEmpty(info.SerialNumber)) {
				return $"serial:{info.SerialNumber}";
			}
			switch (info.Kind) {
				case DofDeviceKind.Usb: return $"usb:{info.Bus}:{info.PortPath}";
				default: return $"path:{info.Path}";
			}
		}

		private static DofDetectedDevice Classify(DofDeviceInfo info, ref int ftdiIndex)
		{
			var vid = info.VendorId;
			var pid = info.ProductId;
			if (info.Kind == DofDeviceKind.Serial) {
				// libdof's PinOne auto-configurator looks for this ID among the serial ports; other serial controllers are
				// configured by port name only.
				return vid == PinOneLegacyVendor && pid == PinOneLegacyProduct ? Make("PinOne", -1, "11 and up", info) : null;
			}
			var product = info.Product ?? string.Empty;
			var pinscapeByName = product.IndexOf("pinscape", StringComparison.OrdinalIgnoreCase) >= 0;

			if (info.Kind == DofDeviceKind.Hid) {
				// libdof's Pinscape Pico scan, on any IDs: the feedback interface (usage page 6, usage 0) of a device whose
				// product string is exactly "PinscapePico". Checked first: a Pico may use the KL25Z's IDs.
				if (info.UsagePage == 0x06 && info.Usage == 0x00 && product == "PinscapePico") {
					return Make("PinscapePico", -1, "120 and up", info);
				}
				var ledWizId = vid == LedWizVendor && pid >= 0x00F0 && pid <= 0x00FF;
				// libdof's Pinscape scan: a LedWiz or Pinscape ID, a release above 7, "pinscape" in the product string
				// and the joystick interface. Its unit number is what the board reports when libdof asks, not
				// necessarily the one of a LedWiz ID it emulates.
				if ((ledWizId || (vid == PinscapeVendor && pid == PinscapeProduct)) && info.Release > 7 && pinscapeByName) {
					return PinscapeInterface(info) ? Make("Pinscape", -1, "51-66", info) : null;
				}
				// libdof's LedWiz scan, which takes Pinscape firmware the Pinscape scan does not as well.
				if (ledWizId && pid < 0x00FF) {
					return Make("LedWiz", pid - 0x00EF, "1-16", info);
				}
				if (vid == 0x20A0 && info.Manufacturer?.IndexOf("zebsboards", StringComparison.OrdinalIgnoreCase) >= 0 && pid >= 0x00F0 && pid < 0x00FF) {
					return Make("LedWiz", pid - 0x00EF, "1-16", info);
				}
				if (vid == PinOneVendor && (pid & 0xFF00) == 0x9200 && (info.UsagePage < 0 || (info.UsagePage == 0xFF00 && info.Usage == 0x01))) {
					return Make("PinOne", -1, "11 and up", info);
				}
				// libdof drives a Dude's Cab through its outputs interface ("DudesCab Outputs", or "DudesCab" interface 3
				// where the product string is the same for all); the MX interface belongs to the same board.
				if (vid == DudesCabVendor && pid == DudesCabProduct && (product == "DudesCab Outputs" || (product == "DudesCab" && info.Interface == 3))) {
					return Make("DudesCab", -1, "90 and up", info);
				}
			}
			if (vid == UltimarcVendor) {
				if (pid >= 0x1401 && pid <= 0x1404) {
					return Make("PacLed64", pid - 0x1401 + 20, "20-23", info);
				}
				if (pid == 0x1500) {
					return Make("PacDrive", 19, "19", info);
				}
				if (pid == 0x0410 || pid == 0x0411) {
					return Make("PacUIO", pid - 0x0410 + 27, "27-28", info);
				}
			}
			if (info.Kind == DofDeviceKind.Usb && vid == FtdiVendor && pid == Ft245RProduct) {
				// libdof numbers FT245R boards in the order libftdi lists them, from 40.
				return Make("FT245RBitbangController", 40 + ftdiIndex++, "40 and up", info);
			}
			return null;
		}

		/// <summary>The interface libdof opens on a Pinscape: usage page 1, usage 4 (joystick) or 0.</summary>
		private static bool PinscapeInterface(DofDeviceInfo info) => info.UsagePage == 1 && (info.Usage == 4 || info.Usage == 0);

		private static DofDetectedDevice Make(string type, int device, string range, DofDeviceInfo info)
		{
			return new DofDetectedDevice(type, device, range, info, NotesFor(type, info));
		}

		private static IReadOnlyList<string> NotesFor(string type, DofDeviceInfo info)
		{
			var notes = new List<string>();
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
				if (info.Kind == DofDeviceKind.Hid && !string.IsNullOrEmpty(info.Path) && info.Path.StartsWith("/dev/", StringComparison.Ordinal) && !CanOpenHid(info.Path)) {
					notes.Add($"No access to {info.Path}: add a udev rule that gives your user read and write access to this device.");
				}
				if (info.Kind == DofDeviceKind.Usb) {
					notes.Add("libusb needs write access to the USB device: add a udev rule for it if DOF cannot open it.");
				}
			} else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
				if (type == "PinscapePico") {
					notes.Add("macOS lets only elevated processes open Pinscape Pico's feedback interface.");
				}
			} else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				if (type == "FT245RBitbangController") {
					notes.Add("DOF opens FT245R boards through libusb: install the WinUSB driver for the board (for example with Zadig) instead of FTDI's driver.");
				}
			}
			return notes;
		}

		/// <summary>Opens and closes a hidraw node, which reads and writes nothing, to see whether the user may use it.</summary>
		private static bool CanOpenHid(string path)
		{
			try {
				using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.None)) {
					return true;
				}
			} catch (UnauthorizedAccessException) {
				return false;
			} catch (IOException) {
				return true;
			}
		}
	}
}
