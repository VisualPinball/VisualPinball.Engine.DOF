// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Linq;
using NUnit.Framework;
using VisualPinball.Engine.DOF.Detection;

namespace VisualPinball.Engine.DOF.Test
{
	[TestFixture]
	public class DofDetectionTests
	{
		// A Pinscape release libdof takes: it skips releases up to 7.
		private const int PinscapeRelease = 8;

		private static DofDeviceInfo Hid(int vid, int pid, string product = "", string manufacturer = "", int usagePage = 1, int usage = 0, string serial = "", int iface = 0, string path = null, int release = 1)
			=> DofDeviceInfo.Parse($"hid|{vid}|{pid}|{usagePage}|{usage}|{iface}|{release}|{manufacturer}|{product}|{serial}|{path ?? $"path-{vid}-{pid}-{iface}"}");

		private static DofDeviceInfo Pinscape(int vid = 0x1209, int pid = 0xEAEA, string product = "Pinscape Controller", int usage = 0, string serial = "", int iface = 0, string path = null, int release = PinscapeRelease)
			=> Hid(vid, pid, product, usage: usage, serial: serial, iface: iface, path: path, release: release);

		private static DofDeviceInfo Usb(int vid, int pid, string port = "1.2") => DofDeviceInfo.Parse($"usb|{vid}|{pid}|1|5|{port}");

		[Test]
		public void ClassifiesControllersWithTheirDeviceNumbers()
		{
			var detected = DofDetection.Detect(new[] {
				Hid(0xFAFA, 0x00F0, "LED-Wiz"),
				Pinscape(0xFAFA, 0x00F3),
				Hid(0xD209, 0x1402),
				Hid(0xD209, 0x1500),
				Usb(0x0403, 0x6001, "1.1"),
				Usb(0x0403, 0x6001, "1.3"),
				Hid(0x046D, 0xC52B, "Mouse"),
			});
			Assert.That(detected.Select(d => (d.Type, d.Device)), Is.EqualTo(new[] {
				("LedWiz", 1), ("Pinscape", -1), ("PacLed64", 21), ("PacDrive", 19), ("FT245RBitbangController", 40), ("FT245RBitbangController", 41),
			}));
		}

		[Test]
		public void ListsAPhysicalDeviceOnce()
		{
			// hidapi lists one entry per interface, libusb the device again.
			var detected = DofDetection.Detect(new[] {
				Usb(0x1209, 0xEAEA),
				Pinscape(serial: "A1", iface: 0),
				Pinscape(serial: "A1", iface: 1),
			});
			Assert.That(detected, Has.Count.EqualTo(1));
			Assert.That(detected[0].Info.Kind, Is.EqualTo(DofDeviceKind.Hid), "the HID entry, which has the product name");
			Assert.That(detected[0].DeviceRange, Is.EqualTo("51-66"));

			// Ultimarc boards appear in both lists, the HID one with a serial number.
			Assert.That(DofDetection.Detect(new[] { Usb(0xD209, 0x1401), Hid(0xD209, 0x1401, serial: "P1") }), Has.Count.EqualTo(1));
		}

		[Test]
		public void PinscapeOnLedWizIdsHasAnUnknownUnit()
		{
			// The device reports its unit number when libdof asks; the LedWiz ID it emulates need not be it.
			var detected = DofDetection.Detect(new[] { Pinscape(0xFAFA, 0x00F3) });
			Assert.That((detected[0].Type, detected[0].Device, detected[0].DeviceRange), Is.EqualTo(("Pinscape", -1, "51-66")));
		}

		[Test]
		public void FindsPinscapePicoAsLibdofDoes()
		{
			// Its feedback interface (usage page 6, usage 0) and the exact product string, whatever the IDs.
			var detected = DofDetection.Detect(new[] { Hid(0x1209, 0xEAEA, "PinscapePico", usagePage: 6, usage: 0, release: PinscapeRelease) });
			Assert.That(detected.Select(d => (d.Type, d.Device, d.DeviceRange)), Is.EqualTo(new[] { ("PinscapePico", -1, "120 and up") }));
			Assert.That(DofDetection.Detect(new[] { Hid(0x2E8A, 0x1234, "PinscapePico", usagePage: 6, usage: 0) }).Select(d => d.Type), Is.EqualTo(new[] { "PinscapePico" }));
			Assert.That(DofDetection.Detect(new[] { Hid(0x2E8A, 0x1234, "Pinscape Pico", usagePage: 6, usage: 0) }), Is.Empty, "libdof compares the whole name");
		}

		[Test]
		public void FindsPinscapeAsLibdofDoes()
		{
			// libdof's Pinscape scan wants a release above 7 and "pinscape" in the product string, on either ID.
			Assert.That(DofDetection.Detect(new[] { Pinscape(release: 7) }), Is.Empty);
			Assert.That(DofDetection.Detect(new[] { Pinscape(product: "KL25Z Controller") }), Is.Empty);
			Assert.That(DofDetection.Detect(new[] { Pinscape(usage: 6) }), Is.Empty, "not the joystick interface");
			// Older firmware on a LedWiz ID is a LedWiz to libdof.
			var detected = DofDetection.Detect(new[] { Pinscape(0xFAFA, 0x00F3, release: 7) });
			Assert.That(detected.Select(d => (d.Type, d.Device)), Is.EqualTo(new[] { ("LedWiz", 4) }));
		}

		[Test]
		public void FindsTheLegacySerialPinOne()
		{
			// libdof's PinOneAutoConfigurator looks for VID_0E8F&PID_9207 among the serial ports.
			var detected = DofDetection.Detect(new[] { DofDeviceInfo.Parse($"serial|COM7|USB Serial Device|usb|{0x0E8F}|{0x9207}|") });
			Assert.That(detected.Select(d => (d.Type, d.Device, d.DeviceRange)), Is.EqualTo(new[] { ("PinOne", -1, "11 and up") }));
		}

		[Test]
		public void TellsAnonymousBoardsApart()
		{
			// Two Pinscapes without serial numbers are two boards; a board's other interfaces are not.
			var detected = DofDetection.Detect(new[] {
				Pinscape(usage: 4, path: "a-joystick"),
				Pinscape(usage: 6, path: "a-keyboard"),
				Pinscape(usage: 4, path: "b-joystick"),
			});
			Assert.That(detected.Select(d => d.Info.Path), Is.EqualTo(new[] { "a-joystick", "b-joystick" }));

			// A Dude's Cab shows its outputs and its MX interface; libdof drives it through the first.
			detected = DofDetection.Detect(new[] {
				Hid(0x2E8A, 0x106F, "DudesCab Outputs", path: "outputs"),
				Hid(0x2E8A, 0x106F, "DudesCab Outputs MX", path: "mx"),
			});
			Assert.That(detected.Select(d => (d.Type, d.Info.Path)), Is.EqualTo(new[] { ("DudesCab", "outputs") }));
		}

		[Test]
		public void IgnoresSerialPortsAndUnknownDevices()
		{
			var serial = DofDeviceInfo.Parse("serial|COM3|USB Serial|usb|1027|24577|X1");
			Assert.That(serial.Kind, Is.EqualTo(DofDeviceKind.Serial));
			Assert.That(DofDetection.Detect(new[] { serial, Hid(0x1234, 0x5678) }), Is.Empty);
			Assert.That(DofDeviceInfo.Parse("bogus|1"), Is.Null);
		}
	}
}
