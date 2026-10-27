using System;
using System.Text.RegularExpressions;

namespace AssetPack.Common.Versioning
{
    /// <summary>
    /// Represents a semantic version number in the format "major.minor.patch[a-z]" for comparison purposes.
    /// Examples: "2024.1.0", "2024.1.2a", "2025.3.0b"
    /// </summary>
    public readonly struct PackVersion
    {
        public static readonly PackVersion Zero = new("", 0);

        public bool IsZero => _intVersion == 0;

        public int Major => _intVersion / 1000000;

        public readonly string Version;
        private readonly int _intVersion;

        public PackVersion(string version)
        {
            Version = version;
            if (!TryParse(version, out _intVersion))
                throw new Exception($"Failed to parse version: {version}");
        }

        private PackVersion(string version, int intVersion)
        {
            Version = version;
            _intVersion = intVersion;
        }

        public override string ToString() => Version;

        public static PackVersion FromStringOrZero(string versionString)
        {
            try
            {
                return new PackVersion(versionString);
            }
            catch
            {
                return Zero;
            }
        }

        #region Parsing

        /// <summary>
        /// Human-readable statement of the accepted format, for validation messages.
        /// </summary>
        public const string FormatDescription =
            "major.minor.patch, with an optional single lowercase letter (e.g. '2025.1.0' or '2025.1.0a'). " +
            "Minor and patch are each below 100.";

        // Anchored on purpose. An unanchored match reads "v2025.2.3-wip" as 2025.2.3 and orders the
        // pack by a number its author never wrote. The version is a comparison key, so a string it
        // can't represent exactly has to be rejected, not approximated.
        private static Regex _versionRegex;

        /// <summary>
        /// Parses a version string, returning false rather than throwing on anything malformed.
        /// </summary>
        public static bool TryParse(string versionString, out PackVersion version)
        {
            if (TryParse(versionString, out int intVersion))
            {
                version = new PackVersion(versionString, intVersion);
                return true;
            }

            version = Zero;
            return false;
        }

        private static bool TryParse(string versionString, out int intVersion)
        {
            intVersion = 0;

            if (string.IsNullOrEmpty(versionString))
                return false;

            _versionRegex ??= new Regex(@"^(\d+)\.(\d+)\.(\d+)([a-z]?)$");
            var match = _versionRegex.Match(versionString);
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups[1].Value, out var major) ||
                !int.TryParse(match.Groups[2].Value, out var minor) ||
                !int.TryParse(match.Groups[3].Value, out var patch))
                return false;

            // The comparison key packs the parts into a single int, which only holds if each part
            // stays inside its column. "1.150.0" would otherwise carry into the major and sort as a
            // later release than it is.
            if (major > MaxMajor || minor > MaxPart || patch > MaxPart)
                return false;

            var build = 0;
            if (!string.IsNullOrEmpty(match.Groups[4].Value))
                build = match.Groups[4].Value[0] - 'a' + 1;

            intVersion = major * 1000000 + minor * 10000 + patch * 100 + build;
            return true;
        }

        private const int MaxPart = 99;
        private const int MaxMajor = 2146; // Keeps major * 1000000 inside int.

        #endregion

        #region Helpers

        /// <summary>
        /// Returns the first release of this minor series (major.minor.0). E.g. "2025.2.3".MinorStart() == "2025.2.0".
        /// </summary>
        public PackVersion MinorStart()
        {
            var major = _intVersion / 1000000;
            var minor = _intVersion / 10000 % 100;
            var startInt = major * 1000000 + minor * 10000;
            return new PackVersion($"{major}.{minor}.0", startInt);
        }

        /// <summary>
        /// Returns the start of the next minor version (major.minor+1.0), used as the exclusive
        /// upper bound for a tilde (~) constraint. E.g. "2025.2.3".NextMinor() == "2025.3.0".
        /// </summary>
        public PackVersion NextMinor()
        {
            var major = _intVersion / 1000000;
            var minor = _intVersion / 10000 % 100;
            var nextMinor = minor + 1;
            var nextMajor = major;
            if (nextMinor >= 100)
            {
                nextMajor += 1;
                nextMinor = 0;
            }

            var nextInt = nextMajor * 1000000 + nextMinor * 10000;
            return new PackVersion($"{nextMajor}.{nextMinor}.0", nextInt);
        }

        /// <summary>
        /// Returns the start of the next major version (major+1.0.0), used as the exclusive
        /// upper bound for a caret (^) constraint. E.g. "2025.2.3".NextMajor() == "2026.0.0".
        /// </summary>
        public PackVersion NextMajor()
        {
            var major = _intVersion / 1000000;
            var nextMajor = major + 1;
            var nextInt = nextMajor * 1000000;
            return new PackVersion($"{nextMajor}.0.0", nextInt);
        }

        #endregion

        #region Operators

        public static bool operator <(PackVersion a, PackVersion b) => a._intVersion < b._intVersion;
        public static bool operator >(PackVersion a, PackVersion b) => a._intVersion > b._intVersion;
        public static bool operator <=(PackVersion a, PackVersion b) => a._intVersion <= b._intVersion;
        public static bool operator >=(PackVersion a, PackVersion b) => a._intVersion >= b._intVersion;
        public static bool operator ==(PackVersion a, PackVersion b) => a.Equals(b);
        public static bool operator !=(PackVersion a, PackVersion b) => !a.Equals(b);

        #endregion

        #region Equality

        public bool Equals(PackVersion other)
        {
            return _intVersion == other._intVersion;
        }

        public override bool Equals(object obj)
        {
            return obj is PackVersion other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _intVersion;
        }

        #endregion
    }
}
