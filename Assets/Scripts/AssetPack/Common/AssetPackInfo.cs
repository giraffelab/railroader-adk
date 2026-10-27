using System.Collections.Generic;
using AssetPack.Common.Dependencies;

namespace AssetPack.Common
{
    /// <summary>
    /// Data stored in the <c>Info.json</c> file of an asset pack.
    /// </summary>
    public class AssetPackInfo : IDependencyNode
    {
        /// <summary>
        /// Must be of the form <c>domain/pack</c>.
        /// Domain describes the creator/originator of the pack.
        /// Reserved domains include: rr, steam-*, core, builtin
        /// </summary>
        public string Identifier { get; set; }
        /// <summary>
        /// Display name of this pack.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Display author of this pack.
        /// </summary>
        public string Author { get; set; }
        /// <summary>
        /// Version of this pack. Must be semantic version format: <c>major.minor.patch</c>.
        /// </summary>
        public string Version { get; set; }
        /// <summary>
        /// URL of website for this pack.
        /// </summary>
        public string Url { get; set; }
        /// <summary>
        /// Brief description of what this pack provides.
        /// </summary>
        public string Description { get; set; }
        /// <summary>
        /// License of this pack.
        /// </summary>
        public string License { get; set; }

        /// <summary>
        /// Which peers this pack is for. One of:
        /// <list type="bullet">
        /// <item><c>unscoped</c> (or absent): the default. Recorded in saves and required to match across multiplayer peers.</item>
        /// <item><c>optional</c>: recorded in saves like unscoped, but not required. It runs wherever installed, yet a peer missing it (or holding another version) is never blocked from joining/loading.</item>
        /// <item><c>client</c>: reserved for a future release; do not use.</item>
        /// </list>
        /// An unrecognized value is a load error.
        /// </summary>
        public string Scope { get; set; }

        public AssetPackDependencies Dependencies { get; set; }

        /// <summary>
        /// Optional load-order hints. Separate from <see cref="Dependencies"/>: these influence
        /// <em>when</em> this pack loads relative to others, not <em>whether</em> it loads.
        /// </summary>
        public AssetPackLoadOrder Load { get; set; }

        /// <summary>
        /// Module exports configuration. Maps export specifiers to script file paths.
        /// Export specifiers must use either:
        /// - Relative format: "./module-name" (exports as "{domain}/module-name")
        /// - Explicit format: "{domain}/module-name" (must match pack's domain)
        ///
        /// Example for pack "rr/loads-models01":
        /// {
        ///   "./conditional-stakes": "Scripts/conditional-stakes.rl"
        /// }
        /// This exports the module as "rr/conditional-stakes", imported via: import "rr/conditional-stakes"
        /// </summary>
        public Dictionary<string, string> Exports { get; set; }

        /// <summary>
        /// Managed assemblies (<c>.dll</c>) this pack ships for the game to load. Each entry is a path
        /// relative to the pack root; a path that escapes the pack directory is a load error. Absent or
        /// empty means the pack ships no code, which is the common case.
        /// <para>
        /// A loaded assembly runs with the full privilege of the game process. It is not sandboxed the
        /// way Relay scripts are. See <c>Docs/Modding-ManagedAssemblies.md</c>.
        /// </para>
        /// </summary>
        public List<string> Assemblies { get; set; }

        public override string ToString() => string.IsNullOrEmpty(Identifier) ? "<AssetPackInfo Null Identifier>" : Identifier;
    }

    public class AssetPackDependencies
    {
        public List<string> Required { get; set; } = new();
        public List<string> Optional { get; set; } = new();
        public List<string> Incompatible { get; set; } = new();
    }

    /// <summary>
    /// Load-order hints for an asset pack. Unlike <see cref="AssetPackDependencies"/>, these are
    /// <em>soft</em>: an entry naming a pack that is not present is ignored rather than an error, and
    /// a hint that would create a load-order cycle is dropped (with a warning) rather than failing the
    /// load. Use <see cref="AssetPackDependencies.Required"/> when the other pack must actually be present.
    /// </summary>
    public class AssetPackLoadOrder
    {
        /// <summary>
        /// Identifiers of packs this pack should load <em>after</em>, when they are present.
        /// Bare <c>domain/pack</c> identifiers, with no version constraints (an ordering hint is
        /// version-agnostic; a version requirement belongs in <see cref="AssetPackDependencies"/>).
        /// </summary>
        public List<string> After { get; set; } = new();

        /// <summary>
        /// Identifiers of packs this pack should load <em>before</em>, when they are present.
        /// </summary>
        public List<string> Before { get; set; } = new();

        /// <summary>
        /// Tiebreak among packs that no dependency or ordering hint already orders. Lower values load
        /// earlier; the default is <c>0</c>. Priority never overrides an ordering edge; it only breaks
        /// ties between otherwise-unconstrained packs.
        /// </summary>
        public int Priority { get; set; }
    }

    /// <summary>
    /// Helper methods for working with module exports.
    /// </summary>
    public static class ModuleExportHelpers
    {
        /// <summary>
        /// Finds the script path for a module in an asset pack's exports dictionary.
        /// </summary>
        /// <param name="packInfo">The asset pack info containing exports</param>
        /// <param name="moduleName">The module name to find (without domain, e.g., "conditional-stakes")</param>
        /// <returns>Script path if found, null otherwise</returns>
        public static string FindExportedScriptPath(AssetPackInfo packInfo, string moduleName)
        {
            if (packInfo?.Exports == null || packInfo.Exports.Count == 0)
                return null;

            // Extract pack's domain from identifier (e.g., "rr/loads-models01" -> "rr")
            var packDomain = packInfo.Identifier?.Split('/')[0];
            if (string.IsNullOrWhiteSpace(packDomain))
                return null;

            // Iterate through exports to find one that matches the module name
            foreach (var kvp in packInfo.Exports)
            {
                if (TryParseExportKey(kvp.Key, packDomain, out var exportedModuleName) &&
                    exportedModuleName == moduleName)
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Parses an export key to extract the module name it exports.
        /// </summary>
        /// <param name="exportKey">The export key from the exports dictionary (e.g., "./stakes" or "rr/stakes")</param>
        /// <param name="packDomain">The domain of the pack containing this export (e.g., "rr")</param>
        /// <param name="moduleName">Output: the module name (e.g., "stakes")</param>
        /// <returns>True if the export key is valid and was parsed successfully, false otherwise</returns>
        public static bool TryParseExportKey(string exportKey, string packDomain, out string moduleName)
        {
            moduleName = null;

            if (string.IsNullOrWhiteSpace(exportKey))
                return false;

            if (exportKey.StartsWith("./"))
            {
                // Relative format: "./stakes" -> module name is "stakes"
                moduleName = exportKey.Substring(2);
                return !string.IsNullOrWhiteSpace(moduleName);
            }

            if (Utilities.TryParseDomainAsset(exportKey, out var exportDomain, out moduleName) && exportDomain == packDomain)
            {
                return !string.IsNullOrWhiteSpace(moduleName);
            }

            // Invalid format
            return false;
        }
    }
}