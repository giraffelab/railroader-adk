using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetPack.Common.Dependencies;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AssetPack.Common
{
    /// <summary>
    /// Pure scaffolding for a new development asset pack: validate the identifier, then create the pack
    /// directory with a minimal <c>Info.json</c> and an empty <c>Definitions.json</c>. Has no dependency on
    /// Unity or the running game so it can be unit tested against a temp directory.
    /// </summary>
    public static class DevPackScaffolder
    {
        /// <summary>
        /// True if <paramref name="identifier"/> is a well-formed, discoverable <c>domain/name</c> pack identifier.
        /// </summary>
        /// <remarks>
        /// Stricter than <see cref="Utilities.IsValidDomainIdentifier"/> by one rule: a pack's segments must
        /// begin with a lowercase letter, because AssetPackManager's discovery regex demands it, so a pack
        /// created here is guaranteed to be found. Definition identifiers carry no such constraint.
        /// </remarks>
        public static bool IsValidIdentifier(string identifier)
        {
            return Utilities.IsValidDomainIdentifier(identifier)
                   && identifier.Split('/').All(segment => segment[0] >= 'a' && segment[0] <= 'z');
        }

        /// <summary>
        /// Create a new development pack under <paramref name="modDevRoot"/>.
        /// </summary>
        /// <param name="modDevRoot">Directory that holds development packs (the writeable ModDev source path).</param>
        /// <param name="identifier">Pack identifier in <c>domain/name</c> form.</param>
        /// <param name="displayName">Human-readable pack name written to Info.json.</param>
        /// <param name="author">Optional author written to Info.json. Blank is omitted.</param>
        /// <param name="gameVersion">Running game version, used for the pack's default rr/game requirement.</param>
        /// <param name="existingIdentifiers">
        /// Identifiers of packs already discovered. A new pack must not collide with one of these. May be null.
        /// </param>
        /// <returns>The absolute path of the created pack directory.</returns>
        /// <exception cref="ArgumentException">An input is missing or the identifier is malformed.</exception>
        /// <exception cref="InvalidOperationException">The identifier or its directory already exists.</exception>
        public static string CreatePack(string modDevRoot, string identifier, string displayName, string author,
            string gameVersion, IEnumerable<string> existingIdentifiers = null)
        {
            if (string.IsNullOrWhiteSpace(modDevRoot))
                throw new ArgumentException("ModDev root path is required.", nameof(modDevRoot));

            if (string.IsNullOrWhiteSpace(identifier))
                throw new ArgumentException("Pack identifier is required.", nameof(identifier));

            identifier = identifier.Trim();

            if (!IsValidIdentifier(identifier))
                throw new ArgumentException(
                    $"'{identifier}' is not a valid pack identifier. Use the form domain/name, where each part " +
                    "starts with a lowercase letter and contains only lowercase letters, digits, hyphens, or dots.",
                    nameof(identifier));

            if (string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("Display name is required.", nameof(displayName));

            if (existingIdentifiers != null && existingIdentifiers.Contains(identifier))
                throw new InvalidOperationException($"A pack with identifier '{identifier}' already exists.");

            if (!Utilities.TryParseDomainAsset(identifier, out _, out var nameSegment) ||
                string.IsNullOrWhiteSpace(nameSegment))
                throw new ArgumentException($"Could not derive a directory name from identifier '{identifier}'.",
                    nameof(identifier));

            var packDirectory = Path.Combine(modDevRoot, nameSegment);
            if (Directory.Exists(packDirectory))
                throw new InvalidOperationException(
                    $"A directory named '{nameSegment}' already exists at {packDirectory}.");

            var gameRequirement = GamePackDependencyHelper.DefaultGameRequirement(identifier, gameVersion);

            Directory.CreateDirectory(packDirectory);

            var info = new AssetPackInfo
            {
                Identifier = identifier,
                Version = "1.0.0",
                Name = displayName.Trim(),
                Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
                Dependencies = new AssetPackDependencies
                {
                    // Leave Optional and Incompatible null so only the required list is written.
                    Required = new List<string> { gameRequirement },
                    Optional = null,
                    Incompatible = null,
                }
            };

            File.WriteAllText(Path.Combine(packDirectory, Filenames.Info), SerializeInfo(info));
            File.WriteAllText(Path.Combine(packDirectory, Filenames.Definitions), EmptyDefinitionsJson);

            return packDirectory;
        }

        private const string EmptyDefinitionsJson = "{\n  \"objects\": []\n}";

        /// <summary>
        /// Writes <paramref name="info"/> back to the pack's <c>Info.json</c> at
        /// <paramref name="packDirectory"/>, byte-shaped the same as a create or an ADK-authored pack.
        /// Used by the in-game pack metadata editor to save edited fields. Grooms empty optional and
        /// incompatible dependency lists to null first, matching the ADK editor's writer (an empty
        /// required list is kept, as the create flow writes it). Mutates <paramref name="info"/>'s
        /// dependency lists during grooming, so pass a copy if the caller must keep empties.
        /// </summary>
        public static void WriteInfo(string packDirectory, AssetPackInfo info)
        {
            if (string.IsNullOrWhiteSpace(packDirectory))
                throw new ArgumentException("Pack directory is required.", nameof(packDirectory));
            if (info == null)
                throw new ArgumentNullException(nameof(info));

            if (info.Dependencies != null)
            {
                if (info.Dependencies.Optional != null && info.Dependencies.Optional.Count == 0)
                    info.Dependencies.Optional = null;
                if (info.Dependencies.Incompatible != null && info.Dependencies.Incompatible.Count == 0)
                    info.Dependencies.Incompatible = null;
            }

            File.WriteAllText(Path.Combine(packDirectory, Filenames.Info), SerializeInfo(info));
        }

        /// <summary>
        /// Deep-copies an <see cref="AssetPackInfo"/> via a full JSON round-trip so the metadata editor can
        /// hold an editable working copy without touching the live loaded info until the author saves.
        /// Preserves all known fields (no default/null omission); fields absent from the loaded DTO were
        /// already dropped at load and are not recoverable here.
        /// </summary>
        public static AssetPackInfo Clone(AssetPackInfo info)
        {
            if (info == null)
                return null;
            return JsonConvert.DeserializeObject<AssetPackInfo>(JsonConvert.SerializeObject(info));
        }

        // Matches the editor-side Info.json writer (camelCase, empties omitted, indented) so packs created
        // in-game are byte-shaped the same as packs authored in the ADK.
        private static string SerializeInfo(AssetPackInfo info)
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Formatting = Formatting.Indented
            };
            return JsonConvert.SerializeObject(info, settings);
        }
    }
}
