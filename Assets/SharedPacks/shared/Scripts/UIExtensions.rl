// Shared helpers for property-driven UI dropdowns.

// Adds a dropdown that reads/writes a property.
// `options` is an array of [label, value] pairs.
// An unset property shows the first entry, so the model must treat a missing value as the first option.
export func fieldPropertyDropdown(builder, properties, label, key, options, onChange = null) {
    assert(builder != null, "fieldPropertyDropdown: builder is null")
    assert(properties != null, "fieldPropertyDropdown: properties is null")
    assert(key != null, "fieldPropertyDropdown: key is null")
    assert(options != null && options.count > 0, "fieldPropertyDropdown: options must be a non-empty array")

    builder.rebuildOnPropertyChange(properties, key)

    let labels = options.map((o) => o[0])
    let values = options.map((o) => o[1])
    let current = properties[key] ?? values[0]

    var idx = values.indexOf(current)
    if idx < 0 {
        // Show the real value rather than writing a default: building the panel must not change the car.
        labels.insertAt(0, "Unavailable (\(current))")
        values.insertAt(0, current)
        idx = 0
    }

    builder.fieldDropdown(label, labels, idx, (selected) => {
        properties[key] = values[selected]
        if onChange != null { onChange() }
    })
}
