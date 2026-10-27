using System;
using System.IO;
using System.Linq;

namespace AssetPack.Editor
{
    public static class DirectoryUtilities
    {
        /// <summary>
        /// Predicate that ignores dot files and .meta files for JSON files (Info.json.meta, Definitions.json.meta, etc.)
        /// but keeps .meta files for assets (prefabs, materials, etc.)
        /// </summary>
        public static readonly Func<string, bool> IgnoreDotFilesAndJsonMeta = name =>
            !name.StartsWith(".") && !(name.EndsWith(".json.meta", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Predicate that ignores dot files and every .meta file. For copies whose destination is a
        /// shipped pack rather than a Unity project: a .meta there is project bookkeeping the game
        /// never reads.
        /// </summary>
        public static readonly Func<string, bool> IgnoreDotFilesAndMeta = name =>
            !name.StartsWith(".") && !name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Recursively copies a directory and its contents to a destination path, overwriting existing files
        /// and removing files/directories in destination that don't exist in source, using the specified filter.
        /// </summary>
        /// <param name="sourcePath">The source directory path to copy from</param>
        /// <param name="destPath">The destination directory path to copy to</param>
        /// <param name="includeItem">Predicate to determine if a file or directory should be included (null for no filtering)</param>
        /// <exception cref="ArgumentException">Thrown when paths are null, empty, or invalid</exception>
        /// <exception cref="DirectoryNotFoundException">Thrown when source directory doesn't exist</exception>
        public static void OverwriteDirectoryRecursively(string sourcePath, string destPath,
            Func<string, bool> includeItem = null)
        {
            // If no filter is provided, include everything
            includeItem ??= (_ => true);

            // Validate parameters
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be null or empty.", nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("Destination path cannot be null or empty.", nameof(destPath));

            // Ensure source exists
            if (!Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException($"Source directory not found: {sourcePath}");

            // Create destination directory if it doesn't exist
            if (!Directory.Exists(destPath))
                Directory.CreateDirectory(destPath);

            // Get source and destination files (applying filter)
            var sourceFiles = Directory.GetFiles(sourcePath)
                .Select(Path.GetFileName)
                .Where(name => includeItem(name))
                .ToHashSet();

            var destFiles = Directory.GetFiles(destPath)
                .Select(Path.GetFileName)
                .Where(name => includeItem(name))
                .ToHashSet();

            // Delete files in destination that don't exist in source
            foreach (var fileName in destFiles.Except(sourceFiles))
            {
                var filePath = Path.Combine(destPath, fileName);
                File.Delete(filePath);
            }

            // Copy all filtered files from source
            foreach (var filePath in Directory.GetFiles(sourcePath))
            {
                var fileName = Path.GetFileName(filePath);
                if (!includeItem(fileName)) continue;
                
                var destFile = Path.Combine(destPath, fileName);
                File.Copy(filePath, destFile, true);
            }

            // Get source and destination directories (applying filter)
            var sourceDirs = Directory.GetDirectories(sourcePath)
                .Select(Path.GetFileName)
                .Where(name => includeItem(name))
                .ToHashSet();

            var destDirs = Directory.GetDirectories(destPath)
                .Select(Path.GetFileName)
                .Where(name => includeItem(name))
                .ToHashSet();

            // Delete directories in destination that don't exist in source
            foreach (var dirName in destDirs.Except(sourceDirs))
            {
                var dirPath = Path.Combine(destPath, dirName);
                Directory.Delete(dirPath, true);
            }

            // Process each filtered subdirectory
            foreach (var dirPath in Directory.GetDirectories(sourcePath))
            {
                var dirName = Path.GetFileName(dirPath);
                if (!includeItem(dirName)) continue;
                
                var destDir = Path.Combine(destPath, dirName);
                OverwriteDirectoryRecursively(dirPath, destDir, includeItem);
            }
        }
    }
}