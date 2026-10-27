// PaintMapDecal.rl - drives one or more paint-map image decals (e.g. heralds) from a
// car's style.
//
// Generic: no car-specific knowledge. The car lists which decal style classes to drive via
// the ScriptComponent's `parameters` (parameters["styleClasses"], e.g. ["Decal.herald"]),
// an array so one attachment can coordinate several decal layers on the same car, each
// resolved and repainted independently. Attach via ScriptComponent
// (scriptAsset: "rr/PaintMapDecal"). Repaints on model load and whenever the "style"
// property changes (livery pick or Customize-panel edit).

import log
import "rr/PaintMap" as paintMap

// The decal style classes this component drives, e.g. ["Decal.herald"] (element-qualified,
// as resolveStyle/getComponentsForStyle expect). Supplied per-car via the ScriptComponent
// parameters.
var styleClasses = parameters["styleClasses"]

var _styleObserver = null

// Repaint every decal carrying any of the configured style classes.
func repaint(car) {
    for styleClass in styleClasses {
        var style = car.resolveStyle(styleClass)
        for decal in car.getComponentsForStyle(styleClass) {
            paintMap.applyPaintMapDecal(decal, style)
        }
    }
}

func onModelLoad(car) {
    if styleClasses == null || styleClasses.count == 0 {
        log.warning("PaintMapDecal: add a 'styleClasses' parameter (array) to this ScriptComponent, or no decals will be driven")
        return
    }
    repaint(car)
    _styleObserver = car.properties.observe("style", false, (changed) => repaint(car))
}

func onModelUnload(car) {
    _styleObserver?.dispose()
    _styleObserver = null
}
