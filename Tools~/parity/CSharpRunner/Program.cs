// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using VisualPinball.Engine.DOF.Parity;

namespace VisualPinball.Engine.DOF.Parity.CSharp
{
	/// <summary>
	/// Runs one scenario on C# DirectOutput: CSharpRunner &lt;config root&gt; &lt;table file&gt; &lt;rom&gt; &lt;scenario file&gt;.
	/// Prints "SAMPLE n" when the orchestrator should take sample n and waits for its "ACK" line on standard input, and
	/// prints "DONE" after finishing.
	/// </summary>
	internal static class Program
	{
		private static int Main(string[] args)
		{
			if (args.Length < 4) {
				Console.Error.WriteLine("usage: CSharpRunner <config root> <table file> <rom> <scenario file>");
				return 2;
			}
			var scenario = Scenario.Load(args[3]);
			var pinball = new DirectOutput.Pinball();
			pinball.Setup(Path.Combine(args[0], "directoutputconfig", "GlobalConfig_B2SServer.xml"), args[1], args[2]);
			pinball.Init();
			scenario.Run((type, number, value) => pinball.ReceiveData(type, number, value),
				(name, value) => pinball.ReceiveData(name, value),
				sample => {
					Console.Out.WriteLine("SAMPLE " + sample);
					Console.Out.Flush();
					if (Console.In.ReadLine() != "ACK") {
						throw new InvalidOperationException("The orchestrator did not acknowledge sample " + sample + ".");
					}
				});
			pinball.Finish();
			Console.Out.WriteLine("DONE");
			return 0;
		}
	}
}
