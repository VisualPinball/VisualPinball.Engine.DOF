// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VisualPinball.Engine.DOF.Native
{
	internal static class Utf8
	{
		private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, false);

		/// <summary>A NUL-terminated UTF-8 copy of the string; null stays null, which the ABI accepts as empty.</summary>
		public static byte[] Encode(string value)
		{
			if (value == null) {
				return null;
			}
			var bytes = new byte[Encoding.GetByteCount(value) + 1];
			Encoding.GetBytes(value, 0, value.Length, bytes, 0);
			return bytes;
		}

		public static unsafe string Decode(IntPtr value)
		{
			if (value == IntPtr.Zero) {
				return null;
			}
			var p = (byte*)value;
			var length = 0;
			while (p[length] != 0) {
				length++;
			}
			return Encoding.GetString(p, length);
		}

		/// <summary>The message a native call wrote into an error buffer.</summary>
		public static string DecodeBuffer(byte[] buffer)
		{
			var length = Array.IndexOf(buffer, (byte)0);
			return Encoding.GetString(buffer, 0, length < 0 ? buffer.Length : length);
		}
	}

	/// <summary>
	/// Marks a static method as a native callback. IL2CPP generates a reverse P/Invoke wrapper for methods with an
	/// attribute of this name; Mono and .NET ignore it.
	/// </summary>
	[AttributeUsage(AttributeTargets.Method)]
	internal sealed class MonoPInvokeCallbackAttribute : Attribute
	{
		public MonoPInvokeCallbackAttribute(Type type) { }
	}

	/// <summary>Collects the strings a native list function reports through <see cref="DofNative.StringCallback"/>.</summary>
	internal static class StringCollector
	{
		public static readonly DofNative.StringCallback Callback = OnString;

		[MonoPInvokeCallback(typeof(DofNative.StringCallback))]
		private static void OnString(IntPtr value, IntPtr user)
		{
			if (user == IntPtr.Zero) {
				return;
			}
			if (GCHandle.FromIntPtr(user).Target is System.Collections.Generic.List<string> list) {
				list.Add(Utf8.Decode(value) ?? string.Empty);
			}
		}

		/// <summary>Runs a native list call with a collector and returns its status and the collected strings.</summary>
		public static System.Collections.Generic.List<string> Collect(Func<IntPtr, int> call, out int status)
		{
			var list = new System.Collections.Generic.List<string>();
			var handle = GCHandle.Alloc(list);
			try {
				status = call(GCHandle.ToIntPtr(handle));
			} finally {
				handle.Free();
			}
			return list;
		}
	}
}
