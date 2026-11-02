extends Node
## Passenger's helper in a game it debugs: where the nodes are, input given as a person gives it, and the frames the game
## has run. Each call's first argument is its ask, answered by a passenger:said message of the ask, whether it was done,
## and what it came to.

const SAID := "passenger:said"
const SHIFT := 1
const CTRL := 2
const ALT := 4
const META := 8

## The frames the game has run, once counted (-1 before): every frame its tree processes, the game's own pause among them.
var _frame := -1
## The ask a frame stepped answers, or 0.
var _stepping := 0
## Whether the frame stepped ends the step itself, where no frame is drawn for Godot to end it at.
var _ends_step := false


func _say(ask: int, done: bool, data: Array) -> void:
    EngineDebugger.send_message(SAID, [ask, done] + data)


func _node(id: int) -> Node:
    var found = instance_from_id(id)
    return found if found is Node else null


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


## The point a pointer goes to in the viewport, or null once why not is said: a Control's centre, or x, y from its top left.
func _aim(ask: int, id: int, has_point: bool, x: float, y: float) -> Variant:
    if id == 0:
        return Vector2(x, y)
    var node := _node(id)
    if node == null:
        _say(ask, false, ["the node is gone"])
        return null
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
    if not get_viewport().get_visible_rect().intersects(rect, true):
        _say(ask, false, ["%s is out of view" % _named(node)])
        return null
    return rect.position + (Vector2(x, y) if has_point else rect.size / 2)


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
    var under: Control = await _hover(at, modifiers)
    var target := _node(id)
    if target != null and under != null and under != target and not target.is_ancestor_of(under):
        _say(ask, false, ["%s is covered by %s at %d,%d" % [_named(target), _named(under), at.x, at.y]])
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


func scroll(ask: int, id: int, has_point: bool, x: float, y: float, dx: float, dy: float, modifiers: int) -> void:
    var at = _aim(ask, id, has_point, x, y)
    if at == null:
        return
    var under: Control = await _hover(at, modifiers)
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


## Answers once the next frame the game runs has run, with the count; where the game draws nothing (headless), that frame
## holds the loop again itself, as Godot does at a drawn frame's end.
func step(ask: int, ends_step: bool) -> void:
    _counting()
    _stepping = ask
    _ends_step = ends_step
