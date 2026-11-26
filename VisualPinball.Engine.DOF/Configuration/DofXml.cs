// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

		/// <summary>The trimmed text of a child element (<see cref="FirstChild"/>), or null.</summary>
		internal static string Value(XElement parent, string name)
		{
			var element = FirstChild(parent, name);
			return element == null ? null : element.Value.Trim();
		}

		/// <summary>
		/// An element's name as libdof's XML parser (tinyxml2) sees it: as written, with its prefix if it has one. A default
		/// namespace (<c>xmlns="..."</c>) does not change it, so libdof reads such a file like one without.
		/// </summary>
		public static string LiteralName(XElement element)
		{
			var ns = element.Name.Namespace;
			if (ns == XNamespace.None) {
				return element.Name.LocalName;
			}
			var prefix = element.GetPrefixOfNamespace(ns);
			return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
		}

		/// <summary>The child elements with a name as libdof matches it (<see cref="LiteralName"/>).</summary>
		public static IEnumerable<XElement> Children(XElement parent, string name)
			=> parent == null ? Enumerable.Empty<XElement>() : parent.Elements().Where(e => LiteralName(e) == name);

		/// <summary>The first child element with a name as libdof matches it (tinyxml2's <c>FirstChildElement</c>), or null.</summary>
		public static XElement FirstChild(XElement parent, string name) => Children(parent, name).FirstOrDefault();

		/// <summary>
		/// An element's text as libdof reads it (tinyxml2's <c>GetText</c>): the first child that is not a comment, if it
		/// is text, as written; otherwise null. tinyxml2 keeps no text that is only whitespace, so neither does this.
		/// </summary>
		public static string Text(XElement element)
		{
			foreach (var node in element?.Nodes() ?? Enumerable.Empty<XNode>()) {
				if (node is XComment || (node is XText space && !(node is XCData) && string.IsNullOrWhiteSpace(space.Value))) {
					continue;
				}
				return node is XText text ? text.Value : null;
			}
			return null;
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

		/// <summary>
		/// The name a cabinet controller gives itself, read as libdof's <c>FromXml</c> of its type reads it, or null when
		/// it gives none and libdof names it itself (after its number or type). The pac boards, FT245R boards and the
		/// null controller take only a <c>Name</c> attribute; a LedWiz only a <c>Name</c> element; the others the
		/// attribute if there is one, else the element. The name is as written; an empty one is none.
		/// </summary>
		public static string ControllerName(XElement controller)
		{
			string name;
			switch (LiteralName(controller)) {
				case "NullOutputController":
				case "FT245RBitbangController":
				case "PacDrive":
				case "PacLed64":
				case "PacUIO":
					name = controller.Attribute("Name")?.Value;
					break;
				case "LedWiz":
					name = Text(FirstChild(controller, "Name"));
					break;
				default:
					var attribute = controller.Attribute("Name");
					name = attribute != null ? attribute.Value : Text(FirstChild(controller, "Name"));
					break;
			}
			return string.IsNullOrEmpty(name) ? null : name;
		}

		/// <summary>The serial port a cabinet controller names, or null (none, or PinOne on HID).</summary>
		internal static string SerialPortOf(XElement controller)
		{
			var type = LiteralName(controller);
			var element = SerialPortElement(type);
			var port = element == null ? null : Value(controller, element);
			if (string.IsNullOrEmpty(port) || (type == "PinOne" && port == "HID")) {
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
