using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

namespace AssetPack.Common
{
    public class AssetPackRecord
    {
        public string Path { get; set; }
        public AssetPackInfo Info { get; set; }
    }
    
    public static class Utilities
    {
        /// <summary>
        /// Parses a domain/asset identifier into its component parts.
        /// </summary>
        /// <param name="input">Identifier in format "domain/asset" (e.g., "rr/core", "steam-123456/my-pack")</param>
        /// <param name="domain">The domain part before the slash</param>
        /// <param name="asset">The asset/pack name part after the slash</param>
        /// <returns>True if parsing succeeded, false if no slash found</returns>
        public static bool TryParseDomainAsset(string input, out string domain, out string asset)
        {
            var slashIndex = input.IndexOf('/');
            if (slashIndex < 0)
            {
                domain = null;
                asset = null;
                return false;
            }

            domain = input.Substring(0, slashIndex);
            asset = input.Substring(slashIndex + 1);
            return true;
        }

        /// <summary>
        /// Find asset packs within the given path.
        /// </summary>
        /// <param name="basePath">Path to search.</param>
        /// <param name="validationResultsOrNull">List which will receive validation results. May be null to ignore.</param>
        /// <returns>File name and extension of each found asset pack.</returns>
        public static List<AssetPackRecord> FindAssetPacks(string basePath, List<string> validationResultsOrNull)
        {
            if (!Directory.Exists(basePath))
                return new List<AssetPackRecord>();
            return Directory.GetDirectories(basePath).Select(dirPath =>
                {
                    var infoPath = Path.Combine(dirPath, Filenames.Info);
                    if (!File.Exists(infoPath))
                        return null;
                    try
                    {
                        var json = File.ReadAllText(infoPath);
                        var info = JsonConvert.DeserializeObject<AssetPackInfo>(json);

                        if (validationResultsOrNull != null)
                            ValidateModuleExports(info, dirPath, validationResultsOrNull);

                        return new AssetPackRecord()
                        {
                            Path = dirPath,
                            Info = info,
                        };
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Failed to load Info.json from {infoPath}: {e}");
                    }

                    return null;
                })
                .Where(name => name != null)
                .ToList();
        }

        /// <summary>
        /// Validates module exports in an AssetPackInfo.
        /// Logs warnings for invalid exports but doesn't prevent pack loading.
        /// </summary>
        /// <param name="info">The AssetPackInfo to validate</param>
        /// <param name="packPath">Path to the asset pack for file existence checks</param>
        /// <param name="validationResults">Non-null list to receive validation results</param>
        public static void ValidateModuleExports(AssetPackInfo info, string packPath, List<string> validationResults)
        {
            if (info?.Exports == null || info.Exports.Count == 0)
                return;

            // Extract pack's domain from identifier (e.g., "rr/loads-models01" -> "rr")
            var packDomain = info.Identifier?.Split('/')[0];
            if (string.IsNullOrWhiteSpace(packDomain))
            {
                validationResults.Add($"Pack '{info.Identifier}': Cannot validate exports - invalid pack identifier format");
                return;
            }

            var seenModuleNames = new HashSet<string>();

            foreach (var kvp in info.Exports)
            {
                var exportKey = kvp.Key;
                var scriptPath = kvp.Value;

                // Validate export key format using the shared helper
                if (!ModuleExportHelpers.TryParseExportKey(exportKey, packDomain, out var moduleName))
                {
                    validationResults.Add($"Pack '{info.Identifier}': Export key '{exportKey}' is invalid. Must use './' prefix or explicit domain format '{packDomain}/module-name'");
                    continue;
                }

                // Check for duplicate module names
                if (seenModuleNames.Contains(moduleName))
                {
                    validationResults.Add($"Pack '{info.Identifier}': Duplicate module export for '{moduleName}'");
                }
                seenModuleNames.Add(moduleName);

                // Validate script path
                if (string.IsNullOrWhiteSpace(scriptPath))
                {
                    validationResults.Add($"Pack '{info.Identifier}': Export '{exportKey}' has empty script path");
                    continue;
                }

                if (!scriptPath.StartsWith("Scripts/"))
                {
                    validationResults.Add($"Pack '{info.Identifier}': Export '{exportKey}' should start with 'Scripts/': '{scriptPath}'");
                }

                // Check if file exists (path is relative to Scripts/ directory)
                var fullPath = Path.Combine(packPath, scriptPath);
                if (!File.Exists(fullPath))
                {
                    validationResults.Add($"Pack '{info.Identifier}': Export '{exportKey}' references non-existent file '{scriptPath}'");
                }
            }
        }

        /// <summary>
        /// Sanitizes a string to create a valid asset pack identifier.
        /// Replaces whitespace with hyphens, collapses multiple hyphens, and removes all characters
        /// except lowercase letters (a-z), digits (0-9), hyphens (-), and periods (.).
        /// </summary>
        /// <param name="str">The string to sanitize. Can be null.</param>
        /// <returns>
        /// Sanitized identifier containing only [a-z0-9-.], or null if input is null, or empty string on error.
        /// </returns>
        /// <remarks>
        /// NOTE: Does NOT convert uppercase to lowercase - uppercase letters are removed as invalid characters.
        /// Callers should convert to lowercase before calling if case-insensitive sanitization is needed.
        /// </remarks>
        public static string ValidateIdentifier(string str)
        {
            if (str == null)
                return null;
            try
            {
                str = Regex.Replace(str, @"\s+", "-");
                str = Regex.Replace(str, @"-+", "-");
                str = Regex.Replace(str, @"[^a-z0-9-\.]", "");
                return str;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error validating identifier: {str}");
                #if DEBUG
                Debug.LogException(e);
                #endif
                return "";
            }
        }

        private static readonly Regex IdentifierPartRegex = new(@"\A[a-z0-9][a-z0-9.-]*\z", RegexOptions.Compiled);

        /// <summary>
        /// Checks if an identifier is valid in the 'domain/name' format. Each part must start with a
        /// lowercase letter or digit and continue with lowercase letters, digits, hyphens or dots.
        /// </summary>
        /// <param name="identifier">The identifier to validate (e.g., "my-domain/my-asset")</param>
        /// <returns>True if the identifier is valid, false otherwise</returns>
        /// <remarks>
        /// Matched directly rather than by asking whether <see cref="ValidateIdentifier"/> would rewrite
        /// the string: that sanitizer also collapses runs of hyphens, so it rejects a well-formed
        /// "flatcar04--test". A leading digit is allowed because shipped content uses it
        /// ("rr/1860s-farm-house"); pack identifiers are the stricter case and are checked by
        /// <see cref="DevPackScaffolder.IsValidIdentifier"/>.
        /// </remarks>
        public static bool IsValidDomainIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            if (!TryParseDomainAsset(identifier, out var domain, out var name))
                return false;

            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name))
                return false;

            return IdentifierPartRegex.IsMatch(domain) && IdentifierPartRegex.IsMatch(name);
        }

    }

    public static class ScriptPathHelpers
    {
        /// <summary>
        /// Converts an export script path to the identifier format expected by AssetPackScriptLoader.LoadScript().
        /// Export paths like "Scripts/demo.rl" become "demo" (without prefix or extension).
        /// </summary>
        /// <param name="exportPath">The script path from exports (e.g., "Scripts/demo.rl")</param>
        /// <returns>Script identifier without "Scripts/" prefix or ".rl" extension</returns>
        public static string ExportPathToScriptIdentifier(string exportPath)
        {
            if (string.IsNullOrEmpty(exportPath))
                return exportPath;

            var scriptIdentifier = exportPath;

            // Remove "Scripts/" prefix if present
            if (scriptIdentifier.StartsWith("Scripts/"))
                scriptIdentifier = scriptIdentifier.Substring(8); // "Scripts/".Length == 8

            // Remove .rl extension if present
            if (scriptIdentifier.EndsWith(".rl"))
                scriptIdentifier = scriptIdentifier.Substring(0, scriptIdentifier.Length - 3);

            return scriptIdentifier;
        }
    }

    public static class Filenames
    {
        public const string AssetBundle = "Bundle";
        public const string Info = "Info.json";
        public const string Definitions = "Definitions.json";
    }
}