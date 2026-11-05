extends Node
## Passenger's helper in a game it debugs: where the nodes are, input given as a person gives it, the frames the game
## has run, and the values of its scripts' members. Each call comes through asked, its first argument its ask, answered by
## a passenger:said message of the ask, whether it was done, and what it came to.

const SAID := "passenger:said"
const GONE := "passenger:gone"
const SHIFT := 1
const CTRL := 2
const ALT := 4
const META := 8

## The frames the game has run, once counted (-1 before): every frame its tree processes, the game's own pause among them.
var _frame := -1
## The nodes running each script by its path (a script's base scripts' paths too), each by its id: made once a values call
## first asks, then kept as nodes come and go.
var _scripted = {}
var _indexed = false
## The ask a frame stepped answers, or 0.
var _stepping := 0
## Whether the frame stepped ends the step itself, where no frame is drawn for Godot to end it at.
var _ends_step := false
## Whether a headless game's window was given its size, which the first call gives it.
var _sized := false
## Where in the viewport the last press this helper gave landed, which a pick that found nothing is looked at again at.
var _pressed_at := Vector2(INF, INF)
## The key, and its modifiers, that hands the keys back to Passenger while the game holds them in its Game window (KEY_NONE
## for none).
var _keys_back := KEY_NONE
var _keys_back_modifiers := 0
## The last member, so it is set only once every other member's default is (members are made in the order written).
var _made = true


## Every call Passenger makes, by its name, its ask and its other arguments. Godot reads the debugger's messages between a
## script's lines, its members' defaults among them, so a call can come while those are being made: it waits until they
## are, or the defaults still to come would overwrite what it set.
func asked(named: String, ask: int, arguments: Array) -> void:
    if not _made:
        asked.call_deferred(named, ask, arguments)
        return
    if not _sized:
        _sized = true
        _size_headless()
    callv(named, [ask] + arguments)


## A game run headless lays out in the size its project gives its window, where the headless display gives it 64 by 64, so
## what a person would see of it is in view, and where it would be.
func _size_headless() -> void:
    if DisplayServer.get_name() != "headless":
        return
    var size := Vector2i(int(ProjectSettings.get_setting("display/window/size/viewport_width", 1152)), int(ProjectSettings.get_setting("display/window/size/viewport_height", 648)))
    var over := Vector2i(int(ProjectSettings.get_setting("display/window/size/window_width_override", 0)), int(ProjectSettings.get_setting("display/window/size/window_height_override", 0)))
    get_tree().root.size = over if over.x > 0 and over.y > 0 else size


## The keys that hand the keys back to Passenger, heard in every input the game takes, before the game's own.
func keys_back(ask: int, key: String, modifiers: int) -> void:
    _keys_back = OS.find_keycode_from_string(key)
    _keys_back_modifiers = modifiers
    _say(ask, _keys_back != KEY_NONE, [] if _keys_back != KEY_NONE else ["Godot has no key %s" % key])


## A game whose project draws at its window's size in pixels laid out at the project's size and scaled to its window, so it
## shows whole however small the Game window holds it. One that set its own stretch by now keeps it.
func scale_to_window(ask: int) -> void:
    var root := get_tree().root
    if root.content_scale_mode == Window.CONTENT_SCALE_MODE_DISABLED:
        root.content_scale_size = Vector2i(int(ProjectSettings.get_setting("display/window/size/viewport_width", 1152)), int(ProjectSettings.get_setting("display/window/size/viewport_height", 648)))
        root.content_scale_aspect = Window.CONTENT_SCALE_ASPECT_KEEP
        root.content_scale_mode = Window.CONTENT_SCALE_MODE_CANVAS_ITEMS
    _say(ask, true, [root.content_scale_mode, root.content_scale_size.x, root.content_scale_size.y])


func _input(event: InputEvent) -> void:
    var key := event as InputEventKey
    if _keys_back == KEY_NONE or key == null or not key.pressed or key.echo or key.keycode != _keys_back:
        return
    var modifiers := (SHIFT if key.shift_pressed else 0) | (CTRL if key.ctrl_pressed else 0) | (ALT if key.alt_pressed else 0) | (META if key.meta_pressed else 0)
    if modifiers == _keys_back_modifiers:
        get_viewport().set_input_as_handled()
        EngineDebugger.send_message("passenger:keys", [])


## Said as the helper leaves the game's tree, freed or taken out: what it was asked and has yet to answer, it never will.
func _exit_tree() -> void:
    EngineDebugger.send_message(GONE, [])


func _say(ask: int, done: bool, data: Array) -> void:
    EngineDebugger.send_message(SAID, [ask, done] + data)


func _node(id: int) -> Node:
    var found = instance_from_id(id)
    return found if found is Node else null


## Whether a node aimed at has gone, freed or out of the tree, which is said: input aimed at it reaches nothing else. An id of
## 0 aims at none.
func _gone(ask: int, id: int) -> bool:
    if id == 0:
        return false
    var node := _node(id)
    if node != null and node.is_inside_tree():
        return false
    _say(ask, false, ["the node is gone"])
    return true


func _box(node: Node) -> Array:
    if node is Control:
        var rect: Rect2 = node.get_global_rect()
        return [true, rect.position.x, rect.position.y, rect.size.x, rect.size.y]
    if node is Window:
        if node == get_tree().root:
            var seen: Vector2 = node.get_visible_rect().size
            return [true, 0.0, 0.0, seen.x, seen.y]
        return [true, float(node.position.x), float(node.position.y), float(node.size.x), float(node.size.y)]
    if node is Node2D:
        var at: Vector2 = node.global_position
        return [true, at.x, at.y, 0.0, 0.0]
    return [false, 0.0, 0.0, 0.0, 0.0]


func _text(node: Node) -> Variant:
    if node == null:
        return null
    if node is Window:
        return node.title
    if node is LineEdit and node.secret:
        return "•".repeat(node.text.length())
    if "text" in node:
        var text = node.get("text")
        if text is String:
            return text
    return null


func _disabled(node: Node) -> bool:
    return ("disabled" in node and node.get("disabled") == true) or ("editable" in node and node.get("editable") == false)


func _flags(node: Node, view: Rect2) -> int:
    var flags := 0
    if _disabled(node):
        flags |= 1
    if node is Control:
        if node.has_focus():
            flags |= 2
        if not view.intersects(node.get_global_rect(), true):
            flags |= 4
    return flags


## Each node's id, box, text and flags, and the values of those of its properties named.
func layout(ask: int, names: Array) -> void:
    var nodes: Array = []
    _walk(get_tree().root, nodes, get_viewport().get_visible_rect(), names)
    _say(ask, true, [nodes])


func _walk(node: Node, nodes: Array, view: Rect2, names: Array) -> void:
    if node == self:
        return
    var box := _box(node)
    var values: Array = []
    for property in names:
        if property in node:
            values.append([property, node.get(property)])
    nodes.append([node.get_instance_id(), box[0], box[1], box[2], box[3], box[4], _text(node), _flags(node, view), values])
    for child in node.get_children():
        _walk(child, nodes, view, names)


func windows(ask: int) -> void:
    var found: Array = []
    _windows(get_tree().root, found)
    _say(ask, true, [found])


func _windows(node: Node, found: Array) -> void:
    if node is Window:
        var owner_id := 0
        var above := node.get_parent()
        while above != null and not above is Window:
            above = above.get_parent()
        if above != null:
            owner_id = above.get_instance_id()
        var box := _box(node)
        var scale: float = node.get_final_transform().get_scale().x
        found.append([node.get_instance_id(), node.get_class(), String(node.name), node.title, box[3], box[4], scale, node.has_focus(), node.visible, owner_id])
    for child in node.get_children():
        _windows(child, found)


func _modifiers(event: InputEventWithModifiers, modifiers: int) -> void:
    event.shift_pressed = modifiers & SHIFT != 0
    event.ctrl_pressed = modifiers & CTRL != 0
    event.alt_pressed = modifiers & ALT != 0
    event.meta_pressed = modifiers & META != 0


func _send(event: InputEvent) -> void:
    Input.parse_input_event(event)
    Input.flush_buffered_events()


func _frames(count: int) -> void:
    for i in count:
        await get_tree().process_frame


func _named(node: Node) -> String:
    return "%s %s" % [node.get_class(), node.name]


## The point a pointer goes to in the viewport, or null once why not is said: the middle of what is in view of a Control, or
## x, y from its top left.
func _aim(ask: int, id: int, has_point: bool, x: float, y: float) -> Variant:
    if id == 0:
        return Vector2(x, y)
    if _gone(ask, id):
        return null
    var node := _node(id)
    if not node is Control:
        _say(ask, false, ["%s is no Control, so it has no place a pointer goes to: give x and y in the window" % _named(node)])
        return null
    if not node.is_visible_in_tree():
        _say(ask, false, ["%s is hidden" % _named(node)])
        return null
    if _disabled(node):
        _say(ask, false, ["%s is disabled" % _named(node)])
        return null
    var rect: Rect2 = node.get_global_rect()
    var view := get_viewport().get_visible_rect()
    if not view.intersects(rect, true):
        _say(ask, false, ["%s is out of view" % _named(node)])
        return null
    if has_point:
        return rect.position + Vector2(x, y)
    # The middle of what is in view of it: its own middle may be out of view, where no click lands.
    return rect.intersection(view).get_center() if view.intersects(rect) else rect.get_center()


## What input came to: the point, what was under it, what has the focus and what that shows (else what is under it).
func _after(at: Vector2, under: Control, shows: Variant, told: bool) -> Array:
    var focus := get_viewport().gui_get_focus_owner()
    if not told:
        shows = _text(focus) if focus != null else _text(under)
    return [at.x, at.y, under.get_instance_id() if under != null else 0, focus.get_instance_id() if focus != null else 0, shows]


## Moves the pointer to a point, a frame given for the viewport to say what is under it.
func _hover(at: Vector2, modifiers: int) -> Control:
    var viewport := get_viewport()
    var move := InputEventMouseMotion.new()
    move.position = viewport.get_final_transform() * at
    move.global_position = move.position
    _modifiers(move, modifiers)
    _send(move)
    await _frames(1)
    return viewport.gui_get_hovered_control()


func click(ask: int, id: int, has_point: bool, x: float, y: float, button: int, count: int, modifiers: int) -> void:
    var at = _aim(ask, id, has_point, x, y)
    if at == null:
        return
    _pressed_at = at
    var under: Control = await _hover(at, modifiers)
    if _gone(ask, id):
        return
    var target := _node(id)
    if target != null and under != null and under != target and not target.is_ancestor_of(under):
        _say(ask, false, ["%s is covered by %s at %d,%d" % [_named(target), _named(under), at.x, at.y]])
        return
    if target != null and under == null:
        _say(ask, false, ["%s takes no pointer at %d,%d%s" % [_named(target), at.x, at.y,
            ": its mouse_filter is Ignore" if target is Control and target.mouse_filter == Control.MOUSE_FILTER_IGNORE else ", nor does anything there"]])
        return
    var screen: Vector2 = get_viewport().get_final_transform() * at
    for i in count:
        var down := InputEventMouseButton.new()
        down.button_index = button as MouseButton
        down.button_mask = 1 << (button - 1)
        down.position = screen
        down.global_position = screen
        down.double_click = i == 1
        down.pressed = true
        _modifiers(down, modifiers)
        _send(down)
        await _frames(1)
        # A pick under Godot's node selection is made at a physics frame between the press and the release.
        await get_tree().physics_frame
        var up := down.duplicate() as InputEventMouseButton
        up.pressed = false
        up.button_mask = 0
        up.double_click = false
        _send(up)
    await _frames(2)
    _say(ask, true, _after(at, under, null, false))


## The body or area the game's 3D physics space holds under the last press this helper gave, as Godot's node selection
## casts its ray there: [whether a press and a camera are known, the node's words or "" for none].
func shapes(ask: int) -> void:
    var camera := get_viewport().get_camera_3d()
    if camera == null or not _pressed_at.is_finite():
        _say(ask, true, [false, ""])
        return
    var from := camera.project_ray_origin(_pressed_at)
    var query := PhysicsRayQueryParameters3D.create(from, from + camera.project_ray_normal(_pressed_at) * camera.far)
    query.collide_with_areas = true
    var hit := get_viewport().find_world_3d().direct_space_state.intersect_ray(query)
    _say(ask, true, [true, _named(hit["collider"]) if hit.get("collider") is Node else ""])


func scroll(ask: int, id: int, has_point: bool, x: float, y: float, dx: float, dy: float, modifiers: int) -> void:
    var at = _aim(ask, id, has_point, x, y)
    if at == null:
        return
    var under: Control = await _hover(at, modifiers)
    if _gone(ask, id):
        return
    var screen: Vector2 = get_viewport().get_final_transform() * at
    for turn in [[dy, MOUSE_BUTTON_WHEEL_DOWN, MOUSE_BUTTON_WHEEL_UP], [dx, MOUSE_BUTTON_WHEEL_RIGHT, MOUSE_BUTTON_WHEEL_LEFT]]:
        for i in int(round(absf(turn[0]))):
            var wheel := InputEventMouseButton.new()
            wheel.button_index = turn[1] if turn[0] > 0 else turn[2]
            wheel.position = screen
            wheel.global_position = screen
            wheel.factor = 1.0
            wheel.pressed = true
            _modifiers(wheel, modifiers)
            _send(wheel)
            var up := wheel.duplicate() as InputEventMouseButton
            up.pressed = false
            _send(up)
    await _frames(2)
    var scrolled: Node = under
    while scrolled != null and not scrolled is ScrollContainer:
        scrolled = scrolled.get_parent()
    var shows: Variant = null
    if scrolled != null:
        shows = "%s at %d,%d" % [_named(scrolled), scrolled.scroll_horizontal, scrolled.scroll_vertical]
    _say(ask, true, _after(at, under, shows, true))


func _focus(id: int) -> Control:
    var node := _node(id)
    if node is Control and node.focus_mode != Control.FOCUS_NONE:
        node.grab_focus()
        return node
    return get_viewport().gui_get_focus_owner()


func key(ask: int, id: int, chords: Array) -> void:
    if _gone(ask, id):
        return
    _focus(id)
    for chord in chords:
        var code := OS.find_keycode_from_string(chord[0])
        if code == KEY_NONE:
            _say(ask, false, ["Godot has no key %s" % chord[0]])
            return
        var modifiers: int = chord[1]
        var press := InputEventKey.new()
        press.keycode = code
        press.physical_keycode = code
        press.pressed = true
        _modifiers(press, modifiers)
        if modifiers & (CTRL | ALT | META) == 0 and code >= KEY_SPACE and code <= KEY_ASCIITILDE:
            press.unicode = code + 32 if code >= KEY_A and code <= KEY_Z and modifiers & SHIFT == 0 else code
        _send(press)
        await _frames(1)
        var release := press.duplicate() as InputEventKey
        release.pressed = false
        _send(release)
    await _frames(2)
    _say(ask, true, _after(Vector2.ZERO, null, null, false))


func type(ask: int, id: int, text: String, replace: bool) -> void:
    if _gone(ask, id):
        return
    var aimed := _node(id)
    if aimed != null and not (aimed is Control and aimed.focus_mode != Control.FOCUS_NONE):
        _say(ask, false, ["%s takes no focus, so nothing typed would reach it: a LineEdit, a TextEdit or another control that takes the focus does" % _named(aimed)])
        return
    var focus := _focus(id)
    if replace and focus != null and focus.has_method("select_all"):
        focus.select_all()
    for character in text:
        var press := InputEventKey.new()
        press.pressed = true
        if character == "\n":
            press.keycode = KEY_ENTER
        elif character == "\t":
            press.keycode = KEY_TAB
        else:
            press.unicode = character.unicode_at(0)
            var named := OS.find_keycode_from_string(character.to_upper())
            if named != KEY_NONE and character.length() == 1 and character.to_upper() != character.to_lower():
                press.keycode = named
        _send(press)
        var release := press.duplicate() as InputEventKey
        release.pressed = false
        _send(release)
    await _frames(2)
    _say(ask, true, _after(Vector2.ZERO, null, null, false))


func _process(_delta: float) -> void:
    if _frame < 0:
        return
    _frame += 1
    if _stepping != 0:
        var ask := _stepping
        _stepping = 0
        if _ends_step:
            RenderingServer.frame_post_draw.emit.call_deferred()
        _say(ask, true, [_frame])


## The frames the game has run, counted from now on where they were not: those its engine ran until now, since only
## Passenger holds a game's loop. Called between frames, as every call is.
func _counting() -> void:
    if _frame < 0:
        _frame = Engine.get_process_frames() + 1
        process_mode = Node.PROCESS_MODE_ALWAYS
        set_process(true)


func count(ask: int) -> void:
    _counting()
    _say(ask, true, [_frame])


## The ids of the custom monitors the game's code added, in the order its monitors' messages give their values.
func monitors(ask: int) -> void:
    _say(ask, true, [Array(Performance.get_custom_monitor_names()).map(func(id): return str(id))])


## Answers once the next frame the game runs has run, with the count; where the game draws nothing (headless), that frame
## holds the loop again itself, as Godot does at a drawn frame's end.
func step(ask: int, ends_step: bool) -> void:
    _counting()
    _stepping = ask
    _ends_step = ends_step


## The id of the object a node's property holds, its class and its file: a resource loaded from a file, which the game sends
## by its path, read and set by its id from then on.
func held(ask: int, id: int, property: String) -> void:
    var node := _node(id)
    if node == null:
        _say(ask, false, ["the node is gone"])
        return
    var value = node.get(property)
    if typeof(value) != TYPE_OBJECT or not is_instance_valid(value):
        _say(ask, false, ["%s holds no object" % property])
        return
    _say(ask, true, [value.get_instance_id(), value.get_class(), value.resource_path if value is Resource else ""])


func _index(node: Node) -> void:
    if node == self:
        return
    var script = node.get_script()
    while script is Script:
        var path: String = script.resource_path
        if not path.is_empty():
            if not _scripted.has(path):
                _scripted[path] = {}
            _scripted[path][node.get_instance_id()] = node
        script = script.get_base_script()


func _unindex(node: Node) -> void:
    var script = node.get_script()
    while script is Script:
        if _scripted.has(script.resource_path):
            _scripted[script.resource_path].erase(node.get_instance_id())
        script = script.get_base_script()


func _index_under(node: Node) -> void:
    _index(node)
    for child in node.get_children():
        _index_under(child)


## A value as a hint's words say it where it is not sent itself: a node by its name and class, a resource by its file,
## another object by its class, a container by its first items and its size.
func _described(value: Variant) -> String:
    if typeof(value) == TYPE_OBJECT:
        if not is_instance_valid(value):
            return "freed"
        if value is Node:
            return "%s (%s)" % [value.name, value.get_class()]
        if value is Resource and not value.resource_path.is_empty():
            return value.resource_path
        return "<%s>" % value.get_class()
    if typeof(value) == TYPE_DICTIONARY:
        return str(value).left(200) if value.size() <= 4 else "{%d entries}" % value.size()
    if value.size() <= 6:
        return str(value).left(200)
    return "%s, …] (%d items)" % [str(value.slice(0, 6)).trim_suffix("]").left(200), value.size()]


## A member's value as sent: [] for one the node does not hold, [value], [words, 1] for an object or a container, or
## [path, 2, node] for a node path and the node it reaches ("" for none).
func _value(node: Node, name: String) -> Array:
    if not name in node:
        return []
    var value = node.get(name)
    var kind := typeof(value)
    if kind == TYPE_OBJECT or kind >= TYPE_DICTIONARY:
        return [_described(value), 1]
    if kind == TYPE_NODE_PATH:
        var reached: Node = node.get_node_or_null(value) if not value.is_empty() else null
        return [value, 2, _described(reached) if reached != null else ""]
    if kind == TYPE_STRING:
        return [value.left(200)]
    return [value]


## For each script asked of (its path, the id of the node chosen or 0, the members named): how many nodes run it, the node
## read (the one chosen where it runs the script, else the only one, else none), its name, and each member's value.
func values(ask: int, asks: Array) -> void:
    if not _indexed:
        _indexed = true
        _scripted = {}
        _index_under(get_tree().root)
        if not get_tree().node_added.is_connected(_index):
            get_tree().node_added.connect(_index)
            get_tree().node_removed.connect(_unindex)
    var answers: Array = []
    for wanted in asks:
        var nodes: Dictionary = _scripted.get(wanted[0], {})
        var found = null
        if nodes.has(wanted[1]):
            found = nodes[wanted[1]]
        elif nodes.size() == 1:
            found = nodes.values()[0]
        var read: Array = []
        if is_instance_valid(found):
            for name in wanted[2]:
                read.append(_value(found, name))
            answers.append([nodes.size(), found.get_instance_id(), String(found.name), read])
        else:
            for name in wanted[2]:
                read.append([])
            answers.append([nodes.size(), 0, "", read])
    _say(ask, true, [answers])
