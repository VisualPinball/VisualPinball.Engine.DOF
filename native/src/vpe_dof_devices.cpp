// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

// Device enumeration for the host's detection page. Nothing here opens a device for output: hidapi and libserialport
// only list, libusb reads the descriptors the OS caches.

#include "vpe_dof.h"

#include <algorithm>
#include <cstdint>
#include <cwchar>
#include <mutex>
#include <string>
#include <vector>

#include <hidapi/hidapi.h>
#include <libserialport.h>
#include <libusb-1.0/libusb.h>

// vpe_dof.cpp: serializes hidapi's init and exit, which the DOF instance does, against enumeration.
std::mutex& VpeDofHidLock();

namespace
{

std::string Clean(std::string value)
{
	std::replace(value.begin(), value.end(), '|', '/');
	std::replace(value.begin(), value.end(), '\n', ' ');
	std::replace(value.begin(), value.end(), '\r', ' ');
	return value;
}

std::string Clean(const char* value) { return Clean(std::string(value ? value : "")); }

void AppendUtf8(std::string& out, uint32_t cp)
{
	if (cp < 0x80)
		out += static_cast<char>(cp);
	else if (cp < 0x800)
	{
		out += static_cast<char>(0xC0 | (cp >> 6));
		out += static_cast<char>(0x80 | (cp & 0x3F));
	}
	else if (cp < 0x10000)
	{
		out += static_cast<char>(0xE0 | (cp >> 12));
		out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
		out += static_cast<char>(0x80 | (cp & 0x3F));
	}
	else
	{
		out += static_cast<char>(0xF0 | (cp >> 18));
		out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
		out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
		out += static_cast<char>(0x80 | (cp & 0x3F));
	}
}

// wchar_t is UTF-16 on Windows and UTF-32 elsewhere.
std::string Utf8(const wchar_t* value)
{
	std::string out;
	if (!value)
		return out;
	for (const wchar_t* p = value; *p; ++p)
	{
		uint32_t cp = static_cast<uint32_t>(*p);
		if (sizeof(wchar_t) == 2 && cp >= 0xD800 && cp <= 0xDBFF && p[1])
		{
			uint32_t low = static_cast<uint32_t>(p[1]);
			if (low >= 0xDC00 && low <= 0xDFFF)
			{
				cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
				++p;
			}
		}
		if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
			cp = 0xFFFD;
		AppendUtf8(out, cp);
	}
	return Clean(out);
}

int32_t EnumerateHid(vpe_dof_string_fn callback, void* user)
{
	// hid_init is idempotent; hid_exit is left to the DOF instance, which may be running. The lock keeps a destroying
	// instance from calling hid_exit while the list is built. The host is called after the lock is released, so a
	// callback that enumerates again or creates an instance cannot deadlock.
	std::vector<std::string> lines;
	{
		std::lock_guard<std::mutex> lock(VpeDofHidLock());
		if (hid_init() != 0)
			return 0;
		hid_device_info* devices = hid_enumerate(0, 0);
		for (hid_device_info* d = devices; d; d = d->next)
		{
			lines.push_back("hid|" + std::to_string(d->vendor_id) + "|" + std::to_string(d->product_id) + "|" + std::to_string(d->usage_page) + "|"
				+ std::to_string(d->usage) + "|" + std::to_string(d->interface_number) + "|" + std::to_string(d->release_number) + "|"
				+ Utf8(d->manufacturer_string) + "|" + Utf8(d->product_string) + "|" + Utf8(d->serial_number) + "|" + Clean(d->path));
		}
		hid_free_enumeration(devices);
	}
	for (const std::string& line : lines)
		callback(line.c_str(), user);
	return static_cast<int32_t>(lines.size());
}

int32_t EnumerateUsb(vpe_dof_string_fn callback, void* user)
{
	int32_t count = 0;
	libusb_context* context = nullptr;
	if (libusb_init(&context) != 0)
		return 0;
	libusb_device** list = nullptr;
	ssize_t n = libusb_get_device_list(context, &list);
	for (ssize_t i = 0; i < n; i++)
	{
		libusb_device* device = list[i];
		libusb_device_descriptor descriptor {};
		if (libusb_get_device_descriptor(device, &descriptor) != 0)
			continue;
		uint8_t ports[8];
		int depth = libusb_get_port_numbers(device, ports, static_cast<int>(sizeof(ports)));
		std::string portPath;
		for (int p = 0; p < depth; p++)
			portPath += (p ? "." : "") + std::to_string(ports[p]);
		std::string line = "usb|" + std::to_string(descriptor.idVendor) + "|" + std::to_string(descriptor.idProduct) + "|"
			+ std::to_string(libusb_get_bus_number(device)) + "|" + std::to_string(libusb_get_device_address(device)) + "|" + portPath;
		callback(line.c_str(), user);
		count++;
	}
	if (list)
		libusb_free_device_list(list, 1);
	libusb_exit(context);
	return count;
}

int32_t EnumerateSerial(vpe_dof_string_fn callback, void* user)
{
	int32_t count = 0;
	sp_port** ports = nullptr;
	if (sp_list_ports(&ports) != SP_OK)
		return 0;
	for (int i = 0; ports && ports[i]; i++)
	{
		sp_port* port = ports[i];
		std::string transport;
		int vid = -1, pid = -1;
		std::string serial;
		switch (sp_get_port_transport(port))
		{
		case SP_TRANSPORT_USB:
			transport = "usb";
			if (sp_get_port_usb_vid_pid(port, &vid, &pid) != SP_OK)
				vid = pid = -1;
			serial = Clean(sp_get_port_usb_serial(port));
			break;
		case SP_TRANSPORT_BLUETOOTH: transport = "bluetooth"; break;
		default: transport = "native"; break;
		}
		std::string line = "serial|" + Clean(sp_get_port_name(port)) + "|" + Clean(sp_get_port_description(port)) + "|" + transport + "|"
			+ std::to_string(vid) + "|" + std::to_string(pid) + "|" + serial;
		callback(line.c_str(), user);
		count++;
	}
	sp_free_port_list(ports);
	return count;
}

}

extern "C" VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_enumerate_devices(vpe_dof_string_fn callback, void* user)
{
	if (!callback)
		return VPE_DOF_INVALID_ARGUMENT;
	try
	{
		EnumerateHid(callback, user);
		EnumerateUsb(callback, user);
		EnumerateSerial(callback, user);
		return VPE_DOF_OK;
	}
	catch (...)
	{
		return VPE_DOF_ERROR;
	}
}
