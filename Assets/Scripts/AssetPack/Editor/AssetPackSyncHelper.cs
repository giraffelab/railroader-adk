using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetPack.Common;

namespace AssetPack.Editor
{
    /// <summary>
    /// Compares a pack's data artifacts between ModDev (what the running game loads) and the ADK
    /// working copy, and reports which side is ahead.
    /// </summary>
    public static class AssetPackSyncHelper
    {
        /// <summary>Which copy is ahead. Diverged is a state of its own: a difference with no safe direction.</summary>
        public enum SyncDirection
        {
            Absent,
            InSync,
            ModDevAhead,
            AdkAhead,
            Diverged
        }

        public struct SyncStatus
        {
            public SyncDirection Direction;
            public bool ModDevExists;
            public bool AdkExists;
            public string Message;
            /// <summary>
            /// How many of ModDev's files the ADK simply does not have. Ambiguous by nature --
            /// authored in-game, or deleted in the ADK -- so a caller that wants to advise "Pull"
            /// has to offer it as a possibility rather than state it as a fact.
            /// </summary>
            public int ModDevOnlyFiles;

            public bool HasContent => ModDevExists || AdkExists;
        }

        /// <summary>Everything a pack ships outside its AssetBundle, copied as data files.</summary>
        public enum DataArtifact
        {
            Info,
            Definitions,
            /// <summary>The rest, scripts included; the half the running game writes, so ModDev can be the only copy.</summary>
            Payload
        }

        /// <summary>What a mirror would write and delete. Deletes is what makes the operation destructive.</summary>
        public readonly struct DataMirrorPlan
        {
            public readonly IReadOnlyList<string> Writes;
            public readonly IReadOnlyList<string> Deletes;

            public DataMirrorPlan(IReadOnlyList<string> writes, IReadOnlyList<string> deletes)
            {
                Writes = writes;
                Deletes = deletes;
            }

            public bool IsDestructive => Deletes.Count > 0;
        }

        /// <summary>
        /// The files a one-way copy would replace with an older copy, relative to the pack directory.
        /// Walks the same files the copy walks rather than reading the rolled-up
        /// <see cref="SyncStatus"/> flags, which roll per-artifact and can't answer per-file.
        /// </summary>
        /// <param name="only">Limits the check to one artifact; null checks every artifact.</param>
        public static List<string> FilesReplacedWithOlder(
            string sourceDir, string destDir, DataArtifact? only = null)
        {
            var replaced = new List<string>();

            foreach (var relativePath in ArtifactRelativePaths(sourceDir, only))
            {
                var sourceFile = Path.Combine(sourceDir, relativePath);
                var destFile = Path.Combine(destDir, relativePath);
                // A file the destination doesn't have yet is created, not lost.
                if (!File.Exists(destFile))
                    continue;
                // Same one-second floor the comparisons use: a copy lands with a near-identical stamp,
                // and filesystem granularity varies, so anything closer isn't a real difference. Past
                // that the bytes decide -- rewriting a file without changing it destroys nothing, which
                // is what lets an action flush an editor form before asking what the copy would cost.
                if ((File.GetLastWriteTime(destFile) - File.GetLastWriteTime(sourceFile)).TotalSeconds >= 1
                    && !SameContent(sourceFile, destFile))
                    replaced.Add(Normalize(relativePath));
            }

            return replaced;
        }

        /// <summary>Copies the artifacts one way, overwriting the destination.</summary>
        public static List<string> CopyDataArtifacts(
            string sourceDir, string destDir, DataArtifact? only = null)
        {
            var copied = new List<string>();

            foreach (var relativePath in ArtifactRelativePaths(sourceDir, only))
            {
                var destFile = Path.Combine(destDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile));
                File.Copy(Path.Combine(sourceDir, relativePath), destFile, true);
                copied.Add(Normalize(relativePath));
            }

            return copied;
        }

        /// <summary>Driven by what the source has: a file the source lacks is never copied.</summary>
        private static IEnumerable<string> ArtifactRelativePaths(
            string sourceDir, DataArtifact? only)
        {
            if (only is null or DataArtifact.Info && File.Exists(Path.Combine(sourceDir, Filenames.Info)))
                yield return Filenames.Info;

            if (only is null or DataArtifact.Definitions && File.Exists(Path.Combine(sourceDir, Filenames.Definitions)))
                yield return Filenames.Definitions;

            if (only is null or DataArtifact.Payload)
                foreach (var relativePath in PayloadRelativePaths(sourceDir))
                    yield return relativePath;
        }

        /// <summary>Shared by the mirror, the copy and the comparisons, so all agree on what payload means.</summary>
        public static IEnumerable<string> PayloadRelativePaths(string packDir)
        {
            if (!Directory.Exists(packDir))
                yield break;

            // A directory at a time, not AllDirectories, so a dot directory is pruned rather than
            // walked and discarded -- a cloned pack's .git runs to hundreds of objects.
            var pending = new Stack<string>();
            pending.Push(packDir);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();

                foreach (var subdirectory in Directory.EnumerateDirectories(directory))
                    if (DirectoryUtilities.IgnoreDotFilesAndMeta(Path.GetFileName(subdirectory)))
                        pending.Push(subdirectory);

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var relativePath = Normalize(Path.GetRelativePath(packDir, file));
                    if (!PayloadFilter.Matches(relativePath) || IsOwnedByAnotherArtifact(relativePath))
                        continue;

                    yield return relativePath;
                }
            }
        }

        /// <summary>
        /// Whether two files hold the same bytes. Only asked once the write times already disagree,
        /// so a pack that is in sync is never read: a map pack's Terrain is hundreds of tiles and
        /// this runs on every Refresh.
        /// </summary>
        public static bool SameContent(string pathA, string pathB)
        {
            var a = new FileInfo(pathA);
            var b = new FileInfo(pathB);
            if (!a.Exists || !b.Exists || a.Length != b.Length)
                return false;

            try
            {
                return ContentHash(a) == ContentHash(b);
            }
            catch (IOException)
            {
                // Vanished or locked since the check above. Unreadable counts as different, so the
                // caller reports a difference it cannot rule out rather than a match it cannot prove.
                return false;
            }
        }

        // A build stamps every file it copies, so without this the whole pack rehashes on each
        // refresh that follows one. Keyed on write time as well as length, which assumes an edit
        // moves the stamp -- the same thing the comparisons themselves assume. A tool that restored
        // a byte-changed file to an exact previously seen stamp and length would read as unchanged.
        private static readonly Dictionary<(string Path, long Length, long Ticks), string> ContentHashes =
            new Dictionary<(string, long, long), string>();

        private static string ContentHash(FileInfo file)
        {
            var key = (file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
            if (ContentHashes.TryGetValue(key, out var hash))
                return hash;

            using (var stream = file.OpenRead())
            using (var md5 = MD5.Create())
                hash = Convert.ToBase64String(md5.ComputeHash(stream));

            ContentHashes[key] = hash;
            return hash;
        }

        /// <summary>Matched exactly, so a Definitions.json in a subdirectory is ordinary payload.</summary>
        private static bool IsOwnedByAnotherArtifact(string relativePath) =>
            relativePath.Equals(Filenames.Info, StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals(Filenames.Definitions, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Everything a build would destroy in <paramref name="destDir"/>. ModDev is not just output:
        /// the running game writes maps, tiles and definitions there.
        /// </summary>
        public static List<string> FilesLostToBuild(string sourceDir, string destDir)
        {
            var lost = new List<string>(PlanPayloadMirror(sourceDir, destDir).Deletes);
            lost.AddRange(FilesReplacedWithOlder(sourceDir, destDir));
            return lost;
        }

        /// <summary>What <see cref="MirrorPayload"/> would do, so a caller can show the deletions first.</summary>
        public static DataMirrorPlan PlanPayloadMirror(string sourceDir, string destDir)
        {
            var writes = PayloadRelativePaths(sourceDir).ToList();
            // A set, not a List.Contains scan: a map pack's tile set runs to hundreds of files and the
            // pairing is otherwise quadratic.
            var kept = new HashSet<string>(writes);
            var deletes = PayloadRelativePaths(destDir)
                .Where(relativePath => !kept.Contains(relativePath))
                .ToList();

            return new DataMirrorPlan(writes, deletes);
        }

        /// <summary>
        /// Makes the destination carry the source's payload and nothing else. Deletes run before
        /// writes: on a case-insensitive volume a case-only rename puts the same file in both lists.
        /// </summary>
        public static DataMirrorPlan MirrorPayload(string sourceDir, string destDir)
        {
            // Its own guard: nothing has been touched yet if the walk throws, so the message below
            // ("partially updated") would be wrong here.
            DataMirrorPlan plan;
            try
            {
                plan = PlanPayloadMirror(sourceDir, destDir);
            }
            catch (Exception e)
            {
                throw new IOException(
                    $"Failed reading the payload of '{sourceDir}' or '{destDir}'; " +
                    $"nothing was changed. {e.Message}", e);
            }

            // A copy throwing midway leaves the destination stripped, so name the pack rather than
            // surfacing an unrelated-looking exception.
            try
            {
                foreach (var relativePath in plan.Deletes)
                    File.Delete(Path.Combine(destDir, relativePath));

                foreach (var relativePath in plan.Writes)
                {
                    var destFile = Path.Combine(destDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile));
                    File.Copy(Path.Combine(sourceDir, relativePath), destFile, true);
                }
            }
            catch (Exception e)
            {
                // The caller's logging never runs when this throws, so name the deletions here or
                // there is no record of what went.
                throw new IOException(
                    $"Failed mirroring payload into '{destDir}'; it is partially updated. " +
                    $"Already deleted: {(plan.Deletes.Count == 0 ? "nothing" : string.Join(", ", plan.Deletes))}. " +
                    $"{e.Message}", e);
            }

            return plan;
        }

        private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

        /// <summary>
        /// Compares a single file between the ModDev and ADK copies of a pack.
        /// </summary>
        public static SyncStatus CompareSingleFile(string modDevFile, string adkFile, string displayName)
        {
            var status = new SyncStatus
            {
                ModDevExists = File.Exists(modDevFile),
                AdkExists = File.Exists(adkFile)
            };

            if (!status.HasContent)
            {
                status.Message = $"No {displayName} in ADK or ModDev.";
                return status;
            }

            if (status.ModDevExists && status.AdkExists)
            {
                var modDevTime = File.GetLastWriteTime(modDevFile);
                var adkTime = File.GetLastWriteTime(adkFile);
                var diff = modDevTime - adkTime;
                var diffSeconds = diff.TotalSeconds;

                if (Math.Abs(diffSeconds) < 1 || SameContent(modDevFile, adkFile))
                {
                    status.Message = "Synced";
                    status.Direction = SyncDirection.InSync;
                }
                else if (diffSeconds < 0)
                {
                    status.Message = $"ADK is newer by {FormatTimeSpanCompact(-diff)}";
                    status.Direction = SyncDirection.AdkAhead;
                }
                else
                {
                    status.Message = $"ModDev is newer by {FormatTimeSpanCompact(diff)}";
                    status.Direction = SyncDirection.ModDevAhead;
                }
            }
            else if (status.ModDevExists)
            {
                var modDevTime = File.GetLastWriteTime(modDevFile);
                status.Message = $"ModDev {displayName} saved {modDevTime:g}. No ADK {displayName}.";
                status.Direction = SyncDirection.ModDevAhead;
            }
            else
            {
                var adkTime = File.GetLastWriteTime(adkFile);
                status.Message = $"ADK {displayName} saved {adkTime:g}. No ModDev {displayName}.";
                status.Direction = SyncDirection.AdkAhead;
            }

            return status;
        }

        /// <summary>
        /// A pack's data files compared between ModDev and the ADK, keyed by pack-relative path.
        /// Recursive, which is what stops a row reading "Synced" while a nested file differs.
        /// </summary>
        public static SyncStatus ComparePayload(string modDevPackDir, string adkPackDir)
        {
            Dictionary<string, DateTime> Stamps(string packDir) =>
                PayloadRelativePaths(packDir)
                    .ToDictionary(
                        relativePath => relativePath,
                        relativePath => File.GetLastWriteTime(Path.Combine(packDir, relativePath)));

            return CompareFileSets(
                Stamps(modDevPackDir), Stamps(adkPackDir), modDevPackDir, adkPackDir, "data files");
        }

        private static SyncStatus CompareFileSets(
            Dictionary<string, DateTime> modDevFiles, Dictionary<string, DateTime> adkFiles,
            string modDevDir, string adkDir, string displayName)
        {
            var status = new SyncStatus
            {
                ModDevExists = modDevFiles.Count > 0,
                AdkExists = adkFiles.Count > 0
            };

            if (!status.HasContent)
            {
                status.Message = $"No {displayName} in ADK or ModDev.";
                return status;
            }

            if (status.ModDevExists && status.AdkExists)
            {
                // Compare all files to determine sync status
                var allFiles = modDevFiles.Keys.Union(adkFiles.Keys).ToList();
                var modDevNewer = 0;
                var adkNewer = 0;
                var synced = 0;
                var modDevOnly = 0;

                foreach (var fileName in allFiles)
                {
                    var hasModDev = modDevFiles.TryGetValue(fileName, out var modDevTime);
                    var hasAdk = adkFiles.TryGetValue(fileName, out var adkTime);

                    if (hasModDev && hasAdk)
                    {
                        var diff = modDevTime - adkTime;
                        if (Math.Abs(diff.TotalSeconds) < 1 ||
                            SameContent(Path.Combine(modDevDir, fileName), Path.Combine(adkDir, fileName)))
                            synced++;
                        else if (diff.TotalSeconds > 0)
                            modDevNewer++;
                        else
                            adkNewer++;
                    }
                    else if (hasModDev)
                    {
                        modDevNewer++;
                        // Present on one side only is NOT the same as edited-more-recently: it is
                        // equally a file the other side deleted. The window must not claim to know
                        // which, so this is carried separately rather than folded in.
                        modDevOnly++;
                    }
                    else
                    {
                        adkNewer++;
                    }
                }

                if (modDevNewer == 0 && adkNewer == 0)
                {
                    status.Message = $"Synced ({synced} files)";
                    status.Direction = SyncDirection.InSync;
                }
                else if (modDevNewer > 0 && adkNewer == 0)
                {
                    status.Message = $"ModDev has {modDevNewer} newer/additional files";
                    status.Direction = SyncDirection.ModDevAhead;
                    status.ModDevOnlyFiles = modDevOnly;
                }
                else if (adkNewer > 0 && modDevNewer == 0)
                {
                    status.Message = $"ADK has {adkNewer} newer/additional files";
                    status.Direction = SyncDirection.AdkAhead;
                }
                else
                {
                    status.Message = $"Mixed: {modDevNewer} newer in ModDev, {adkNewer} newer in ADK";
                    // Diverged is exactly where the advice matters most, so the one-sided count has
                    // to survive here too -- not just when ModDev is cleanly ahead.
                    status.ModDevOnlyFiles = modDevOnly;
                    status.Direction = SyncDirection.Diverged;
                }
            }
            else if (status.ModDevExists)
            {
                status.Message = $"ModDev has {modDevFiles.Count} file(s). No ADK {displayName}.";
                status.Direction = SyncDirection.ModDevAhead;
                // The whole side is one-sided, which is the in-game-authored case: a map saved from
                // the editor into a pack the ADK has never had a Data directory for.
                status.ModDevOnlyFiles = modDevFiles.Count;
            }
            else
            {
                status.Message = $"ADK has {adkFiles.Count} file(s). No ModDev {displayName}.";
                status.Direction = SyncDirection.AdkAhead;
            }

            return status;
        }

        /// <summary>
        /// Formats a TimeSpan into a compact string representation.
        /// </summary>
        public static string FormatTimeSpanCompact(TimeSpan timeSpan)
        {
            var absoluteSpan = timeSpan.Duration();
            var parts = new List<string>();

            if (absoluteSpan.Days > 0)
                parts.Add($"{absoluteSpan.Days}d");

            if (absoluteSpan.Hours > 0)
                parts.Add($"{absoluteSpan.Hours}h");

            if (absoluteSpan.Minutes > 0 && absoluteSpan.Days == 0)
                parts.Add($"{absoluteSpan.Minutes}m");

            return parts.Count > 0 ? string.Join(" ", parts) : "< 1m";
        }
    }
}
