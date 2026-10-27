using UnityEngine.Rendering.Universal;

/// <summary>
/// Stub replacing Enviro's renderer feature so the synced URP renderer asset loads without errors.
/// The GUID on this file's .meta must match the main project's Enviro script GUID.
/// </summary>
public class EnviroURPRenderFeature : ScriptableRendererFeature
{
    public override void Create() { }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) { }
}
