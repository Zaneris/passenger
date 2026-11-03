## Passenger's reading of the engine's classes, run headless by Passenger with the file to write as its one argument: for
## each class a scene's nodes and resources may be, what it extends, its own properties (name, Variant type, hint, hint
## string, usage), and the default of each property it has, its parents' too, where it differs from its parent's.
extends SceneTree


func _init() -> void:
	var out: String = OS.get_cmdline_user_args()[0]
	var classes := {}
	var defaults := {}
	var names := Array(ClassDB.get_class_list())
	names.sort()
	for name in names:
		if not (ClassDB.is_parent_class(name, "Node") or ClassDB.is_parent_class(name, "Resource")):
			continue
		var own := []
		for p in ClassDB.class_get_property_list(name, true):
			own.append([p.name, p.type, p.hint, p.hint_string, p.usage])
		classes[name] = {"inherits": ClassDB.get_parent_class(name), "properties": own, "defaults": {}}
	for name in names:
		if not classes.has(name):
			continue
		var all := {}
		if ClassDB.can_instantiate(name):
			for p in ClassDB.class_get_property_list(name):
				if p.usage & (PROPERTY_USAGE_CATEGORY | PROPERTY_USAGE_GROUP | PROPERTY_USAGE_SUBGROUP) == 0:
					all[p.name] = var_to_str(ClassDB.class_get_property_default_value(name, p.name))
		defaults[name] = all
	for name in classes:
		var parent: String = classes[name].inherits
		var above: Dictionary = defaults.get(parent, {})
		var differ := {}
		for key in defaults[name]:
			if not above.has(key) or above[key] != defaults[name][key]:
				differ[key] = defaults[name][key]
		classes[name].defaults = differ
	var file := FileAccess.open(out, FileAccess.WRITE)
	file.store_string(JSON.stringify(classes))
	file.close()
	quit()
