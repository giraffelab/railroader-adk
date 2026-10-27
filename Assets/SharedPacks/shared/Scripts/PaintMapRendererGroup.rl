// PaintMapRendererGroup.rl - drives one or more renderer-group paint-map layers from a
// car's style.
//
// Generic: no car-specific knowledge. The car lists which style classes to drive via the
// ScriptComponent's `parameters` (parameters["styleClasses"], e.g. ["Renderer.body"]), an
// array so one attachment can coordinate several paint-map layers on the same car (e.g.
// body + underframe), each resolved and repainted independently. Attach via ScriptComponent
// (scriptAsset: "rr/PaintMapRendererGroup"). Repaints on model load and whenever the
// "style" property changes (livery pick or Customize-panel edit).

import log
import "rr/PaintMap" as paintMap

// The renderer-group style classes this component drives, e.g. ["Renderer.body"].
// Supplied per-car via the ScriptComponent parameters.
var styleClasses = parameters["styleClasses"]

var _styleObserver = null

// Repaint every renderer group carrying any of the configured style classes.
func repaint(car) {
    for styleClass in styleClasses {
        var style = car.resolveStyle(styleClass)
        for group in car.getComponentsForStyle(styleClass) {
            paintMap.applyPaint(group, style)
        }
    }
}

func onModelLoad(car) {
    if styleClasses == null || styleClasses.count == 0 {
        log.warning("PaintMapRendererGroup: add a 'styleClasses' parameter (array) to this ScriptComponent, or no renderer groups will be driven")
        return
    }
    repaint(car)
    _styleObserver = car.properties.observe("style", false, (changed) => repaint(car))
}

func onModelUnload(car) {
    _styleObserver?.dispose()
    _styleObserver = null
}
