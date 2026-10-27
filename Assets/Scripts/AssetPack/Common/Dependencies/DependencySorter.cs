using System;
using System.Collections.Generic;
using System.Linq;
using AssetPack.Common.Versioning;

namespace AssetPack.Common.Dependencies
{
    public static class DependencySorter
    {
        /// <summary>
        /// Validates dependencies and returns sorted packs with detailed error information.
        /// This method does not throw on validation errors, instead collecting them in the result.
        /// </summary>
        /// <param name="packs">The packs to validate and order, keyed by identifier.</param>
        /// <param name="implicitRequirements">
        /// Optional hook supplying additional required-dependency identifiers per node, applied as if the
        /// node had listed them in <see cref="AssetPackDependencies.Required"/> (no version constraint).
        /// Used to inject Railroader's "every pack implicitly requires <c>rr/game</c>" rule from
        /// <see cref="GamePackDependencyHelper"/> without special-casing it here. Returning identifiers not
        /// present in <paramref name="packs"/> disables the node exactly like a missing required dependency.
        /// </param>
        public static DependencyValidationResult<IDependencyNode> ValidateAndSortPacks(
            IReadOnlyDictionary<string, IDependencyNode> packs,
            Func<IDependencyNode, IEnumerable<string>> implicitRequirements = null)
        {
            /* Asset packs are loaded in a topological order of their dependency and load-order edges: a pack
             * loads after every pack it requires, after its present optional dependencies, and after/before the
             * packs named in its load-order hints. Among packs that no edge orders, ties break by load priority
             * (lower first) then by identifier. Requirement edges are hard: a missing required dependency, a
             * version mismatch, an incompatibility, or a requirement cycle removes the offending pack. Load-order
             * hints are soft: a hint naming an absent pack is ignored, and a hint that would close a cycle is
             * dropped with a warning rather than failing the load.
             */

            var errors = new List<DependencyValidationResult<IDependencyNode>.ValidationError>();
            var warnings = new List<string>();
            var validPacks = new Dictionary<string, IDependencyNode>(packs);

            // Phase 1: Parse all dependencies (plus any implicit requirements) into DependencyInfo per pack.
            var parsedDependencies = new Dictionary<string, List<DependencyInfo>>();
            foreach (var (identifier, pack) in packs)
            {
                var deps = new List<DependencyInfo>();

                ParseInto(deps, pack.Dependencies?.Required, DependencyInfo.DependencyType.Required, identifier, errors);
                ParseInto(deps, pack.Dependencies?.Optional, DependencyInfo.DependencyType.Optional, identifier, errors);
                ParseInto(deps, pack.Dependencies?.Incompatible, DependencyInfo.DependencyType.Incompatible, identifier, errors);

                if (implicitRequirements != null)
                {
                    foreach (var implicitId in implicitRequirements(pack))
                    {
                        // Injected as a constraint-free required dependency: it must be present (it always is,
                        // for rr/game) and it contributes an ordering edge, but never a version check.
                        deps.Add(new DependencyInfo(implicitId, DependencyInfo.DependencyType.Required,
                            Array.Empty<VersionConstraint>()));
                    }
                }

                parsedDependencies[identifier] = deps;
            }

            // Phase 2: Remove packs that declare an incompatibility with a present pack (version-constrained
            // incompatibilities only fire when the present pack's version matches the constraint).
            foreach (var (identifier, deps) in parsedDependencies)
            {
                foreach (var dep in deps.Where(d => d.Type == DependencyInfo.DependencyType.Incompatible))
                {
                    if (packs.TryGetValue(dep.Identifier, out var incompatiblePack))
                    {
                        var version = GetPackVersion(incompatiblePack);
                        // A constraint-free incompatibility fires whenever the pack is present; a version-
                        // constrained one fires when the present version matches, or when that version is
                        // unparseable (assume-matched, so the conflict is surfaced rather than silently
                        // dropped). This mirrors ComputeModSelection's runtime incompatibility handling.
                        if (dep.Constraints.Count == 0 || version.IsZero || dep.IsSatisfiedBy(version))
                        {
                            errors.Add(new DependencyValidationResult<IDependencyNode>.ValidationError(
                                DependencyValidationResult<IDependencyNode>.ErrorType.IncompatibleMod,
                                identifier,
                                $"Pack '{identifier}' is incompatible with '{dep.Identifier}'"));
                            validPacks.Remove(identifier);
                            break;
                        }
                    }
                }
            }

            // Phase 3: Validate required dependencies and version constraints; remove packs that fail.
            var packsToRemove = new HashSet<string>();
            foreach (var (identifier, deps) in parsedDependencies)
            {
                if (!validPacks.ContainsKey(identifier))
                    continue; // Already removed due to incompatibility

                foreach (var dep in deps.Where(d => d.Type == DependencyInfo.DependencyType.Required))
                {
                    if (!packs.TryGetValue(dep.Identifier, out var depPack))
                    {
                        errors.Add(new DependencyValidationResult<IDependencyNode>.ValidationError(
                            DependencyValidationResult<IDependencyNode>.ErrorType.MissingDependency,
                            identifier,
                            $"Required dependency '{dep.Identifier}' is missing"));
                        packsToRemove.Add(identifier);
                        continue;
                    }

                    if (dep.Constraints.Count > 0)
                    {
                        var depVersion = GetPackVersion(depPack);
                        if (!depVersion.IsZero && !dep.IsSatisfiedBy(depVersion))
                        {
                            errors.Add(new DependencyValidationResult<IDependencyNode>.ValidationError(
                                DependencyValidationResult<IDependencyNode>.ErrorType.VersionMismatch,
                                identifier,
                                $"Dependency '{dep.Identifier}' version {depVersion} does not satisfy constraint {dep}"));
                            packsToRemove.Add(identifier);
                        }
                    }
                }
            }

            foreach (var packId in packsToRemove)
                validPacks.Remove(packId);

            // Phase 3b: Cascade. A pack whose required dependency was itself removed above (missing, wrong
            // version, or incompatible) can no longer satisfy that requirement, so it must be removed too.
            // The checks above consult the original `packs` set and so only catch the first failure in a
            // chain; iterate to a fixpoint against `validPacks` to propagate removals upstream.
            bool removedThisPass;
            do
            {
                removedThisPass = false;
                foreach (var (identifier, deps) in parsedDependencies)
                {
                    if (!validPacks.ContainsKey(identifier))
                        continue;

                    foreach (var dep in deps.Where(d => d.Type == DependencyInfo.DependencyType.Required))
                    {
                        if (!validPacks.ContainsKey(dep.Identifier))
                        {
                            errors.Add(new DependencyValidationResult<IDependencyNode>.ValidationError(
                                DependencyValidationResult<IDependencyNode>.ErrorType.MissingDependency,
                                identifier,
                                $"Required dependency '{dep.Identifier}' is not available (it was itself removed or is missing)"));
                            validPacks.Remove(identifier);
                            removedThisPass = true;
                            break;
                        }
                    }
                }
            } while (removedThisPass);

            // Phase 4: Build the hard-edge graph (requirement + present-optional edges) over the surviving
            // packs, and remove any pack caught in a requirement cycle.
            var successors = new Dictionary<string, HashSet<string>>();
            var indegree = new Dictionary<string, int>();
            foreach (var id in validPacks.Keys)
            {
                successors[id] = new HashSet<string>();
                indegree[id] = 0;
            }

            // Local helper: add "before" must-load-before "after"; returns false if it would be a duplicate.
            bool AddEdge(string before, string after)
            {
                if (before == after || !validPacks.ContainsKey(before) || !validPacks.ContainsKey(after))
                    return false;
                if (!successors[before].Add(after))
                    return false;
                indegree[after]++;
                return true;
            }

            foreach (var (identifier, deps) in parsedDependencies)
            {
                if (!validPacks.ContainsKey(identifier))
                    continue;

                foreach (var dep in deps.Where(d =>
                    d.Type == DependencyInfo.DependencyType.Required ||
                    d.Type == DependencyInfo.DependencyType.Optional))
                {
                    // dep loads before identifier.
                    AddEdge(dep.Identifier, identifier);
                }
            }

            var cyclic = FindNodesInCycles(validPacks.Keys, successors, indegree);
            foreach (var packId in cyclic)
            {
                errors.Add(new DependencyValidationResult<IDependencyNode>.ValidationError(
                    DependencyValidationResult<IDependencyNode>.ErrorType.CircularDependency,
                    packId,
                    $"Circular dependency detected in asset pack dependencies involving: {packId}"));
                RemoveNode(packId, successors, indegree);
                validPacks.Remove(packId);
            }

            // Phase 5: Layer in soft load-order hints (after/before), skipping any that would create a cycle.
            foreach (var (identifier, node) in validPacks)
            {
                var load = node.Load;
                if (load == null)
                    continue;

                if (load.After != null)
                    foreach (var other in load.After)
                        TryAddSoftEdge(other, identifier, before: other, after: identifier,
                            validPacks, successors, indegree, warnings, AddEdge);

                if (load.Before != null)
                    foreach (var other in load.Before)
                        TryAddSoftEdge(other, identifier, before: identifier, after: other,
                            validPacks, successors, indegree, warnings, AddEdge);
            }

            // Phase 6: Kahn's algorithm. The ready set (indegree 0) is ordered by (priority, identifier) so the
            // output is deterministic and honors load priority among otherwise-unconstrained packs.
            int PriorityOf(string id) => validPacks.TryGetValue(id, out var n) ? (n.Load?.Priority ?? 0) : 0;
            var ready = new SortedSet<string>(Comparer<string>.Create((a, b) =>
            {
                var pa = PriorityOf(a);
                var pb = PriorityOf(b);
                return pa != pb ? pa.CompareTo(pb) : string.CompareOrdinal(a, b);
            }));
            foreach (var id in validPacks.Keys)
                if (indegree[id] == 0)
                    ready.Add(id);

            var sortedPacks = new List<IDependencyNode>(validPacks.Count);
            while (ready.Count > 0)
            {
                var id = ready.Min;
                ready.Remove(id);
                sortedPacks.Add(validPacks[id]);

                foreach (var next in successors[id])
                {
                    if (--indegree[next] == 0)
                        ready.Add(next);
                }
            }

            return new DependencyValidationResult<IDependencyNode>(errors, sortedPacks, validPacks, warnings);
        }

        private static void ParseInto(
            List<DependencyInfo> deps,
            IEnumerable<string> depStrings,
            DependencyInfo.DependencyType type,
            string identifier,
            List<DependencyValidationResult<IDependencyNode>.ValidationError> errors)
        {
            if (depStrings == null)
                return;

            foreach (var depString in depStrings)
            {
                try
                {
                    deps.Add(DependencyInfo.Parse(depString, type));
                }
                catch (Exception ex)
                {
                    errors.Add(new DependencyValidationResult<IDependencyNode>.ValidationError(
                        DependencyValidationResult<IDependencyNode>.ErrorType.VersionMismatch,
                        identifier,
                        $"Failed to parse {type.ToString().ToLowerInvariant()} dependency '{depString}': {ex.Message}"));
                }
            }
        }

        /// <summary>
        /// Adds a soft load-order edge, dropping it with a warning if honoring it would create a cycle
        /// (i.e. the "after" node can already reach the "before" node through existing edges), or silently
        /// if either endpoint is absent (an ordering hint toward an unloaded pack is simply ignored).
        /// </summary>
        private static void TryAddSoftEdge(
            string other, string self,
            string before, string after,
            IReadOnlyDictionary<string, IDependencyNode> validPacks,
            Dictionary<string, HashSet<string>> successors,
            Dictionary<string, int> indegree,
            List<string> warnings,
            Func<string, string, bool> addEdge)
        {
            if (string.IsNullOrWhiteSpace(other) || other == self || !validPacks.ContainsKey(other))
                return; // Hint names an absent (or self) pack; ignore it.

            if (CanReach(after, before, successors))
            {
                warnings.Add($"Dropped load-order hint '{before}' before '{after}': it would create a load-order cycle.");
                return;
            }

            addEdge(before, after);
        }

        /// <summary>Depth-first reachability: can <paramref name="from"/> reach <paramref name="to"/> via edges?</summary>
        private static bool CanReach(string from, string to, Dictionary<string, HashSet<string>> successors)
        {
            if (from == to)
                return true;

            var stack = new Stack<string>();
            var visited = new HashSet<string>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current))
                    continue;
                if (current == to)
                    return true;
                if (successors.TryGetValue(current, out var next))
                    foreach (var n in next)
                        stack.Push(n);
            }

            return false;
        }

        /// <summary>
        /// Returns the identifiers that cannot be placed in a topological order given the current hard edges:
        /// those on a requirement cycle or downstream of one. Does not mutate the graph.
        /// </summary>
        private static HashSet<string> FindNodesInCycles(
            IEnumerable<string> nodes,
            Dictionary<string, HashSet<string>> successors,
            Dictionary<string, int> indegree)
        {
            var remaining = new HashSet<string>(nodes);
            var working = remaining.ToDictionary(id => id, id => indegree[id]);
            var queue = new Queue<string>(working.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                remaining.Remove(id);
                foreach (var next in successors[id])
                    if (working.ContainsKey(next) && --working[next] == 0)
                        queue.Enqueue(next);
            }

            return remaining; // Anything not drained sits on or after a cycle.
        }

        /// <summary>Removes a node and its outgoing edges, decrementing successors' indegree.</summary>
        private static void RemoveNode(
            string id,
            Dictionary<string, HashSet<string>> successors,
            Dictionary<string, int> indegree)
        {
            if (successors.TryGetValue(id, out var next))
                foreach (var n in next)
                    if (indegree.ContainsKey(n))
                        indegree[n]--;

            successors.Remove(id);
            indegree.Remove(id);
            foreach (var set in successors.Values)
                set.Remove(id);
        }

        private static PackVersion GetPackVersion(IDependencyNode pack)
        {
            if (pack.Version == null)
                return PackVersion.Zero;

            return PackVersion.FromStringOrZero(pack.Version);
        }
    }
}
