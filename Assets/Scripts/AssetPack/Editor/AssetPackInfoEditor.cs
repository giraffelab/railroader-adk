using System;
using System.Collections.Generic;
using System.Linq;
using AssetPack.Common;
using AssetPack.Common.Versioning;

namespace AssetPack.Editor
{
    /// <summary>
    /// Helper class for editing and validating AssetPackInfo data.
    /// Provides validation methods and mutation operations for Info.json content.
    /// </summary>
    public class AssetPackInfoEditor
    {
        private readonly AssetPackInfo _info;

        public AssetPackInfoEditor(AssetPackInfo info)
        {
            _info = info ?? throw new ArgumentNullException(nameof(info));
        }

        #region Validation

        public struct ValidationResult
        {
            public bool IsValid;
            public string ErrorMessage;

            public static ValidationResult Valid() => new ValidationResult { IsValid = true, ErrorMessage = null };
            public static ValidationResult Invalid(string message) => new ValidationResult { IsValid = false, ErrorMessage = message };
        }

        public ValidationResult ValidateIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return ValidationResult.Invalid("Identifier cannot be empty");

            if (!Utilities.TryParseDomainAsset(identifier, out var domain, out var packName))
                return ValidationResult.Invalid("Must be format 'domain/pack-name'");

            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(packName))
                return ValidationResult.Invalid("Domain and pack name must not be empty");

            // Validate domain and pack name separately (slash is valid as separator)
            var sanitizedDomain = Utilities.ValidateIdentifier(domain);
            if (sanitizedDomain != domain)
                return ValidationResult.Invalid("Domain contains invalid characters (use lowercase, digits, hyphens, dots only)");

            var sanitizedPackName = Utilities.ValidateIdentifier(packName);
            if (sanitizedPackName != packName)
                return ValidationResult.Invalid("Pack name contains invalid characters (use lowercase, digits, hyphens, dots only)");

            return ValidationResult.Valid();
        }

        /// <summary>
        /// Validates the pack version against <see cref="PackVersion"/> itself, so the editor accepts
        /// exactly what the loader can order. A version the comparison key can't represent is worse
        /// than a rejected one: it would resolve dependency constraints against a number nobody wrote.
        /// </summary>
        public ValidationResult ValidateVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return ValidationResult.Invalid("Version cannot be empty");

            if (!PackVersion.TryParse(version, out _))
                return ValidationResult.Invalid($"Must be {PackVersion.FormatDescription}");

            return ValidationResult.Valid();
        }

        public ValidationResult ValidateScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
                return ValidationResult.Valid(); // Absent = the default 'unscoped'.

            switch (scope.Trim().ToLowerInvariant())
            {
                case "unscoped":
                case "optional":
                    return ValidationResult.Valid();
                case "client":
                    return ValidationResult.Invalid("'client' scope is reserved for a future release and cannot be used yet");
                default:
                    return ValidationResult.Invalid("Must be 'unscoped' or 'optional' (or omitted for the default)");
            }
        }

        public ValidationResult ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return ValidationResult.Valid(); // URL is optional

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return ValidationResult.Invalid("Invalid URL format");

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return ValidationResult.Invalid("URL must use http:// or https://");

            return ValidationResult.Valid();
        }

        /// <summary>
        /// Validates one dependency entry: <c>domain/pack-name</c> optionally followed by
        /// space-separated version constraints, e.g. <c>rr/core &gt;=2025.2.0 &lt;2025.3.0</c>.
        /// Splits the same way <c>DependencyInfo.Parse</c> does at load, so an entry accepted here is
        /// one the loader can parse.
        /// </summary>
        public ValidationResult ValidateDependency(string dependency)
        {
            if (string.IsNullOrWhiteSpace(dependency))
                return ValidationResult.Invalid("Dependency cannot be empty");

            var parts = dependency.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var identifier = parts[0];

            if (!Utilities.TryParseDomainAsset(identifier, out var domain, out var packName))
                return ValidationResult.Invalid("Must be format 'domain/pack-name'");

            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(packName))
                return ValidationResult.Invalid("Domain and pack name must not be empty");

            if (!Utilities.IsValidDomainIdentifier(identifier))
                return ValidationResult.Invalid(
                    "Domain and pack name may only use lowercase letters, digits, hyphens and dots");

            for (var i = 1; i < parts.Length; i++)
            {
                try
                {
                    VersionConstraint.Parse(parts[i]);
                }
                catch (Exception)
                {
                    return ValidationResult.Invalid(
                        $"'{parts[i]}' is not a version constraint. Use e.g. '>=2025.2.0', '<2026.0.0', '~2025.2.0' or '^2025.2.0'");
                }
            }

            return ValidationResult.Valid();
        }

        public ValidationResult ValidateExportKey(string exportKey)
        {
            if (string.IsNullOrWhiteSpace(exportKey))
                return ValidationResult.Invalid("Export key cannot be empty");

            var packDomain = _info.Identifier?.Split('/')[0];

            if (string.IsNullOrWhiteSpace(packDomain))
                return ValidationResult.Invalid("Invalid pack identifier");

            // Validate using the shared helper
            if (!ModuleExportHelpers.TryParseExportKey(exportKey, packDomain, out _))
                return ValidationResult.Invalid($"Must use './module-name' or '{packDomain}/module-name' format");

            return ValidationResult.Valid();
        }

        public ValidationResult ValidateScriptPath(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
                return ValidationResult.Invalid("Script path cannot be empty");

            if (!scriptPath.StartsWith("Scripts/"))
                return ValidationResult.Invalid("Must start with 'Scripts/'");

            if (!scriptPath.EndsWith(".rl"))
                return ValidationResult.Invalid("Must end with '.rl'");

            return ValidationResult.Valid();
        }

        #endregion

        #region Exports

        private void EnsureExports()
        {
            if (_info.Exports == null)
                _info.Exports = new Dictionary<string, string>();
        }

        public void AddExport(string key, string scriptPath)
        {
            var keyValidation = ValidateExportKey(key);
            if (!keyValidation.IsValid)
                throw new ArgumentException($"Invalid export key: {keyValidation.ErrorMessage}");

            var pathValidation = ValidateScriptPath(scriptPath);
            if (!pathValidation.IsValid)
                throw new ArgumentException($"Invalid script path: {pathValidation.ErrorMessage}");

            EnsureExports();

            if (_info.Exports.ContainsKey(key))
                throw new ArgumentException($"Export key '{key}' already exists");

            _info.Exports[key] = scriptPath;
        }

        public void RemoveExport(string key)
        {
            if (_info.Exports != null)
                _info.Exports.Remove(key);
        }

        public Dictionary<string, string> GetExports()
        {
            return _info.Exports ?? new Dictionary<string, string>();
        }

        #endregion
    }
}
