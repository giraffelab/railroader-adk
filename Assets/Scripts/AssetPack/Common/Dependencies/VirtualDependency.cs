namespace AssetPack.Common.Dependencies
{
    /// <summary>
    /// Represents a virtual dependency target that has no actual asset pack store.
    /// Used for dependencies like "rr/game" that represent the game itself.
    /// </summary>
    public class VirtualDependency : IDependencyNode
    {
        public string Identifier { get; }
        public string Version { get; }
        public AssetPackDependencies Dependencies { get; }

        /// <summary>Virtual dependencies carry no load-order hints.</summary>
        public AssetPackLoadOrder Load => null;

        public VirtualDependency(string identifier, string version, AssetPackDependencies dependencies = null)
        {
            Identifier = identifier;
            Version = version;
            Dependencies = dependencies;
        }
    }
}
