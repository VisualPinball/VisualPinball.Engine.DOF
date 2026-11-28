// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;

namespace VisualPinball.Engine.DOF.Native
{
	/// <summary>
	/// P/Invoke declarations of VpeDof's C ABI (native/include/vpe_dof.h). Strings are passed as NUL-terminated UTF-8
	/// byte arrays and read back with <see cref="Utf8"/>, so no runtime-specific string marshalling is involved.
	/// </summary>
	internal static class DofNative
	{
		public const string Library = "VpeDof";
		public const int AbiVersion = 6;

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void LogCallback(int level, IntPtr message, IntPtr user);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void StringCallback(IntPtr value, IntPtr user);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void ControllerCallback(int loaded, IntPtr type, IntPtr name, IntPtr reason, IntPtr user);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_abi_version")]
		public static extern int GetAbiVersion();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_libdof_version")]
		public static extern IntPtr GetLibdofVersion();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_set_log")]
		public static extern void SetLog(LogCallback callback, IntPtr user, int level);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_create")]
		public static extern IntPtr Create(byte[] basePath, out int status);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_init")]
		public static extern int Init(IntPtr dof, byte[] tableFile, byte[] romName, byte[] error, int errorSize);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_data")]
		public static extern void Data(IntPtr dof, byte type, int number, int value);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_data_named")]
		public static extern void DataNamed(IntPtr dof, byte[] name, int value);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_pending_input")]
		public static extern int PendingInput(IntPtr dof);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_finish")]
		public static extern int Finish(IntPtr dof, byte[] error, int errorSize);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_destroy")]
		public static extern void Destroy(IntPtr dof);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_set_output_gate")]
		public static extern long SetOutputGate(IntPtr dof, int open);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_set_connect_tests")]
		public static extern int SetConnectTests(IntPtr dof, int allowed);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_set_output_map")]
		public static extern int SetOutputMap(IntPtr dof, int device, int[] targets, int count);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_latch_output_gate")]
		public static extern long LatchOutputGate(IntPtr dof);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_gate_state")]
		public static extern int GateState(IntPtr dof, long generation, out int confirmed, out int pending, out int failed, out int untouched);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_gate_pending")]
		public static extern int GatePending(IntPtr dof, long generation, StringCallback callback, IntPtr user);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_test_output")]
		public static extern int TestOutput(IntPtr dof, int device, int port, int value);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_describe")]
		public static extern int Describe(IntPtr dof, StringCallback callback, IntPtr user);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_rom_names")]
		public static extern int RomNames(byte[] basePath, byte[] tableFile, StringCallback callback, IntPtr user);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_cabinet_controllers")]
		public static extern int CabinetControllers(byte[] basePath, ControllerCallback callback, IntPtr user, byte[] error, int errorSize);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vpe_dof_enumerate_devices")]
		public static extern int EnumerateDevices(StringCallback callback, IntPtr user);
	}
}
