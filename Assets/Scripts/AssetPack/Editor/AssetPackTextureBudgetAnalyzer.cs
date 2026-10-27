using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetPack.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetPack.Editor
{
    /// <summary>
    /// Analyzes texture memory usage across asset packs and reports budget compliance.
    /// Measures compressed texture data size (GetRawTextureData byte length) which represents
    /// the GPU memory footprint including mipmaps but after compression.
    /// </summary>
    public static class AssetPackTextureBudgetAnalyzer
    {
        // JSON field names from Definitions.json schema
        private const string JsonFieldObjects = "objects";
        private const string JsonFieldDefinition = "definition";
        private const string JsonFieldKind = "kind";
        private const string JsonFieldCarType = "carType";

        // Definition kind constants
        private const string DefinitionKindCar = "Car";

        public class PackTextureAnalysis
        {
            public string Identifier { get; set; }
            public long TextureMemoryBytes { get; set; }
            public Dictionary<string, int> DefinitionCounts { get; set; } = new Dictionary<string, int>();
            public int TotalDefinitions { get; set; }
        }

        /// <summary>
        /// Texture memory budgets per definition type (in MB).
        /// For cars, supports specific budgets by car type (e.g., "Car:PB" for passenger cars).
        /// Falls back to "Car" for car types without specific budgets.
        /// Based on analysis of existing asset packs.
        /// </summary>
        private static readonly Dictionary<string, long> BudgetsPerDefinition = new Dictionary<string, long>
        {
            { "DieselLocomotive", 100 * 1024 * 1024 },      // 100 MB per diesel locomotive
            { "SteamLocomotive", 100 * 1024 * 1024 },       // 100 MB per steam locomotive
            { "Car:PB", 80 * 1024 * 1024 },                 // 80 MB per passenger car (coaches, etc.)
            { "Car", 50 * 1024 * 1024 },                    // 50 MB per car (default for freight)
            { "Truck", 20 * 1024 * 1024 },                  // 20 MB per truck
            { "Prop", 10 * 1024 * 1024 },                   // 10 MB per prop
            { "Material", 10 * 1024 * 1024 },               // 10 MB per material
            { "StaticLoadModel", 10 * 1024 * 1024 },        // 10 MB per static load
            { "LinearLoadModel", 10 * 1024 * 1024 },        // 10 MB per linear load
            { "Whistle", 1 * 1024 * 1024 },                 // 1 MB per whistle
            { "Horn", 1 * 1024 * 1024 },                    // 1 MB per horn
            { "Texture", 1 * 1024 * 1024 },                 // 1 MB per texture
        };

        public static PackTextureAnalysis AnalyzePackTextures(AssetPackEditorStore store, string buildPath)
        {
            var analysis = new PackTextureAnalysis
            {
                Identifier = store.Identifier
            };

            // Analyze textures in the pack's source assets
            var packPath = store.SourcePath;

            if (Directory.Exists(packPath))
            {
                var textureGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { packPath });
                long totalMemory = 0;

                foreach (var guid in textureGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (texture != null)
                    {
                        try
                        {
                            var pixelData = texture.GetRawTextureData();
                            totalMemory += pixelData.Length;
                        }
                        catch
                        {
                            // Texture not readable, skip it
                        }
                    }
                }

                analysis.TextureMemoryBytes = totalMemory;
            }

            ExtractDefinitionCounts(store, buildPath, analysis);

            return analysis;
        }

        private static void ExtractDefinitionCounts(AssetPackEditorStore store, string buildPath, PackTextureAnalysis analysis)
        {
            // Count definitions from Definitions.json
            var definitionsPath = Path.Combine(buildPath, Filenames.Definitions);
            if (!File.Exists(definitionsPath))
                return;

            try
            {
                var json = File.ReadAllText(definitionsPath);
                var root = JObject.Parse(json);

                var objects = root[JsonFieldObjects] as JArray;
                if (objects == null)
                    return;

                analysis.TotalDefinitions = objects.Count;
                foreach (var obj in objects)
                {
                    var definition = obj[JsonFieldDefinition];
                    if (definition == null)
                        continue;

                    var kind = definition[JsonFieldKind]?.ToString();
                    if (string.IsNullOrEmpty(kind))
                        continue;

                    // For Car definitions, create composite key with carType
                    string budgetKey = kind;
                    if (kind == DefinitionKindCar)
                    {
                        var carType = definition[JsonFieldCarType]?.ToString();
                        if (!string.IsNullOrEmpty(carType))
                            budgetKey = $"{kind}:{carType}";
                    }

                    if (!analysis.DefinitionCounts.ContainsKey(budgetKey))
                        analysis.DefinitionCounts[budgetKey] = 0;
                    analysis.DefinitionCounts[budgetKey]++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to parse Definitions.json for {store.Identifier}: {ex.Message}");
            }
        }

        public static void GenerateTextureBudgetReport(List<PackTextureAnalysis> analyses)
        {
            if (analyses.Count == 0)
                return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== TEXTURE BUDGET REPORT ===");
            sb.AppendLine();

            // Calculate total memory
            var totalMemory = analyses.Sum(a => a.TextureMemoryBytes);
            sb.AppendLine($"Total texture memory across all packs: {FormatBytes(totalMemory)}");
            sb.AppendLine();

            // Categorize packs as over-budget or within-budget (ignore packs with no definitions)
            var overBudgetPacks = new List<(PackTextureAnalysis analysis, long budget, long overage)>();
            var withinBudgetPacks = new List<(PackTextureAnalysis analysis, long budget)>();
            var packsWithoutDefinitions = new List<PackTextureAnalysis>();

            foreach (var analysis in analyses)
            {
                if (analysis.TotalDefinitions == 0)
                {
                    packsWithoutDefinitions.Add(analysis);
                    continue;
                }

                var budget = CalculatePackBudget(analysis);
                var overage = analysis.TextureMemoryBytes - budget;

                if (overage > 0)
                    overBudgetPacks.Add((analysis, budget, overage));
                else
                    withinBudgetPacks.Add((analysis, budget));
            }

            // Display over-budget packs first
            if (overBudgetPacks.Count > 0)
            {
                sb.AppendLine($"⚠️  OVER BUDGET ({overBudgetPacks.Count} packs):");
                sb.AppendLine();

                // Sort by overage amount descending
                foreach (var item in overBudgetPacks.OrderByDescending(x => x.overage))
                {
                    var analysis = item.analysis;
                    var definitionSummary = analysis.TotalDefinitions > 0
                        ? $" | {string.Join(", ", analysis.DefinitionCounts.Select(kv => $"{kv.Value} {kv.Key}"))}"
                        : " | No definitions";

                    sb.AppendLine($"  {analysis.Identifier,-30} {FormatBytes(analysis.TextureMemoryBytes),10} (Over {FormatBytes(item.budget),8} by {FormatBytes(item.overage),7}){definitionSummary}");
                }
                sb.AppendLine();
            }

            // Display within-budget packs
            sb.AppendLine($"✓ Within Budget ({withinBudgetPacks.Count} packs):");
            sb.AppendLine();

            // Sort by texture size descending
            var maxMemory = analyses.Max(a => a.TextureMemoryBytes);
            foreach (var item in withinBudgetPacks.OrderByDescending(x => x.analysis.TextureMemoryBytes))
            {
                var analysis = item.analysis;
                var barLength = maxMemory > 0 ? (int)((analysis.TextureMemoryBytes / (double)maxMemory) * 40) : 0;
                var bar = new string('█', barLength);

                var definitionSummary = analysis.TotalDefinitions > 0
                    ? $" | {string.Join(", ", analysis.DefinitionCounts.Select(kv => $"{kv.Value} {kv.Key}"))}"
                    : " | No definitions";

                var budgetUsagePercent = item.budget > 0 ? (analysis.TextureMemoryBytes / (double)item.budget * 100) : 0;

                sb.AppendLine($"  {analysis.Identifier,-40} {FormatBytes(analysis.TextureMemoryBytes),10} ({budgetUsagePercent:F0}% of {FormatBytes(item.budget),10}) {bar}{definitionSummary}");
            }
            sb.AppendLine();

            // Calculate averages per definition type
            sb.AppendLine("--- Budgets and Averages per Definition Type ---");
            var definitionTypeStats = new Dictionary<string, (long totalMemory, int count)>();

            foreach (var analysis in analyses)
            {
                if (analysis.TotalDefinitions == 0)
                    continue;

                foreach (var kv in analysis.DefinitionCounts)
                {
                    var kind = kv.Key;
                    var count = kv.Value;
                    // Allocate memory proportionally
                    var memoryPerDef = analysis.TextureMemoryBytes / analysis.TotalDefinitions;

                    if (!definitionTypeStats.ContainsKey(kind))
                        definitionTypeStats[kind] = (0, 0);

                    var stats = definitionTypeStats[kind];
                    definitionTypeStats[kind] = (stats.totalMemory + memoryPerDef * count, stats.count + count);
                }
            }

            foreach (var kv in definitionTypeStats.OrderByDescending(kv => kv.Value.totalMemory / kv.Value.count))
            {
                var avgMemory = kv.Value.totalMemory / kv.Value.count;
                var budget = GetBudgetForKey(kv.Key);
                var budgetStr = FormatBytes(budget);
                sb.AppendLine($"  {kv.Key,-30} Budget: {budgetStr,10}  |  Avg: {FormatBytes(avgMemory),10} ({kv.Value.count} samples)");
            }

            // Display packs without definitions (not subject to budgets)
            if (packsWithoutDefinitions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Packs Without Definitions ({packsWithoutDefinitions.Count} packs, not subject to budgets) ---");
                foreach (var analysis in packsWithoutDefinitions.OrderByDescending(a => a.TextureMemoryBytes))
                {
                    sb.AppendLine($"  {analysis.Identifier,-40} {FormatBytes(analysis.TextureMemoryBytes),10}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== END TEXTURE BUDGET REPORT ===");

            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Looks up budget for a definition key, with fallback to base kind for composite keys.
        /// E.g., "Car:PB" -> tries "Car:PB" first, then falls back to "Car"
        /// </summary>
        private static long GetBudgetForKey(string budgetKey)
        {
            // Try specific budget first (e.g., "Car:PB")
            if (BudgetsPerDefinition.TryGetValue(budgetKey, out var budget))
                return budget;

            // Fall back to base kind if composite key (e.g., "Car:XM" -> "Car")
            if (budgetKey.Contains(":"))
            {
                var baseKind = budgetKey.Split(':')[0];
                if (BudgetsPerDefinition.TryGetValue(baseKind, out budget))
                    return budget;
            }

            // Unknown definition type - use a default budget of 50 MB
            return 50 * 1024 * 1024;
        }

        private static long CalculatePackBudget(PackTextureAnalysis analysis)
        {
            long totalBudget = 0;

            foreach (var kv in analysis.DefinitionCounts)
            {
                var budgetKey = kv.Key;
                var count = kv.Value;
                totalBudget += GetBudgetForKey(budgetKey) * count;
            }

            return totalBudget;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }
    }
}
