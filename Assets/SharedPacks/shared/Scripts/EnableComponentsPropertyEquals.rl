// EnableComponentsPropertyEquals
// Enables the given components when a property equals the given value.

var componentNames = parameters["components"]
var propertyKey = parameters["key"]
var expectedValue = parameters["value"]
var defaultEnabled = parameters["defaultEnabled"] == true

var observer = null

func onModelLoad(car) {
    // Grab the components we want to control
    var components = componentNames
        .map((name) => car.components[name])
        .filter((c) => c != null)

    if car.properties[propertyKey] == null && defaultEnabled {
        car.properties[propertyKey] = expectedValue
    }

    // Watch the property and turn components on/off
    observer = car.properties.observe(propertyKey, true, (value) => {
        var shouldEnable = (value == expectedValue)

        for comp in components {
            comp.enabled = shouldEnable
        }
    })
}

func onModelUnload(car) {
    observer?.dispose()
    observer = null
}
