using System;

namespace AssetPack.Common.Versioning
{
    /// <summary>
    /// Represents a version constraint for dependency resolution.
    /// Supports operators: &lt;, &gt;, &lt;=, &gt;=, == (or implicit equality), ~ (tilde), and ^ (caret).
    /// A tilde constraint like "~1.2.3" pins major and minor and allows the patch to float
    /// upward, equivalent to "&gt;=1.2.3 &lt;1.3.0". A caret constraint like "^1.2.3" pins
    /// only the major and allows minor and patch to float, equivalent to "&gt;=1.2.3 &lt;2.0.0".
    /// Below 1.0 a minor release may break compatibility, so a caret on a 0.x version pins the minor
    /// too: "^0.6.2" is "&gt;=0.6.2 &lt;0.7.0".
    /// </summary>
    public readonly struct VersionConstraint
    {
        public enum Operator
        {
            Equal,
            LessThan,
            LessThanOrEqual,
            GreaterThan,
            GreaterThanOrEqual,
            Tilde,
            Caret
        }

        public Operator Op { get; }
        public PackVersion Version { get; }

        public VersionConstraint(Operator op, PackVersion version)
        {
            Op = op;
            Version = version;
        }

        /// <summary>
        /// Parses a version constraint string like ">=2025.2.0" or "2025.2.0" (implicit ==).
        /// </summary>
        public static VersionConstraint Parse(string constraintString)
        {
            if (string.IsNullOrWhiteSpace(constraintString))
                throw new ArgumentException("Constraint string cannot be null or empty", nameof(constraintString));

            constraintString = constraintString.Trim();

            // Check for two-character operators first
            if (constraintString.StartsWith(">="))
            {
                var versionString = constraintString.Substring(2).Trim();
                return new VersionConstraint(Operator.GreaterThanOrEqual, new PackVersion(versionString));
            }
            if (constraintString.StartsWith("<="))
            {
                var versionString = constraintString.Substring(2).Trim();
                return new VersionConstraint(Operator.LessThanOrEqual, new PackVersion(versionString));
            }
            if (constraintString.StartsWith("=="))
            {
                var versionString = constraintString.Substring(2).Trim();
                return new VersionConstraint(Operator.Equal, new PackVersion(versionString));
            }

            // Check for single-character operators
            if (constraintString.StartsWith(">"))
            {
                var versionString = constraintString.Substring(1).Trim();
                return new VersionConstraint(Operator.GreaterThan, new PackVersion(versionString));
            }
            if (constraintString.StartsWith("<"))
            {
                var versionString = constraintString.Substring(1).Trim();
                return new VersionConstraint(Operator.LessThan, new PackVersion(versionString));
            }
            if (constraintString.StartsWith("~"))
            {
                var versionString = constraintString.Substring(1).Trim();
                return new VersionConstraint(Operator.Tilde, new PackVersion(versionString));
            }
            if (constraintString.StartsWith("^"))
            {
                var versionString = constraintString.Substring(1).Trim();
                return new VersionConstraint(Operator.Caret, new PackVersion(versionString));
            }

            // No operator means implicit equality
            return new VersionConstraint(Operator.Equal, new PackVersion(constraintString));
        }

        /// <summary>
        /// Checks if the given version satisfies this constraint.
        /// </summary>
        public bool IsSatisfiedBy(PackVersion version)
        {
            return Op switch
            {
                Operator.Equal => version == Version,
                Operator.LessThan => version < Version,
                Operator.LessThanOrEqual => version <= Version,
                Operator.GreaterThan => version > Version,
                Operator.GreaterThanOrEqual => version >= Version,
                Operator.Tilde => version >= Version && version < Version.NextMinor(),
                Operator.Caret => version >= Version && version < (Version.Major == 0 ? Version.NextMinor() : Version.NextMajor()),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public override string ToString()
        {
            var opString = Op switch
            {
                Operator.Equal => "==",
                Operator.LessThan => "<",
                Operator.LessThanOrEqual => "<=",
                Operator.GreaterThan => ">",
                Operator.GreaterThanOrEqual => ">=",
                Operator.Tilde => "~",
                Operator.Caret => "^",
                _ => "?"
            };
            return $"{opString}{Version}";
        }
    }
}
