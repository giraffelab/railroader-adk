namespace AssetPack.Common.Dependencies
{
    /// <summary>
    /// Represents a node in the dependency graph for validation and sorting.
    /// Implemented by both real asset packs (AssetPackInfo) and virtual dependencies.
    /// </summary>
    public interface IDependencyNode
    {
        string Identifier { get; }
        string Version { get; }
        AssetPackDependencies Dependencies { get; }

        /// <summary>
        /// Optional load-order hints. May be <c>null</c> when the node declares none.
        /// </summary>
        AssetPackLoadOrder Load { get; }
    }
}
