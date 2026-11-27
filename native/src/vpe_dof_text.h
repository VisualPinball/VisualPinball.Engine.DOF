// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

#pragma once

#include <cstdint>
#include <cstring>
#include <string>

namespace vpe_dof_text
{

// Copies the message, cut at a character boundary if the buffer is too small.
inline void WriteError(char* buffer, int32_t size, const char* message)
{
	if (!buffer || size <= 0)
		return;
	const char* text = message ? message : "";
	size_t length = std::strlen(text);
	size_t capacity = static_cast<size_t>(size) - 1;
	if (length > capacity)
	{
		length = capacity;
		while (length > 0 && (static_cast<unsigned char>(text[length]) & 0xC0) == 0x80)
			length--;
	}
	std::memcpy(buffer, text, length);
	buffer[length] = '\0';
}

// The error of an init that failed. When stopping what it started failed too, outputs may still be on: that comes first,
// so a short buffer keeps it, then why init failed and why stopping failed. Init is not tried again after it, and a later
// finish finds the instance finished, so this is the only report of it.
inline std::string InitFailure(const std::string& initError, const std::string& finishError)
{
	if (finishError.empty())
		return initError;
	return "stopping what init started failed, outputs may still be on: " + initError + "; " + finishError;
}

}
