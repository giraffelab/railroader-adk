using System.Collections.Generic;
using System.Linq;

namespace AssetPack.Common.Dependencies
{
    /// <summary>
    /// Result of dependency validation and resolution.
    /// Generic to work with any pack type (AssetPackInfo, PackEntry, etc.)
    /// </summary>
    public class DependencyValidationResult<T>
    {
        public enum ErrorType
        {
            /// <summary>
            /// A required dependency is missing.
            /// </summary>
            MissingDependency,

            /// <summary>
            /// A dependency exists but its version doesn't satisfy constraints.
            /// </summary>
            VersionMismatch,

            /// <summary>
            /// Circular dependency detected.
            /// </summary>
            CircularDependency,

            /// <summary>
            /// An incompatible mod is present.
            /// </summary>
            IncompatibleMod
        }

        public class ValidationError
        {
            public ErrorType Type { get; }
            public string PackIdentifier { get; }
            public string Message { get; }

            public ValidationError(ErrorType type, string packIdentifier, string message)
            {
                Type = type;
                PackIdentifier = packIdentifier;
                Message = message;
            }

            public override string ToString() => $"[{Type}] {PackIdentifier}: {Message}";
        }

        /// <summary>
        /// List of all validation errors encountered.
        /// </summary>
        public IReadOnlyList<ValidationError> Errors { get; }

        /// <summary>
        /// Non-fatal advisories that did not prevent any pack from loading, for example a load-order
        /// hint (<see cref="AssetPackLoadOrder.After"/> / <see cref="AssetPackLoadOrder.Before"/>) that
        /// was dropped because honoring it would have created a load-order cycle. Unlike
        /// <see cref="Errors"/>, warnings never affect <see cref="IsSuccess"/>.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// Sorted list of valid packs (packs that passed validation).
        /// Ordered by dependency depth, then alphabetically.
        /// </summary>
        public IReadOnlyList<T> ValidPacks { get; }

        /// <summary>
        /// Dictionary of all packs that passed validation, keyed by identifier.
        /// </summary>
        public IReadOnlyDictionary<string, T> ValidPacksDict { get; }

        /// <summary>
        /// True if there are no errors and at least one valid pack.
        /// </summary>
        public bool IsSuccess => Errors.Count == 0 && ValidPacks.Count > 0;

        public DependencyValidationResult(
            IReadOnlyList<ValidationError> errors,
            IReadOnlyList<T> validPacks,
            IReadOnlyDictionary<string, T> validPacksDict,
            IReadOnlyList<string> warnings = null)
        {
            Errors = errors;
            ValidPacks = validPacks;
            ValidPacksDict = validPacksDict;
            Warnings = warnings ?? System.Array.Empty<string>();
        }

        /// <summary>
        /// Gets all errors for a specific pack.
        /// </summary>
        public IEnumerable<ValidationError> GetErrorsForPack(string packIdentifier)
        {
            return Errors.Where(e => e.PackIdentifier == packIdentifier);
        }

        /// <summary>
        /// Gets all errors of a specific type.
        /// </summary>
        public IEnumerable<ValidationError> GetErrorsByType(ErrorType type)
        {
            return Errors.Where(e => e.Type == type);
        }
    }
}
