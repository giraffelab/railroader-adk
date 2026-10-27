using System;
using System.Collections.Generic;
using System.Linq;
using AssetPack.Common.Versioning;
using UnityEngine;

namespace AssetPack.Common.Dependencies
{
    /// <summary>
    /// Helper for handling virtual game dependencies in dependency validation.
    /// Virtual dependencies represent the game itself as a dependency target but have no actual stores.
    /// </summary>
    public static class GamePackDependencyHelper
    {
        public const string PackIdentifierGame = "rr/game";
        public const string PackIdentifierShared = "rr/shared";

        /// <summary>
        /// Creates the virtual dependency entries representing the game itself.
        /// These are valid dependency targets but have no actual stores.
        /// Currently only rr/game is virtual - rr/shared is a real pack with assets.
        /// </summary>
        public static Dictionary<string, VirtualDependency> CreateVirtualDependencies()
        {
            return new Dictionary<string, VirtualDependency>
            {
                [PackIdentifierGame] = new VirtualDependency(
                    PackIdentifierGame,
                    Application.version,
                    // rr/game depends on rr/shared, so mods only need to reference rr/game
                    new AssetPackDependencies
                    {
                        Required = new List<string> { PackIdentifierShared }
                    }
                )
            };
        }

        /// <summary>
        /// The rr/game requirement a new pack's Info.json starts with: the start of the running game's minor
        /// series (e.g. "rr/game >=0.6.0" on 0.6.3), or bare "rr/game" for first-party rr/ packs.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="gameVersion"/> is not a valid version.</exception>
        public static string DefaultGameRequirement(string packIdentifier, string gameVersion)
        {
            // First-party packs ship with the build they target, so a version constraint on them can never fail.
            if (Utilities.TryParseDomainAsset(packIdentifier, out var domain, out _) && domain == "rr")
                return PackIdentifierGame;

            if (!PackVersion.TryParse(gameVersion, out var version))
                throw new ArgumentException(
                    $"Game version '{gameVersion}' is not a valid version, so the new pack's rr/game requirement can't be set.",
                    nameof(gameVersion));

            return $"{PackIdentifierGame} >={version.MinorStart()}";
        }

        /// <summary>
        /// Validates and sorts packs, automatically including game virtual dependencies.
        /// Use this for Railroader-specific contexts (runtime, editor builds).
        /// </summary>
        public static DependencyValidationResult<IDependencyNode> ValidateAndSort(
            IReadOnlyDictionary<string, AssetPackInfo> packs)
        {
            return DependencySorter.ValidateAndSortPacks(NodesWithVirtualDependencies(packs), ImplicitRequirementsFor);
        }

        /// <summary>
        /// The identifiers of <paramref name="roots"/> and every pack they require, directly or transitively.
        /// </summary>
        public static HashSet<string> CollectRequired(IEnumerable<string> roots,
            IReadOnlyDictionary<string, AssetPackInfo> packs)
        {
            var nodes = NodesWithVirtualDependencies(packs);
            var result = new HashSet<string>();
            foreach (var root in roots)
                CollectRequired(root, nodes, result);
            return result;
        }

        private static void CollectRequired(string identifier, Dictionary<string, IDependencyNode> nodes,
            HashSet<string> result)
        {
            if (!result.Add(identifier)) return;
            if (!nodes.TryGetValue(identifier, out var node)) return;
            var required = node.Dependencies?.Required ?? Enumerable.Empty<string>();
            foreach (var dep in required.Concat(ImplicitRequirementsFor(node)))
            {
                var depInfo = DependencyInfo.Parse(dep, DependencyInfo.DependencyType.Required);
                CollectRequired(depInfo.Identifier, nodes, result);
            }
        }

        private static Dictionary<string, IDependencyNode> NodesWithVirtualDependencies(
            IReadOnlyDictionary<string, AssetPackInfo> packs)
        {
            var allNodes = new Dictionary<string, IDependencyNode>();

            foreach (var kvp in packs)
            {
                allNodes[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in CreateVirtualDependencies())
            {
                allNodes[kvp.Key] = kvp.Value;
            }

            return allNodes;
        }

        /// <summary>
        /// rr/shared is foundational content that packs may consume whether or not they declare it, so every
        /// pack must load after it. Rather than special-case the sorted result, treat every pack as implicitly
        /// requiring <c>rr/game</c> (which in turn requires <c>rr/shared</c>), the convention modders already
        /// follow, so the precedence falls out of the dependency graph. A pack that already references
        /// <c>rr/game</c> or <c>rr/shared</c> keeps its own (possibly version-constrained) declaration. Only
        /// <c>rr/game</c> and <c>rr/shared</c> themselves are excluded, so <c>rr/game → rr/shared → rr/game</c>
        /// never forms; every other pack (including other core <c>rr/*</c> packs like <c>rr/loads</c>) gets
        /// the edge and loads after the base content (they do not sort ahead of rr/shared alphabetically).
        /// </summary>
        private static IEnumerable<string> ImplicitRequirementsFor(IDependencyNode node)
        {
            var identifier = node.Identifier;
            if (string.IsNullOrEmpty(identifier))
                yield break;

            // Excluding these two by identifier (not the whole rr domain) is what keeps other rr/* core packs
            // ordered after rr/shared instead of falling back to the alphabetical tiebreak.
            if (identifier == PackIdentifierGame || identifier == PackIdentifierShared)
                yield break;

            if (ReferencesGameOrShared(node.Dependencies))
                yield break;

            yield return PackIdentifierGame;
        }

        private static bool ReferencesGameOrShared(AssetPackDependencies dependencies)
        {
            if (dependencies == null)
                return false;

            return MentionsGameOrShared(dependencies.Required) || MentionsGameOrShared(dependencies.Optional);
        }

        private static bool MentionsGameOrShared(List<string> dependencyStrings)
        {
            if (dependencyStrings == null)
                return false;

            foreach (var dependencyString in dependencyStrings)
            {
                if (string.IsNullOrWhiteSpace(dependencyString))
                    continue;

                var identifier = dependencyString.Split(' ')[0];
                if (identifier == PackIdentifierGame || identifier == PackIdentifierShared)
                    return true;
            }

            return false;
        }
    }
}
