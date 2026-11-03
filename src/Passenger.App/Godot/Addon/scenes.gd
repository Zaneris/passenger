@tool
extends RefCounted
## Passenger's addon for Godot's editor (MIT, Copyright (c) 2026 Ted L; written here by Passenger when asked): what an
## agent asks through Passenger of the scenes this editor edits. Each change is one action of the editor's own undo
## history for the scene, so the person undoes it here, and the scene's file is written only when they save it.

## The classes' own values of their properties, by class, made once, which a node's stored values are told apart from.
static var defaults := {}


## An answer's result, or why not in words.
static func asked(method: String, params: Dictionary) -> Variant:
	var scene := str(params.get("scene", ""))
	if scene != "":
		if not (scene.ends_with(".tscn") or scene.ends_with(".scn")) or not ResourceLoader.exists(scene):
			return "%s is no scene of the project" % scene
		var edited := EditorInterface.get_edited_scene_root()
		if edited == null or edited.scene_file_path != scene:
			EditorInterface.open_scene_from_path(scene)
	var root := EditorInterface.get_edited_scene_root()
	if root == null:
		return "Godot's editor edits no scene: open one"
	if scene != "" and root.scene_file_path != scene:
		return "Godot's editor did not open %s" % scene
	match method:
		"scene_open":
			return {"said": "Godot's editor edits %s" % root.scene_file_path, "scene": root.scene_file_path}
		"scene_read":
			return _read(root)
		"scene_select":
			return _select(root, str(params.get("node", ".")), bool(params.get("front", false)))
		"scene_change":
			return _change(root, params)
	return "Passenger's addon takes no %s" % method


static func _read(root: Node) -> Dictionary:
	var nodes := []
	var connections := []
	for node in _nodes(root):
		var entry := {"path": _path(root, node), "type": node.get_class(), "properties": _stored(node)}
		if node.get_script() != null:
			entry["script"] = node.get_script().resource_path
		if node != root and node.scene_file_path != "":
			entry["instance"] = node.scene_file_path
		nodes.append(entry)
		for connection in node.get_incoming_connections():
			var source = connection["signal"].get_object()
			if connection["flags"] & CONNECT_PERSIST and source is Node and _in(root, source):
				connections.append({"from": _path(root, source), "signal": str(connection["signal"].get_name()), "to": _path(root, node),
					"method": str(connection["callable"].get_method())})
	var selected := []
	for node in EditorInterface.get_selection().get_selected_nodes():
		if _in(root, node):
			selected.append(_path(root, node))
	var said := {"scene": root.scene_file_path, "nodes": nodes, "connections": connections, "selected": selected}
	if EditorInterface.has_method("get_unsaved_scenes"):
		said["unsaved"] = root.scene_file_path in EditorInterface.call("get_unsaved_scenes")
	return said


static func _select(root: Node, path: String, front: bool) -> Variant:
	var node = _node(root, path)
	if node is String:
		return node
	EditorInterface.get_selection().clear()
	EditorInterface.get_selection().add_node(node)
	EditorInterface.edit_node(node)
	if front:
		# The person's own ask from Passenger, who goes on here.
		var window := EditorInterface.get_base_control().get_window()
		window.grab_focus()
		DisplayServer.window_move_to_foreground(window.get_window_id())
	return {"said": "Selected %s in %s" % [_path(root, node), root.scene_file_path]}


static func _change(root: Node, params: Dictionary) -> Variant:
	var node = _node(root, str(params.get("node", ".")))
	if node is String:
		return node
	var undo := EditorInterface.get_editor_undo_redo()
	match str(params.get("action", "")):
		"set":
			return _set_properties(root, node, params.get("properties"), undo)
		"add":
			return _add(root, node, params, undo)
		"move":
			return _move(root, node, params, undo)
		"remove":
			return _remove(root, node, undo)
		"connect":
			return _connect(root, node, params, undo)
	return "%s is no change of a scene: set, add, move, remove or connect" % str(params.get("action", ""))


static func _set_properties(root: Node, node: Node, given: Variant, undo: EditorUndoRedoManager) -> Variant:
	var values = _values(node, given)
	if values is String:
		return values
	if values.is_empty():
		return "set names no property: properties gives each one's value"
	var names := ", ".join(PackedStringArray(values.keys()))
	undo.create_action("Passenger: set %s of %s" % [names, node.name], UndoRedo.MERGE_DISABLE, root)
	for name in values:
		undo.add_do_property(node, name, values[name])
		undo.add_undo_property(node, name, node.get(name))
	undo.commit_action()
	return {"said": "Set %s of %s" % [names, _path(root, node)], "node": _path(root, node)}


static func _add(root: Node, parent: Node, params: Dictionary, undo: EditorUndoRedoManager) -> Variant:
	var made = _made(str(params.get("type", "")), root)
	if made is String:
		return made
	if str(params.get("name", "")) != "":
		made.name = str(params["name"])
	var values = _values(made, params.get("properties")) if params.get("properties") is Dictionary else {}
	if values is String:
		made.free()
		return values
	for name in values:
		made.set(name, values[name])
	undo.create_action("Passenger: add %s" % made.name, UndoRedo.MERGE_DISABLE, root)
	undo.add_do_method(parent, "add_child", made, true)
	undo.add_do_method(made, "set_owner", root)
	if params.has("index"):
		undo.add_do_method(parent, "move_child", made, int(params["index"]))
	undo.add_do_reference(made)
	undo.add_undo_method(parent, "remove_child", made)
	undo.commit_action()
	return {"said": "Added %s (%s) under %s" % [_path(root, made), made.get_class(), _path(root, parent)], "node": _path(root, made)}


## A new node of a class, a global script class or a scene to instance (res://), or why not.
static func _made(type: String, root: Node) -> Variant:
	if type.begins_with("res://") or type.begins_with("uid://"):
		var packed = load(type) if ResourceLoader.exists(type) else null
		if not packed is PackedScene:
			return "%s is no scene to instance" % type
		if packed.resource_path == root.scene_file_path:
			return "%s cannot instance itself" % type
		return packed.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	if ClassDB.class_exists(type) and ClassDB.is_parent_class(type, "Node") and ClassDB.can_instantiate(type):
		return ClassDB.instantiate(type)
	for known in ProjectSettings.get_global_class_list():
		if known["class"] == type:
			var script = load(known["path"])
			if script is Script and ClassDB.is_parent_class(script.get_instance_base_type(), "Node"):
				var made: Node = ClassDB.instantiate(script.get_instance_base_type())
				made.set_script(script)
				return made
	return "%s is no node's class nor a res:// scene to instance" % type


static func _move(root: Node, node: Node, params: Dictionary, undo: EditorUndoRedoManager) -> Variant:
	if node == root:
		return "The scene's root moves nowhere"
	var to = _node(root, str(params.get("to", ""))) if str(params.get("to", "")) != "" else node.get_parent()
	if to is String:
		return to
	if to == node or node.is_ancestor_of(to):
		return "%s cannot go under itself" % _path(root, node)
	var from := node.get_parent()
	var at := node.get_index()
	var owned := _owned(root, node)
	undo.create_action("Passenger: move %s" % node.name, UndoRedo.MERGE_DISABLE, root)
	undo.add_do_method(from, "remove_child", node)
	undo.add_do_method(to, "add_child", node, true)
	if params.has("index"):
		undo.add_do_method(to, "move_child", node, int(params["index"]))
	# A node taken out of the tree lets go of its owner, which the scene's file needs.
	for each in owned:
		undo.add_do_method(each, "set_owner", root)
	undo.add_undo_method(to, "remove_child", node)
	undo.add_undo_method(from, "add_child", node, true)
	undo.add_undo_method(from, "move_child", node, at)
	for each in owned:
		undo.add_undo_method(each, "set_owner", root)
	undo.commit_action()
	return {"said": "Moved %s under %s" % [_path(root, node), _path(root, to)], "node": _path(root, node)}


static func _remove(root: Node, node: Node, undo: EditorUndoRedoManager) -> Variant:
	if node == root:
		return "The scene's root cannot be removed from it"
	var path := _path(root, node)
	var parent := node.get_parent()
	var at := node.get_index()
	var owned := _owned(root, node)
	undo.create_action("Passenger: remove %s" % node.name, UndoRedo.MERGE_DISABLE, root)
	undo.add_do_method(parent, "remove_child", node)
	undo.add_undo_method(parent, "add_child", node, true)
	undo.add_undo_method(parent, "move_child", node, at)
	for each in owned:
		undo.add_undo_method(each, "set_owner", root)
	undo.add_undo_reference(node)
	undo.commit_action()
	return {"said": "Removed %s" % path}


static func _connect(root: Node, node: Node, params: Dictionary, undo: EditorUndoRedoManager) -> Variant:
	var signal_name := str(params.get("signal", ""))
	if not node.has_signal(signal_name):
		return "%s (%s) has no signal %s" % [_path(root, node), node.get_class(), signal_name]
	var target = _node(root, str(params.get("to", ".")))
	if target is String:
		return target
	var method_name := str(params.get("method", ""))
	if method_name == "":
		return "connect names the method (method) the signal calls on to"
	var callable := Callable(target, method_name)
	if node.is_connected(signal_name, callable):
		return "%s's %s already calls %s on %s" % [_path(root, node), signal_name, method_name, _path(root, target)]
	undo.create_action("Passenger: connect %s to %s" % [signal_name, method_name], UndoRedo.MERGE_DISABLE, root)
	undo.add_do_method(node, "connect", signal_name, callable, CONNECT_PERSIST)
	undo.add_undo_method(node, "disconnect", signal_name, callable)
	undo.commit_action()
	var said := "Connected %s's %s to %s on %s" % [_path(root, node), signal_name, method_name, _path(root, target)]
	if not _has_method(target, method_name):
		said += "; %s has no method %s yet, which the signal calls once it has" % [_path(root, target), method_name]
	return {"said": said}


## The values given for a node's properties, each read as a .tscn writes it, or why one cannot be.
static func _values(target: Object, given: Variant) -> Variant:
	var values := {}
	if not given is Dictionary:
		return values
	var types := {}
	for property in target.get_property_list():
		types[property["name"]] = property["type"]
	for key in given:
		var name := str(key)
		if not types.has(name):
			return "%s has no property %s" % [target.get_class(), name]
		var read = _parsed(str(given[key]), types[name])
		if read is String:
			return "%s: %s" % [name, read]
		values[name] = read[0]
	return values


## A value as a .tscn writes it, for a property of a type, in an array of one; or why it is none.
static func _parsed(text: String, type: int) -> Variant:
	if type == TYPE_OBJECT:
		if text == "null":
			return [null]
		if (text.begins_with("res://") or text.begins_with("uid://")) and ResourceLoader.exists(text):
			return [load(text)]
		return "takes a resource by its res:// path, or null, not %s" % text
	var words := [TYPE_STRING, TYPE_STRING_NAME, TYPE_NODE_PATH]
	if type in words and not (text.begins_with("\"") or text.begins_with("&\"") or text.begins_with("^\"")):
		return [type_convert(text, type)]
	var value = str_to_var(text)
	if typeof(value) == TYPE_NIL and text.strip_edges() != "null":
		return "%s is no value as a .tscn writes one (12, 0.5, true, \"text\", Vector2(4, 8), Color(1, 0, 0, 1))" % text
	var numbers := [TYPE_BOOL, TYPE_INT, TYPE_FLOAT]
	if type != TYPE_NIL and typeof(value) != type:
		if (type in numbers and typeof(value) in numbers) or (type in words and typeof(value) in words):
			value = type_convert(value, type)
		else:
			return "takes a %s, not %s" % [type_string(type), text]
	return [value]


## The values a node holds that its scene's file stores: those not its class's or script's own.
static func _stored(node: Node) -> Dictionary:
	var stored := {}
	var script: Script = node.get_script()
	var scripted := {}
	if script != null:
		for property in script.get_script_property_list():
			scripted[property["name"]] = true
	for property in node.get_property_list():
		var name: String = property["name"]
		if property["usage"] & PROPERTY_USAGE_STORAGE == 0 or name == "script":
			continue
		var value = node.get(name)
		var own = script.get_property_default_value(name) if scripted.has(name) else _default(node.get_class(), name)
		if typeof(value) == typeof(own) and value == own:
			continue
		stored[name] = _text(value)
	return stored


static func _default(type: String, name: String) -> Variant:
	if not defaults.has(type):
		var values := {}
		var made = ClassDB.instantiate(type)
		if made is Object:
			for property in made.get_property_list():
				values[property["name"]] = made.get(property["name"])
			if made is Node:
				made.free()
		defaults[type] = values
	return defaults[type].get(name)


static func _text(value: Variant) -> String:
	if value is Resource:
		return value.resource_path if value.resource_path != "" and not value.resource_path.contains("::") else "%s (in the scene)" % value.get_class()
	if value is Object:
		return value.get_class()
	return var_to_str(value)


## The scene's own nodes, the root first, in the tree's order: not those of a scene it instances.
static func _nodes(root: Node) -> Array:
	var found := [root]
	for node in root.find_children("*", "", true, false):
		if node.owner == root:
			found.append(node)
	return found


## A node and those below it the scene owns, whose owner a change that takes them out of the tree sets again.
static func _owned(root: Node, node: Node) -> Array:
	var owned := [node]
	for below in node.find_children("*", "", true, false):
		if below.owner == root:
			owned.append(below)
	return owned


static func _in(root: Node, node: Node) -> bool:
	return node == root or (root.is_ancestor_of(node) and node.owner == root)


static func _path(root: Node, node: Node) -> String:
	return "." if node == root else str(root.get_path_to(node))


## A node of the scene by its path from the root (. the root), or why not.
static func _node(root: Node, path: String) -> Variant:
	var node: Node = root if path == "" or path == "." else root.get_node_or_null(NodePath(path))
	if node == null:
		return "%s has no node %s" % [root.scene_file_path, path]
	if not _in(root, node):
		var instance := node.owner
		return "%s belongs to %s, which the scene instances: change it there" % [path, instance.scene_file_path if instance != null else "another scene"]
	return node


static func _has_method(node: Node, name: String) -> bool:
	if ClassDB.class_has_method(node.get_class(), name):
		return true
	var script: Script = node.get_script()
	while script != null:
		for method in script.get_script_method_list():
			if method["name"] == name:
				return true
		script = script.get_base_script()
	return false
