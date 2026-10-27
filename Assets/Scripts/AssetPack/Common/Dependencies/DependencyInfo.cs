using System;
using System.Collections.Generic;
using System.Linq;
using AssetPack.Common.Versioning;

namespace AssetPack.Common.Dependencies
{
    /// <summary>
    /// Represents a parsed dependency with version constraints.
    /// Format: "domain/identifier [constraint1] [constraint2] ..."
    /// Example: "rr/core >=2025.2.0 <2025.3.0"
    /// </summary>
    public class DependencyInfo
    {
        public enum DependencyType
        {
            Required,
            Optional,
            Incompatible
        }

        public string Identifier { get; }
        public DependencyType Type { get; }
        public IReadOnlyList<VersionConstraint> Constraints { get; }

        public DependencyInfo(string identifier, DependencyType type, IReadOnlyList<VersionConstraint> constraints)
        {
            Identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
            Type = type;
            Constraints = constraints ?? throw new ArgumentNullException(nameof(constraints));
        }

        /// <summary>
        /// Parses a dependency string like "rr/core >=2025.2.0 <2025.3.0" into DependencyInfo.
        /// </summary>
        public static DependencyInfo Parse(string dependencyString, DependencyType type)
        {
            if (string.IsNullOrWhiteSpace(dependencyString))
                throw new ArgumentException("Dependency string cannot be null or empty", nameof(dependencyString));

            var parts = dependencyString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw new ArgumentException("Dependency string cannot be empty", nameof(dependencyString));

            var identifier = parts[0];
            var constraints = new List<VersionConstraint>();

            // Parse version constraints (if any)
            for (int i = 1; i < parts.Length; i++)
            {
                try
                {
                    constraints.Add(VersionConstraint.Parse(parts[i]));
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Failed to parse version constraint '{parts[i]}' in dependency '{dependencyString}'", ex);
                }
            }

            return new DependencyInfo(identifier, type, constraints);
        }

        /// <summary>
        /// Checks if the given version satisfies all constraints.
        /// If no constraints are specified, any version is acceptable.
        /// </summary>
        public bool IsSatisfiedBy(PackVersion version)
        {
            if (Constraints.Count == 0)
                return true;

            return Constraints.All(constraint => constraint.IsSatisfiedBy(version));
        }

        public override string ToString()
        {
            var typeStr = Type switch
            {
                DependencyType.Required => "required",
                DependencyType.Optional => "optional",
                DependencyType.Incompatible => "incompatible",
                _ => "unknown"
            };

            if (Constraints.Count == 0)
                return $"{Identifier} ({typeStr})";

            var constraintsStr = string.Join(" ", Constraints.Select(c => c.ToString()));
            return $"{Identifier} {constraintsStr} ({typeStr})";
        }
    }
}
