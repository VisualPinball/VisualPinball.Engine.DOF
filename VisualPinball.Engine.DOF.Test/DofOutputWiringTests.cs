// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VisualPinball.Engine.DOF.Configuration;

namespace VisualPinball.Engine.DOF.Test
{
	public class DofOutputWiringTests
	{
		private string _root;

		[SetUp]
		public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "vpe-dof-wiring-" + Guid.NewGuid().ToString("N"));

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_root)) {
				Directory.Delete(_root, true);
			}
		}

		[Test]
		public void SwapsSingleOutputsBothWays()
		{
			var wiring = DofOutputWiring.None.Swap(1, 16, 14, 1);
			Assert.That(wiring.OutputOf(1, 16), Is.EqualTo(14));
			Assert.That(wiring.OutputOf(1, 14), Is.EqualTo(16));
			Assert.That(wiring.PortOn(1, 14), Is.EqualTo(16));
			Assert.That(wiring.OutputOf(2, 16), Is.EqualTo(16), "other devices keep their own");
			Assert.That(wiring.MapOf(1).Count, Is.EqualTo(16), "up to the last moved port");

			// Swapping the same outputs back leaves no map at all.
			Assert.That(wiring.Swap(1, 14, 16, 1).IsEmpty, Is.True);
		}

		[Test]
		public void SwapsByOutputNotByPort()
		{
			// Port 16 drives output 14; moving output 14 to 3 moves port 16 along.
			var wiring = DofOutputWiring.None.Swap(1, 16, 14, 1).Swap(1, 14, 3, 1);
			Assert.That(wiring.OutputOf(1, 16), Is.EqualTo(3));
			Assert.That(wiring.OutputOf(1, 3), Is.EqualTo(14));
			Assert.That(wiring.OutputOf(1, 14), Is.EqualTo(16));
		}

		[Test]
		public void SwapsRunsInOrder()
		{
			var wiring = DofOutputWiring.None.Swap(2, 17, 4, 3);
			Assert.That(Enumerable.Range(17, 3).Select(p => wiring.OutputOf(2, p)), Is.EqualTo(new[] { 4, 5, 6 }));
			Assert.That(Enumerable.Range(4, 3).Select(p => wiring.OutputOf(2, p)), Is.EqualTo(new[] { 17, 18, 19 }));
			Assert.Throws<ArgumentOutOfRangeException>(() => wiring.Swap(2, 4, 5, 3), "overlapping runs");
			Assert.Throws<ArgumentOutOfRangeException>(() => wiring.Swap(2, DofOutputWiring.MaxOutput, 1, 2), "past the last output");
			Assert.Throws<ArgumentOutOfRangeException>(() => wiring.Swap(DofOutputWiring.MaxDevice + 1, 1, 2, 1), "a device DOF does not take");
		}

		[Test]
		public void SavesAndLoads()
		{
			var wiring = DofOutputWiring.None.Swap(1, 16, 14, 1).Swap(30, 1, 2, 1);
			wiring.Save(_root);
			var loaded = DofOutputWiring.Load(_root);
			Assert.That(loaded.Devices, Is.EqualTo(new[] { 1, 30 }));
			Assert.That(loaded.MapOf(1), Is.EqualTo(wiring.MapOf(1)));
			Assert.That(loaded.MapOf(30), Is.EqualTo(new[] { 2, 1 }));

			loaded.Without(1).Without(30).Save(_root);
			Assert.That(File.Exists(Path.Combine(_root, DofOutputWiring.FileName)), Is.False, "no file without a map");
			Assert.That(DofOutputWiring.Load(_root).IsEmpty, Is.True);
		}

		[Test]
		public void LeavesOutLinesThatAreNoPermutation()
		{
			Directory.CreateDirectory(_root);
			File.WriteAllText(Path.Combine(_root, DofOutputWiring.FileName), "1=2,1\n2=1,1\n3=3,1\nx=1\n0=1\n4=\n5=2,1,3\n6=2,,1\n");
			var loaded = DofOutputWiring.Load(_root);
			Assert.That(loaded.Devices, Is.EqualTo(new[] { 1, 5 }));
			Assert.That(loaded.MapOf(5), Is.EqualTo(new[] { 2, 1 }), "trailing ports that keep their own are left out");
		}
	}
}
