// PaintMap.rl - shared paint-map material helpers for car body paint (RendererGroup) and
// image/herald decals (Decal). Each function takes a component proxy plus a resolved
// style and drives its paint-map shader.
//
// Most cars can drive this from the generic wiring scripts in this folder,
// PaintMapRendererGroup.rl and PaintMapDecal.rl, by attaching them via a ScriptComponent
// and listing style classes in its `parameters`. Write your own wiring script only if a
// car needs to coordinate multiple paint-map layers in a way those generic drivers don't
// support.

from railroader import MaterialConfiguration

// Default paint for an unset zone.
var white = "#FFFFFF"

// Drives a renderer-group component's paint-map shader from a resolved style. `group` is
// a component proxy (from getComponentsForStyle); `group.material` is its material facet.
// A null style (or one with no image) restores default coloring.
//
// Each zone is one paint-material string ("#hex" or "metallic(#hex, m)"), applied via
// setColorParamsUsingStyle, the same flexible value the paint-map decals use.
export func applyPaint(group, style) {
    if style == null || style["image"] == null {
        group.setAutomaticStyleColorEnabled(true)
        group.material.configure(MaterialConfiguration.carStandard)
        return
    }

    // Paint active: take over from the default color system. The control map assigns
    // body areas to zones; each zone has a color and a 0..1 metallic.
    group.setAutomaticStyleColorEnabled(false)
    var material = group.material
    material.configure(MaterialConfiguration.carPaintMap)
    material.setTexture("_PaintControlMap", style["image"])
    material.setColorParamsUsingStyle("_BaseColor", "_PaintParamsBase", style["color"] ?? white)
    material.setColorParamsUsingStyle("_PaintColor1", "_PaintParams1", style["color1"] ?? white)
    material.setColorParamsUsingStyle("_PaintColor2", "_PaintParams2", style["color2"] ?? white)
    material.setColorParamsUsingStyle("_PaintColor3", "_PaintParams3", style["color3"] ?? white)
    material.setFloat("_PaintEdgeSoftness", 0.02)
}

// Drives a decal's paint-map material from a resolved style: the control-map image plus
// up to four zone colors, each a paint-material string ("#hex" or "metallic(#hex, m)"). A
// null style (or one with no image) hides the decal: that livery has no decal of this
// kind.
export func applyPaintMapDecal(decal, style) {
    if style == null || style["image"] == null {
        decal.enabled = false
        return
    }
    decal.enabled = true

    var material = decal.material
    material.configure(MaterialConfiguration.decalPaintMap)
    material.setTexture("_ControlMap", style["image"])
    material.setColorParamsUsingStyle("_Color0", "_Params0", style["color"] ?? white)
    material.setColorParamsUsingStyle("_Color1", "_Params1", style["color1"] ?? white)
    material.setColorParamsUsingStyle("_Color2", "_Params2", style["color2"] ?? white)
    material.setColorParamsUsingStyle("_Color3", "_Params3", style["color3"] ?? white)
    material.setFloat("_EdgeSoftness", 0.02)
    material.setFloat("_MatteSmoothness", 0.1)
}
