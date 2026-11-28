// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VisualPinball.Engine.DOF.Configuration
{
	/// <summary>
	/// The imported DOF configurations of the player: a folder of revisions and a pointer to the active one.
	/// </summary>
	/// <remarks>
	/// Layout:
	/// <code>
	/// &lt;root&gt;/revisions/&lt;id&gt;/   one revision each (see <see cref="DofRevision"/>)
	/// &lt;root&gt;/revisions.txt      "active=&lt;id&gt;" and "previous=&lt;id&gt;" (the revision active before, for <see cref="Rollback"/>)
	/// &lt;root&gt;/staging/           imports in progress
	/// </code>
	/// Switching revisions replaces the one pointer file in one step, so a failure or a crash leaves either the old or
	/// the new pair of revisions in place. A game holds a lease on the revision it started with (<see cref="Acquire"/>):
	/// imports and rollbacks apply to the next game, and a leased revision is never deleted, nor is one an import is
	/// still writing or checking.
	/// </remarks>
	public sealed class DofConfigStore
	{
		private const string RevisionsFolder = "revisions";
		private const string StagingFolder = "staging";
		private const string PointerFile = "revisions.txt";

		private readonly object _lock = new object();
		// Held by a prune from listing the folders until it deleted them, so two prunes never work from the same list.
		private readonly object _pruneLock = new object();
		private readonly Dictionary<string, int> _leases = new Dictionary<string, int>(StringComparer.Ordinal);
		// Revisions an import created and has not activated or discarded yet.
		private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);

		public string Root { get; }

		public DofConfigStore(string root)
		{
			if (string.IsNullOrEmpty(root)) {
				throw new ArgumentException("A folder is required.", nameof(root));
			}
			Root = Path.GetFullPath(root);
		}

		internal string RevisionsDir => Path.Combine(Root, RevisionsFolder);
		internal string StagingDir => Path.Combine(Root, StagingFolder);

		/// <summary>The active revision, or null if nothing was imported.</summary>
		public DofRevision Active
		{
			get {
				lock (_lock) {
					return Revision(ReadPointers().active);
				}
			}
		}

		/// <summary>The revision <see cref="Rollback"/> returns to, or null.</summary>
		public DofRevision Previous
		{
			get {
				lock (_lock) {
					return Revision(ReadPointers().previous);
				}
			}
		}

		/// <summary>
		/// Takes a lease on the active revision for a game. Dispose it when the game's DOF session has finished.
		/// </summary>
		/// <returns>The lease, or null if nothing was imported.</returns>
		public DofRevisionLease Acquire() => Acquire(true);

		/// <summary>
		/// Takes a lease on the active revision while it is read, such as by the settings page, so a prune does not delete
		/// it meanwhile. Unlike a game's lease, releasing it deletes nothing: a read must not prune, since another Player
		/// sharing the folder may be building a revision this one does not know about.
		/// </summary>
		/// <returns>The lease, or null if nothing was imported.</returns>
		public DofRevisionLease AcquireForReading() => Acquire(false);

		private DofRevisionLease Acquire(bool prune)
		{
			lock (_lock) {
				var revision = Revision(ReadPointers().active);
				if (revision == null) {
					return null;
				}
				_leases.TryGetValue(revision.Id, out var count);
				_leases[revision.Id] = count + 1;
				return new DofRevisionLease(this, revision, prune);
			}
		}

		internal void Release(DofRevision revision, bool prune)
		{
			lock (_lock) {
				if (_leases.TryGetValue(revision.Id, out var count)) {
					if (count <= 1) {
						_leases.Remove(revision.Id);
					} else {
						_leases[revision.Id] = count - 1;
					}
				}
			}
			if (prune) {
				Prune();
			}
		}

		/// <summary>Makes the previous revision active again. The current one becomes the previous one.</summary>
		/// <returns>False if there is no previous revision.</returns>
		public bool Rollback()
		{
			lock (_lock) {
				var (active, previous) = ReadPointers();
				if (previous == null || Revision(previous) == null) {
					return false;
				}
				WritePointers(previous, active);
				return true;
			}
		}

		/// <summary>
		/// Creates an empty revision folder for an import, which no prune deletes until it is activated
		/// (<see cref="Activate"/>) or discarded (<see cref="Discard"/>).
		/// </summary>
		internal DofRevision CreateRevision()
		{
			lock (_lock) {
				Directory.CreateDirectory(RevisionsDir);
				var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				while (true) {
					// Never the name of a revision deleted before: a prune that listed that one must not take this one.
					var id = $"{stamp}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
					var path = Path.Combine(RevisionsDir, id);
					if (!Directory.Exists(path) && !_pending.Contains(id)) {
						Directory.CreateDirectory(path);
						_pending.Add(id);
						return new DofRevision(id, path);
					}
				}
			}
		}

		/// <summary>
		/// Makes a revision active; the active one becomes the previous one, older ones are deleted unless leased. Both
		/// change in one step: if it throws, nothing changed.
		/// </summary>
		internal void Activate(DofRevision revision)
		{
			lock (_lock) {
				var (active, previous) = ReadPointers();
				WritePointers(revision.Id, active != null && active != revision.Id ? active : previous);
				_pending.Remove(revision.Id);
			}
			Prune();
		}

		/// <summary>Deletes a revision an import created and did not activate.</summary>
		internal void Discard(DofRevision revision)
		{
			lock (_lock) {
				var (active, previous) = ReadPointers();
				if (revision.Id == active || revision.Id == previous) {
					// It was published after all: deleting it would leave the pointer on a missing folder.
					_pending.Remove(revision.Id);
					return;
				}
				TryDelete(revision.BasePath);
				_pending.Remove(revision.Id);
			}
		}

		internal string CreateStaging()
		{
			var path = Path.Combine(StagingDir, Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(path);
			return path;
		}

		/// <summary>Deletes revisions that are neither active, previous nor leased, and staging folders left by a crash.</summary>
		public void Prune()
		{
			lock (_pruneLock) {
				List<string> remove;
				lock (_lock) {
					if (!Directory.Exists(RevisionsDir)) {
						return;
					}
					var keep = new HashSet<string>(_leases.Keys, StringComparer.Ordinal);
					keep.UnionWith(_pending);
					var (active, previous) = ReadPointers();
					if (active != null) {
						keep.Add(active);
					}
					if (previous != null) {
						keep.Add(previous);
					}
					remove = Directory.GetDirectories(RevisionsDir).Where(d => !keep.Contains(Path.GetFileName(d))).ToList();
				}
				// Outside the store's lock, so leases and imports do not wait for the deletion. A revision created
				// meanwhile has a name no listed folder had, and a listed one never becomes active or previous again.
				foreach (var dir in remove) {
					TryDelete(dir);
				}
			}
		}

		/// <summary>Deletes staging folders. Call when no import runs, such as at startup.</summary>
		public void CleanStaging()
		{
			if (Directory.Exists(StagingDir)) {
				foreach (var dir in Directory.GetDirectories(StagingDir)) {
					TryDelete(dir);
				}
			}
		}

		private DofRevision Revision(string id)
		{
			if (id == null) {
				return null;
			}
			var path = Path.Combine(RevisionsDir, id);
			return Directory.Exists(Path.Combine(path, DofRevision.ConfigFolder)) ? new DofRevision(id, path) : null;
		}

		private (string active, string previous) ReadPointers()
		{
			var path = Path.Combine(Root, PointerFile);
			if (!File.Exists(path)) {
				return (null, null);
			}
			string active = null, previous = null;
			foreach (var line in File.ReadAllLines(path)) {
				var separator = line.IndexOf('=');
				if (separator <= 0) {
					continue;
				}
				var id = Id(line.Substring(separator + 1));
				switch (line.Substring(0, separator).Trim()) {
					case "active": active = id; break;
					case "previous": previous = id; break;
				}
			}
			return (active, previous);

			// An id is a folder name the store created; anything else is not followed.
			string Id(string value)
			{
				var id = value.Trim();
				return id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..") ? null : id;
			}
		}

		/// <summary>Replaces the pointer file in one step.</summary>
		private void WritePointers(string active, string previous)
		{
			Directory.CreateDirectory(Root);
			var path = Path.Combine(Root, PointerFile);
			var temp = path + ".tmp";
			File.WriteAllText(temp, $"active={active}\nprevious={previous}\n");
			if (File.Exists(path)) {
				File.Replace(temp, path, null);
			} else {
				File.Move(temp, path);
			}
		}

		internal static void TryDelete(string dir)
		{
			try {
				if (Directory.Exists(dir)) {
					Directory.Delete(dir, true);
				}
			} catch (IOException) {
				// In use or locked: the next prune tries again.
			} catch (UnauthorizedAccessException) {
			}
		}
	}

	/// <summary>A game's hold on a revision; the revision is not deleted while a lease exists.</summary>
	public sealed class DofRevisionLease : IDisposable
	{
		private DofConfigStore _store;
		private readonly bool _prune;

		public DofRevision Revision { get; }

		internal DofRevisionLease(DofConfigStore store, DofRevision revision, bool prune)
		{
			_store = store;
			Revision = revision;
			_prune = prune;
		}

		public void Dispose()
		{
			var store = System.Threading.Interlocked.Exchange(ref _store, null);
			store?.Release(Revision, _prune);
		}
	}
}
