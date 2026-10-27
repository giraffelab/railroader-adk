using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetPack.Editor
{
    /// <summary>Which data files a pack ships: matching extensions anywhere under the pack root.</summary>
    public static class PayloadFilter
    {
        /// <summary>Not .md/.txt: pack folders hold author notes that must not reach players.</summary>
        public static readonly IReadOnlyList<string> Extensions =
            new[] { ".json", ".riff", ".rrhorn", ".rrwhistle", ".rl" };

        private static readonly HashSet<string> Lookup =
            new HashSet<string>(Extensions, System.StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a pack-relative path is a data file. Dot files and .meta lose on any segment.</summary>
        public static bool Matches(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return false;

            var segments = relativePath.Split('/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => !DirectoryUtilities.IgnoreDotFilesAndMeta(segment)))
                return false;

            return Lookup.Contains(Path.GetExtension(relativePath));
        }
    }
}
