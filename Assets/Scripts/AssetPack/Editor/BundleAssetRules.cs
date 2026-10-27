using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetPack.Editor
{
    /// <summary>
    /// Which files a pack's asset bundle lists by name, and the name collisions that would make a load ambiguous.
    /// </summary>
    public static class BundleAssetRules
    {
        // A model loads as a GameObject, as its prefab does, so a model named in the bundle next to a same-named
        // prefab can win the load. Models are never named; the prefab still pulls its model in as a dependency.
        private static readonly string[] ModelExtensions =
            { ".fbx", ".obj", ".blend", ".dae", ".3ds", ".ma", ".mb", ".c4d", ".max", ".dxf" };

        public static bool IsModelFile(string assetPath) =>
            ModelExtensions.Contains(Path.GetExtension(assetPath), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a shared pack names one of its assets' dependencies in its bundle, so dependents link to it
        /// rather than carry their own copy.
        /// </summary>
        /// <param name="absorbsOutsidePack">Only rr/shared: mods link against what its bundle names, so that set
        /// must not change.</param>
        public static bool ShouldAbsorb(string dependencyPath, string packRoot, bool absorbsOutsidePack,
            IEnumerable<string> otherPackRoots)
        {
            // Seen with the coal aggregate in rr/shared: "Packages/com.unity.render-pipelines.universal/Editor/AssetVersion.cs"
            if (Path.GetExtension(dependencyPath) == ".cs")
                return false;
            if (IsModelFile(dependencyPath))
                return false;
            // Unity rejects an asset assigned to two bundles.
            if (otherPackRoots.Any(root => IsUnder(dependencyPath, root)))
                return false;
            return absorbsOutsidePack || IsUnder(dependencyPath, packRoot);
        }

        private static bool IsUnder(string path, string root) =>
            path.StartsWith(root + "/", StringComparison.Ordinal);

        /// <summary>
        /// Pairs of bundle assets of the same type that answer to the same short name (filename without extension,
        /// which is what the runtime loads by), so a load by that name picks one arbitrarily. Loads are typed, so
        /// a material and a prefab sharing a name are fine. An unknown type counts as the same type.
        /// </summary>
        public static List<(string PathA, string PathB)> FindAmbiguousNames(IEnumerable<(string Path, Type MainType)> assets)
        {
            var ambiguous = new List<(string, string)>();
            var groups = assets.GroupBy(asset => Path.GetFileNameWithoutExtension(asset.Path),
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var members = group.ToList();
                for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                {
                    var a = members[i];
                    var b = members[j];
                    if (a.MainType == null || b.MainType == null || a.MainType == b.MainType)
                        ambiguous.Add((a.Path, b.Path));
                }
            }
            return ambiguous;
        }
    }
}
