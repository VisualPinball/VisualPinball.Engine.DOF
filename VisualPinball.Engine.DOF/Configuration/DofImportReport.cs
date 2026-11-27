// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Text;

namespace VisualPinball.Engine.DOF.Configuration
{
	public enum DofReportSeverity
	{
		/// <summary>Worth knowing; nothing is wrong.</summary>
		Info,
		/// <summary>Imported, but something will not work as on Windows DOF, or a reference could not be resolved.</summary>
		Warning,
		/// <summary>The import was rejected.</summary>
		Error,
	}

	public enum DofReportTopic
	{
		General,
		/// <summary>A file was skipped, kept from the previous configuration or replaced.</summary>
		Files,
		/// <summary>A file pattern of the imported global config and what it now points to.</summary>
		Relocation,
		/// <summary>A file a configuration names that is not part of it.</summary>
		Unresolved,
		/// <summary>Configuration libdof does not support and skips.</summary>
		Unsupported,
		/// <summary>How libdof picks ini rows for a ROM.</summary>
		RomMatching,
	}

	public readonly struct DofReportEntry
	{
		public DofReportSeverity Severity { get; }
		public DofReportTopic Topic { get; }
		public string Message { get; }

		public DofReportEntry(DofReportSeverity severity, DofReportTopic topic, string message)
		{
			Severity = severity;
			Topic = topic;
			Message = message;
		}

		public override string ToString() => $"{Severity} ({Topic}): {Message}";
	}

	/// <summary>The result of an import: whether it was accepted, and everything worth telling the user about it.</summary>
	public sealed class DofImportReport
	{
		private readonly List<DofReportEntry> _entries = new List<DofReportEntry>();

		public IReadOnlyList<DofReportEntry> Entries => _entries;

		/// <summary>The revision the import created and made active, or null if it was rejected or could not be published.</summary>
		public string Revision { get; internal set; }

		public bool Accepted => Revision != null;

		/// <summary>The first error, the reason the import was rejected; null if it was accepted.</summary>
		public string RejectionReason
		{
			get {
				foreach (var entry in _entries) {
					if (entry.Severity == DofReportSeverity.Error) {
						return entry.Message;
					}
				}
				return null;
			}
		}

		public bool HasErrors => RejectionReason != null;

		/// <summary>What the import is made from, such as the zip's name; written with the revision.</summary>
		internal string Origin { get; set; }

		internal void Info(DofReportTopic topic, string message) => _entries.Add(new DofReportEntry(DofReportSeverity.Info, topic, message));
		internal void Warning(DofReportTopic topic, string message) => _entries.Add(new DofReportEntry(DofReportSeverity.Warning, topic, message));
		internal void Error(string message) => _entries.Add(new DofReportEntry(DofReportSeverity.Error, DofReportTopic.General, message));

		public override string ToString() => Format(Revision);

		/// <summary>The report as it reads once the revision is published; written into the revision before that.</summary>
		internal string Format(string revision)
		{
			var text = new StringBuilder();
			text.AppendLine(revision != null ? $"Imported as revision {revision}." : $"Rejected: {RejectionReason}");
			foreach (var entry in _entries) {
				text.AppendLine(entry.ToString());
			}
			return text.ToString();
		}
	}
}
