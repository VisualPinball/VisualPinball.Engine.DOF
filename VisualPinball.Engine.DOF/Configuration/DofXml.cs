// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>The kinds of XML file a DOF configuration contains, by their root element.</summary>
	public enum DofXmlKind
	{
		Unknown,
		GlobalConfig,
		Cabinet,
		Table,
		TableMappings,
		Shapes,
	}

	/// <summary>Loads configuration XML without DTD processing, external resolution or unbounded entity expansion.</summary>
	public static class DofXml
	{
		/// <summary>Largest XML document read, in characters.</summary>
		public const long MaxCharacters = 64L * 1024 * 1024;

		public static XDocument Load(string path)
		{
			var settings = new XmlReaderSettings {
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = MaxCharacters,
				MaxCharactersFromEntities = 0,
				IgnoreComments = true,
			};
			using (var stream = File.OpenRead(path))
			using (var reader = XmlReader.Create(stream, settings)) {
				return XDocument.Load(reader, LoadOptions.None);
			}
		}

		public static DofXmlKind KindOf(XDocument document)
		{
			switch (document.Root?.Name.LocalName) {
				case "GlobalConfig": return DofXmlKind.GlobalConfig;
				case "Cabinet": return DofXmlKind.Cabinet;
				case "Table": return DofXmlKind.Table;
				case "TableNameMappings": return DofXmlKind.TableMappings;
				case "ShapeDefinitions": return DofXmlKind.Shapes;
				default: return DofXmlKind.Unknown;
			}
		}

		/// <summary>The trimmed text of a child element of the root, or null.</summary>
		internal static string Value(XElement parent, string name)
		{
			var element = parent?.Element(name);
			return element == null ? null : element.Value.Trim();
		}

		/// <summary>
		/// The child element that holds a cabinet controller's serial port, as libdof's loader reads it, or null if the
		/// controller type has none: <c>ComPort</c> for PinControl and PinOne, <c>ComPortName</c> for the Teensy and Wemos
		/// strip controllers.
		/// </summary>
		internal static string SerialPortElement(string controllerType)
		{
			switch (controllerType) {
				case "PinControl":
				case "PinOne":
					return "ComPort";
				case "TeensyStripController":
				case "WemosD1MPStripController":
					return "ComPortName";
				default:
					return null;
			}
		}

		/// <summary>The serial port a cabinet controller names, or null (none, or PinOne on HID).</summary>
		internal static string SerialPortOf(XElement controller)
		{
			var element = SerialPortElement(controller.Name.LocalName);
			var port = element == null ? null : Value(controller, element);
			if (string.IsNullOrEmpty(port) || (controller.Name.LocalName == "PinOne" && port == "HID")) {
				return null;
			}
			return port;
		}

		internal static void Save(XDocument document, string path)
		{
			var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", Encoding = new System.Text.UTF8Encoding(false) };
			var temp = path + ".tmp";
			using (var writer = XmlWriter.Create(temp, settings)) {
				document.Save(writer);
			}
			if (File.Exists(path)) {
				File.Delete(path);
			}
			File.Move(temp, path);
		}

		internal static Exception Describe(string fileName, Exception e)
		{
			return new FormatException($"{fileName} is not valid XML: {e.Message}", e);
		}
	}
}
