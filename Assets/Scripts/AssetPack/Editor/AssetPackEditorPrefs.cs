using UnityEditor;

namespace AssetPack.Editor
{
    public static class AssetPackEditorPrefs
    {
        private static class Keys
        {
            public const string LibraryPath = "AssetPackLibraryPath";
            public const string BuildPath = "AssetPackBuildPath";
        }

        public static string LibraryPath
        {
            get => EditorPrefs.GetString(Keys.LibraryPath);
            set => EditorPrefs.SetString(Keys.LibraryPath, value);
        }
        
        public static string BuildPath
        {
            get => EditorPrefs.GetString(Keys.BuildPath);
            set => EditorPrefs.SetString(Keys.BuildPath, value);
        }
    }
}