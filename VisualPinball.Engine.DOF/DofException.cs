// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace VisualPinball.Engine.DOF
{
	/// <summary>Status codes of VpeDof's C ABI.</summary>
	public enum DofStatus
	{
		Ok = 0,
		Error = 1,
		InvalidArgument = 2,
		InvalidState = 3,
		/// <summary>Another DOF instance exists in this process.</summary>
		Busy = 4,
	}

	/// <summary>A failed VpeDof call, with libdof's message where it gave one.</summary>
	public class DofException : Exception
	{
		public DofStatus Status { get; }

		public DofException(DofStatus status, string message) : base(message)
		{
			Status = status;
		}

		public DofException(string message, Exception inner) : base(message, inner)
		{
			Status = DofStatus.Error;
		}

		internal static void ThrowIfFailed(int status, string operation, string detail = null)
		{
			if (status == (int)DofStatus.Ok) {
				return;
			}
			var s = (DofStatus)status;
			var message = string.IsNullOrEmpty(detail) ? $"{operation} failed ({s})." : $"{operation} failed ({s}): {detail}";
			throw new DofException(s, message);
		}
	}
}
