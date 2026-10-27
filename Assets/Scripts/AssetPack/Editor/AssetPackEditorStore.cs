using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetPack.Common;
using AssetPack.Common.Dependencies;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AssetPack.Editor
{
    public class AssetPackEditorStore
    {
        public static readonly string LocalBasePath = Path.Combine("Assets", "AssetPacks");
        public static readonly string SharedPacksBasePath = Path.Combine("Assets", "SharedPacks");

        public string Identifier
        {
            get => _info.Identifier;
            set => _info.Identifier = value;
        }

        public string Name
        {
            get => _info.Name;
            set => _info.Name = value;
        }

        /// <summary>
        /// Directory name on disk (e.g., "shared" for the rr/shared pack).
        /// Use this for constructing file paths, not Identifier.
        /// </summary>
        public string DirectoryName => _directoryName;

        /// <summary>
        /// Gets the full path to this pack's source assets directory.
        /// </summary>
        public string SourcePath => PathForAssetPackSource();

        /// <summary>
        /// When true, this pack is included in asset bundle builds for dependency resolution
        /// but its build output (bundle, Info.json, scripts, etc.) is not copied to the build path.
        /// Used for shared/reference packs in the ADK that the game already ships with.
        /// </summary>
        public bool IsReferencePack { get; }

        private string _path;
        private string _directoryName; // Directory name on disk (e.g., "shared")
        private string _sourceDirectoryName; // Differs only for a distribution build checkout
        private AssetPackInfo _info;

        private AssetPackEditorStore(string path, string directoryName, AssetPackInfo info, bool isReferencePack = false,
            string sourceDirectoryName = null)
        {
            _path = path;
            _directoryName = directoryName;
            _sourceDirectoryName = sourceDirectoryName ?? directoryName;
            _info = info ?? throw new ArgumentNullException(nameof(info));
            IsReferencePack = isReferencePack;
        }

        /// <summary>
        /// Gets the AssetPackInfo for this pack.
        /// </summary>
        public AssetPackInfo GetInfo()
        {
            return _info;
        }

        #region Dependency Management

        /// <summary>
        /// Ensures the Dependencies object exists.
        /// </summary>
        private void EnsureDependencies()
        {
            if (_info.Dependencies == null)
            {
                _info.Dependencies = new AssetPackDependencies();
            }
        }

        /// <summary>
        /// Validates that a dependency identifier is well-formed.
        /// Expected format: "domain/pack-name" (e.g., "rr/core", "steam-123456/my-pack")
        /// </summary>
        private bool IsValidDependencyIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            // Use existing utility method for consistent parsing
            if (!Utilities.TryParseDomainAsset(identifier, out var domain, out var packName))
                return false;

            // Both domain and pack name must be non-empty
            return !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(packName);
        }

        public void AddRequiredDependency(string dependency)
        {
            if (!IsValidDependencyIdentifier(dependency))
                throw new ArgumentException($"Invalid dependency identifier: '{dependency}'. Expected format: 'domain/pack-name'");

            EnsureDependencies();
            if (_info.Dependencies.Required == null)
                _info.Dependencies.Required = new List<string>();
            if (!_info.Dependencies.Required.Contains(dependency))
                _info.Dependencies.Required.Add(dependency);
        }

        public void RemoveRequiredDependency(string dependency)
        {
            if (_info?.Dependencies?.Required != null)
                _info.Dependencies.Required.Remove(dependency);
        }

        public void AddOptionalDependency(string dependency)
        {
            if (!IsValidDependencyIdentifier(dependency))
                throw new ArgumentException($"Invalid dependency identifier: '{dependency}'. Expected format: 'domain/pack-name'");

            EnsureDependencies();
            if (_info.Dependencies.Optional == null)
                _info.Dependencies.Optional = new List<string>();
            if (!_info.Dependencies.Optional.Contains(dependency))
                _info.Dependencies.Optional.Add(dependency);
        }

        public void RemoveOptionalDependency(string dependency)
        {
            if (_info?.Dependencies?.Optional != null)
                _info.Dependencies.Optional.Remove(dependency);
        }

        public void AddIncompatibleDependency(string dependency)
        {
            if (!IsValidDependencyIdentifier(dependency))
                throw new ArgumentException($"Invalid dependency identifier: '{dependency}'. Expected format: 'domain/pack-name'");

            EnsureDependencies();
            if (_info.Dependencies.Incompatible == null)
                _info.Dependencies.Incompatible = new List<string>();
            if (!_info.Dependencies.Incompatible.Contains(dependency))
                _info.Dependencies.Incompatible.Add(dependency);
        }

        public void RemoveIncompatibleDependency(string dependency)
        {
            if (_info?.Dependencies?.Incompatible != null)
                _info.Dependencies.Incompatible.Remove(dependency);
        }

        public List<string> GetRequiredDependencies()
        {
            return _info?.Dependencies?.Required ?? new List<string>();
        }

        /// <summary>
        /// Identifiers of the required packs, without version constraints ("acme/base >=1.0" is "acme/base").
        /// </summary>
        public IEnumerable<string> RequiredPackIdentifiers()
        {
            foreach (var dependency in GetRequiredDependencies())
            {
                if (string.IsNullOrWhiteSpace(dependency))
                {
                    Debug.LogWarning($"{Identifier}: ignoring a blank entry in dependencies.required");
                    continue;
                }

                string identifier;
                try
                {
                    identifier = DependencyInfo.Parse(dependency, DependencyInfo.DependencyType.Required).Identifier;
                }
                catch (ArgumentException e)
                {
                    // The game rejects this pack at load anyway; the build shouldn't fail on another pack's account.
                    Debug.LogWarning($"{Identifier}: invalid required dependency '{dependency}': {e.Message}");
                    identifier = dependency.Trim().Split(' ')[0];
                }
                yield return identifier;
            }
        }

        public List<string> GetOptionalDependencies()
        {
            return _info?.Dependencies?.Optional ?? new List<string>();
        }

        public List<string> GetIncompatibleDependencies()
        {
            return _info?.Dependencies?.Incompatible ?? new List<string>();
        }

        #endregion

        #region Source Serialization

        public static AssetPackEditorStore Load(string path, string directoryName, bool isReferencePack = false,
            string sourceDirectoryName = null)
        {
            var infoPath = Path.Combine(path, sourceDirectoryName ?? directoryName, Filenames.Info);
            AssetPackInfo info = null;

            if (File.Exists(infoPath))
            {
                // A present-but-unreadable Info.json is an error: falling back to defaults would
                // silently distribute the pack with the wrong name/version and dropped dependencies.
                try
                {
                    var json = File.ReadAllText(infoPath);
                    info = JsonConvert.DeserializeObject<AssetPackInfo>(json);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        $"Failed to parse Info.json for '{directoryName}' ({infoPath}): {e.Message}", e);
                }

                if (info == null)
                    throw new InvalidOperationException(
                        $"Info.json for '{directoryName}' ({infoPath}) deserialized to null.");
            }

            // If no Info.json exists, create a minimal one with directory name as identifier
            if (info == null)
            {
                info = new AssetPackInfo
                {
                    Identifier = directoryName,
                    Name = "New Asset Pack",
                    Version = "1.0.0"
                };
            }

            var store = new AssetPackEditorStore(path, directoryName, info, isReferencePack, sourceDirectoryName);
            return store;
        }

        public static AssetPackEditorStore Empty(string path, string directoryName, string identifier)
        {
            var info = new AssetPackInfo
            {
                Identifier = identifier,
                Name = "New Asset Pack",
                Version = "1.0.0",
                Dependencies = new AssetPackDependencies
                {
                    Required = new List<string> { GamePackDependencyHelper.DefaultGameRequirement(identifier, Application.version) },
                }
            };
            return new AssetPackEditorStore(path, directoryName, info);
        }

        public void Save()
        {
            if (IsReferencePack)
                throw new InvalidOperationException("Cannot save a reference pack through the editor.");

            // Note: We don't rename the directory when the identifier changes.
            // The directory name (_directoryName) stays constant, only the identifier in Info.json changes.
            var sourcePath = PathForAssetPackSource();

            Directory.CreateDirectory(sourcePath);

            // Update Info with current values
            _info.Identifier = Identifier;

            // Write Info.json
            var infoPath = Path.Combine(sourcePath, Filenames.Info);
            WriteInfoJson(infoPath);
            AssetDatabase.Refresh();

            // Newtonsoft JSON added by adding package name "com.unity.nuget.newtonsoft-json" (3.0.2)
            // https://github.com/jilleJr/Newtonsoft.Json-for-Unity/wiki/Install-official-via-UPM
        }

        public static List<string> FindAllStores(string path)
        {
            if (!Directory.Exists(path))
                return new List<string>();
            return Directory.GetDirectories(path).Select(directory =>
                {
                    var infoPath = Path.Combine(directory, Filenames.Info);
                    if (File.Exists(infoPath))
                        return Path.GetFileName(directory);
                    return null;
                })
                .Where(name => name != null)
                .ToList();
        }

        /// <summary>
        /// Loads all packs in SharedPacksBasePath as reference packs.
        /// These are included in builds for dependency resolution but their output is discarded.
        /// </summary>
        public static List<AssetPackEditorStore> LoadSharedPacks()
        {
            var result = new List<AssetPackEditorStore>();
            if (!Directory.Exists(SharedPacksBasePath))
                return result;

            foreach (var directoryName in FindAllStores(SharedPacksBasePath))
            {
                var store = Load(SharedPacksBasePath, directoryName, isReferencePack: true);
                result.Add(store);
            }

            return result;
        }

        private void WriteInfoJson(string infoPath)
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Formatting = Formatting.Indented
            };

            // Clean up empty arrays before serialization
            if (_info.Dependencies != null)
            {
                if (_info.Dependencies.Optional != null && _info.Dependencies.Optional.Count == 0)
                    _info.Dependencies.Optional = null;
                if (_info.Dependencies.Incompatible != null && _info.Dependencies.Incompatible.Count == 0)
                    _info.Dependencies.Incompatible = null;
            }

            var json = JsonConvert.SerializeObject(_info, settings);
            File.WriteAllText(infoPath, json);
        }

        /// <summary>
        /// Path for the source assets contained within this pack.
        /// These assets are used to build the asset bundle ("pack").
        /// Returns Unity-style path with forward slashes for AssetDatabase compatibility.
        /// Uses the folder on disk (e.g., "shared"), not the identifier (e.g., "rr/shared").
        /// </summary>
        private string PathForAssetPackSource()
        {
            return StandardizePathSeparators(Path.Combine(_path, _sourceDirectoryName));
        }

        #endregion

        #region Asset Management

        private static class TypeFilters
        {
            public const string Prefab = "t:Prefab";
            public const string Material = "t:Material";
            public const string Texture2D = "t:Texture2D";
            public const string AudioClip = "t:AudioClip";
        }

        private static readonly (string TypeFilter, string DisplayName)[] AssetTypes =
        {
            (TypeFilters.Prefab, "Prefab"),
            (TypeFilters.Material, "Material"),
            (TypeFilters.Texture2D, "Texture"),
            (TypeFilters.AudioClip, "Audio"),
        };

        public IEnumerable<Asset> EnumerateAssets()
        {
            var pathForAssetPackSource = PathForAssetPackSource();
            if (!Directory.Exists(pathForAssetPackSource))
                yield break;

            foreach (var (typeFilter, displayName) in AssetTypes)
            {
                var guids = AssetDatabase.FindAssets(typeFilter, new[] { pathForAssetPackSource });
                foreach (var guid in guids)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    // Only include assets directly in the pack folder, not subdirectories
                    var assetDir = StandardizePathSeparators(Path.GetDirectoryName(assetPath));
                    if (assetDir != pathForAssetPackSource)
                        continue;
                    // FindAssets returns an .fbx under t:Prefab, t:Material and t:Texture2D
                    if (BundleAssetRules.IsModelFile(assetPath))
                        continue;
                    var assetIdentifier = Path.GetFileNameWithoutExtension(assetPath);
                    var filename = Path.GetFileName(assetPath);
                    yield return new Asset(assetIdentifier, displayName, filename);
                }
            }
        }

        private static string StandardizePathSeparators(string path)
        {
            return path?.Replace('\\', '/');
        }

        #endregion

        /// <summary>Path to this exact asset, built from its filename. No lookup.</summary>
        public string PathForAsset(Asset asset)
        {
            var filename = asset.filename;
            if (string.IsNullOrEmpty(asset.filename))
            {
                Debug.Log($"Asset {asset.name} missing filename - re-save pack - assuming .prefab");
                filename = asset.name + ".prefab";
            }
            var basePath = PathForAssetPackSource();
            return StandardizePathSeparators(Path.Combine(basePath, filename));
        }

        /// <summary>
        /// Path to the first asset matching the identifier. It's a lookup, so duplicate identifiers
        /// (foo.prefab + foo.fbx) resolve to just one. Prefer <see cref="PathForAsset(Asset)"/> when
        /// you already hold the asset.
        /// </summary>
        private string FindAssetPath(string assetIdentifier)
        {
            // Try to find the asset by enumerating
            var asset = EnumerateAssets().FirstOrDefault(a => a.name == assetIdentifier);
            if (asset.name != null)
                return PathForAsset(asset);

            // Fallback to Unity AssetDatabase search
            var basePath = PathForAssetPackSource();
            var guids = AssetDatabase.FindAssets(assetIdentifier, new[] {basePath});
            if (guids.Length != 1)
                throw new Exception($"Unexpected number of assets for {Identifier}: {guids.Length}");
            return AssetDatabase.GUIDToAssetPath(guids[0]);
        }

        public void SelectAsset(string assetIdentifier)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(FindAssetPath(assetIdentifier));
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
        
        #region Build

        public static void BuildBundles(string basePath, List<AssetPackEditorStore> assetPackStores)
        {
            var numBundles = assetPackStores.Count;
            var buildPaths = new string[numBundles];
            var packAnalyses = new List<AssetPackTextureBudgetAnalyzer.PackTextureAnalysis>();

            // Some packs have no assets (only Definitions.json, scripts, etc.) and produce no bundle.
            var bundleBuilds = new Dictionary<int, AssetBundleBuild>();
            for (var index = 0; index < numBundles; index++)
            {
                var store = assetPackStores[index];
                // buildPath is unused for reference packs (output is discarded).
                var buildPath = Path.Combine(basePath, store.DirectoryName);
                buildPaths[index] = buildPath;

                if (store.IsReferencePack)
                {
                    Debug.Log($"Build (reference only, output will be discarded): {store.Identifier}");
                }
                else
                {
                    Debug.Log($"Build: {store.Identifier} -> {buildPath}");

                    if (!Directory.Exists(buildPath))
                    {
                        Debug.Log($"Create directory: {buildPath}");
                        Directory.CreateDirectory(buildPath);
                    }
                }

                var build = store.MakeAssetBundleBuild(assetPackStores);
                if (build.HasValue)
                {
                    bundleBuilds[index] = build.Value;
                }
            }

            // Build the bundle into a temporary directory so that we can easily take only the bundle file.
            // Note that the BuildAssetBundle method determines the name of the bundle. This determines both the name
            // inside the bundle, but also the filename. For loading purposes we want the name to be stable, so we
            // copy only that file (ignoring the .manifest and other files) and give it a stable name.
            var tempBuildPath = Path.Combine(Application.temporaryCachePath, Guid.NewGuid().ToString());
            try
            {
                Directory.CreateDirectory(tempBuildPath);

                if (bundleBuilds.Count > 0)
                {
                    BuildAssetBundles(tempBuildPath, bundleBuilds.Values.ToArray());
                }

                // Copy the built bundles into their respective local locations:
                foreach (var (index, assetBundleBuild) in bundleBuilds)
                {
                    // Reference packs are built for dependency resolution but output is discarded.
                    if (assetPackStores[index].IsReferencePack)
                        continue;

                    var buildPath = buildPaths[index];
                    var sourceFileName = Path.Combine(tempBuildPath, assetBundleBuild.assetBundleName);
                    var destFileName = Path.Combine(buildPath, Filenames.AssetBundle);
                    if (!File.Exists(sourceFileName))
                    {
                        Debug.LogError($"Bundle file not found: {sourceFileName}");
                        continue;
                    }
                    Debug.Log($"Copy bundle: {sourceFileName} to {destFileName}");
                    File.Copy(sourceFileName, destFileName, true);
                }
            }
            finally
            {
                Directory.Delete(tempBuildPath, true);
            }

            for (var index = 0; index < numBundles; index++)
            {
                var store = assetPackStores[index];

                // Reference packs: no output to copy or analyze.
                if (store.IsReferencePack)
                    continue;

                var buildPath = buildPaths[index];

                var sourcePath = store.PathForAssetPackSource();

                // A build reproduces the source, so every artifact copies unconditionally. Protecting a
                // newer file in the destination is a development-time concern, and it belongs to the
                // workspace window's Push Text / Pull (which asks before replacing) rather than here:
                // the built pack is output, not somewhere edits are meant to live.
                foreach (var filename in new[] {Filenames.Definitions, Filenames.Info})
                {
                    var sourceFilePath = Path.Combine(sourcePath, filename);
                    if (!File.Exists(sourceFilePath)) continue;
                    var destFilePath = Path.Combine(buildPath, filename);
                    Debug.Log($"Copying {sourceFilePath} to {destFilePath}");
                    File.Copy(sourceFilePath, destFilePath, true);
                }

                // Definitions reference payload files by path, so they have to land at the same relative
                // path or the reference dangles in the shipped pack.
                var dataPlan = AssetPackSyncHelper.MirrorPayload(
                    sourcePath, buildPath);
                if (dataPlan.Writes.Count > 0)
                    Debug.Log($"Copying payload: {string.Join(", ", dataPlan.Writes)}");
                // Deletions are the destructive half, so they are never silent.
                if (dataPlan.Deletes.Count > 0)
                    Debug.LogWarning($"Removing payload the source no longer has, from {buildPath}: " +
                                     string.Join(", ", dataPlan.Deletes));

                // Analyze texture memory usage for this pack
                var analysis = AssetPackTextureBudgetAnalyzer.AnalyzePackTextures(store, buildPath);
                packAnalyses.Add(analysis);
            }

            // Generate and display texture budget report
            AssetPackTextureBudgetAnalyzer.GenerateTextureBudgetReport(packAnalyses);
        }

        private AssetBundleBuild? MakeAssetBundleBuild(List<AssetPackEditorStore> allPacks)
        {
            var assets = EnumerateAssets().ToList();
            Debug.Log($"Building asset bundle for pack {Identifier} with {assets.Count} assets");

            var bundleAssets = assets.Select(asset => PathForAsset(asset)).Where(assetPath =>
            {
                if (File.Exists(assetPath)) return true;
                Debug.LogWarning($"Asset not found: {assetPath}, will be skipped.");
                return false;
            }).ToList();

            if (bundleAssets.Count == 0)
            {
                Debug.Log($"Asset pack {Identifier} has no assets; skipping bundle.");
                return null;
            }

            // Give the bundle a unique name, based on the hash of the assets.
            // If two bundles are loaded with the same name (even with a different filename), Unity will fail to load the second one:
            // "The AssetBundle 'bundle' can't be loaded because another AssetBundle with the same files is already loaded."
            // By using the hash we hopefully make it possible to load two asset bundles containing the same assets (but different versions).
            // This would be desirable when overriding an internal asset.
            var hash = new Hash128();

            // Determine if this is a "shared" pack: a pack that OTHER packs depend on
            // Shared packs include their asset dependencies in the bundle
            // rr/shared always is: every pack requires it through rr/game, and references only resolve against
            // the shipped bundle under the same bundle name.
            var isSharedPack = Identifier == GamePackDependencyHelper.PackIdentifierShared
                || allPacks.Any(otherPack =>
                    otherPack.Identifier != this.Identifier &&
                    otherPack.RequiredPackIdentifiers().Contains(this.Identifier));

            if (isSharedPack)
            {
                Debug.Log($"Pack {Identifier} is a shared pack");
            }

            var packRoot = PathForAssetPackSource();
            var absorbsOutsidePack = Identifier == GamePackDependencyHelper.PackIdentifierShared;
            var otherPackRoots = allPacks
                .Where(otherPack => otherPack != this)
                .Select(otherPack => otherPack.PathForAssetPackSource())
                .ToList();

            foreach (var asset in bundleAssets.ToList())
            {
                hash.Append(AssetDatabase.GetAssetDependencyHash(asset).ToString());
                if (!isSharedPack)
                    continue;

                var deps = AssetDatabase.GetDependencies(asset, false)
                    .Where(path => BundleAssetRules.ShouldAbsorb(path, packRoot, absorbsOutsidePack, otherPackRoots))
                    .ToArray();
                Debug.Log($"Asset {asset} has {deps.Length} dependencies:\n{(string.Join("\n", deps))}");
                bundleAssets.AddRange(deps);
            }

            var assetNames = bundleAssets.Distinct().ToArray();
            ThrowOnAmbiguousNames(assetNames);
            if (!IsReferencePack)
                WarnOnModelEmbeddedMaterials(assetNames);

            var bundleName = isSharedPack ? Identifier : $"{Identifier}-{hash}";

            return new()
            {
                assetBundleName = bundleName,
                assetNames = assetNames
            };
        }

        private void ThrowOnAmbiguousNames(IEnumerable<string> assetNames)
        {
            var ambiguous = BundleAssetRules.FindAmbiguousNames(
                assetNames.Select(path => (path, AssetDatabase.GetMainAssetTypeAtPath(path))));
            if (ambiguous.Count > 0)
                throw new Exception($"Pack {Identifier} has assets of the same type sharing a name, so loading " +
                                    "by that name would pick one arbitrarily. Rename one of each pair:\n" +
                                    string.Join("\n", ambiguous.Select(pair => $"  {pair.PathA}\n  {pair.PathB}")));
        }

        /// <summary>
        /// A renderer left on a model's embedded material renders with the model importer's default shader
        /// (URP Lit), which the bundle then carries its own copy of, instead of the pack's car material.
        /// </summary>
        private void WarnOnModelEmbeddedMaterials(IEnumerable<string> assetNames)
        {
            foreach (var prefabPath in assetNames.Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                    continue;

                var uses = new List<string>();
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null)
                            continue;
                        var materialPath = AssetDatabase.GetAssetPath(material);
                        if (BundleAssetRules.IsModelFile(materialPath))
                            uses.Add($"  {renderer.name}: {material.name} ({Path.GetFileName(materialPath)})");
                    }
                }

                if (uses.Count > 0)
                    Debug.LogWarning($"Pack {Identifier}: {prefabPath} has renderers using materials embedded in a " +
                                     "model, so they render with the model's default shader rather than the pack's " +
                                     "materials. Remap them in the model's import settings (Materials > Search and " +
                                     "Remap), or assign the pack's materials on the renderers:\n" +
                                     string.Join("\n", uses));
            }
        }

        private static void BuildAssetBundles(string directory, AssetBundleBuild[] buildMap)
        {
            var manifest = BuildPipeline.BuildAssetBundles(directory, buildMap,
                BuildAssetBundleOptions.ForceRebuildAssetBundle
                | BuildAssetBundleOptions.ChunkBasedCompression // LZ4: lossless, smaller download, cheap on-demand decompress
                | BuildAssetBundleOptions.StrictMode,
                BuildTarget);
            if (manifest == null)
                throw new Exception("BuildAssetBundles returned null manifest");
        }

        private static BuildTarget BuildTarget
        {
            get
            {
#if UNITY_EDITOR_OSX
                return BuildTarget.StandaloneOSX;
#else
                return BuildTarget.StandaloneWindows;
#endif
            }
        }
        
        #endregion

    }
}