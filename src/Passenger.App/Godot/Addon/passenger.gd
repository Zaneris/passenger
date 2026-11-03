@tool
extends EditorPlugin
## Passenger's addon for Godot's editor (MIT, Copyright (c) 2026 Ted L; written here by Passenger when asked). It finds
## the Passenger window open on this project through the file that window keeps in Passenger's state, which only this
## user may read, naming its loopback port and two secrets: it says the first, and talks on only once Passenger has said
## the second. While Passenger answers, Play builds the game's C# there, its errors in Passenger's Problems, and
## Passenger's debugger attaches to each game Play starts, held at its start until it has. With no Passenger listening,
## or one that refuses, it does nothing, and Godot plays as it always did.

## The addon's version, which Passenger reads to update an older one in place.
const VERSION := 1
## How long a build may take to be answered.
const BUILD_LIMIT_MS := 30 * 60 * 1000
## How long any other request may take to be answered.
const ANSWER_LIMIT_MS := 5000
## How often a Passenger is looked for while none answers.
const LOOK_EVERY_MS := 2000

var sessions := Sessions.new()
var peer: StreamPeerTCP = null
## Whether the Passenger connected to has said its secret and taken this project.
var welcomed := false
var received := PackedByteArray()
var asked := 0
var answers := {}
var looked_at := -LOOK_EVERY_MS
## The variables a game is started with, as the editor had them before: each name's value, or null where it had none.
var held := {}


## Each game session Godot's editor holds, told to Passenger as it starts and stops.
class Sessions extends EditorDebuggerPlugin:
	var addon: EditorPlugin

	func _setup_session(session_id: int) -> void:
		var session := get_session(session_id)
		session.started.connect(func(): addon.tell("started", {"session": session_id}))
		session.stopped.connect(func(): addon.tell("stopped", {"session": session_id}))


func _enter_tree() -> void:
	sessions.addon = self
	add_debugger_plugin(sessions)


func _exit_tree() -> void:
	remove_debugger_plugin(sessions)
	_let_go()
	_lost()


func _process(_delta: float) -> void:
	if peer == null:
		if Time.get_ticks_msec() - looked_at >= LOOK_EVERY_MS:
			looked_at = Time.get_ticks_msec()
			_connect()
		return
	_read()


## Godot's Play builds through Passenger: false stops Play where its build failed. Where Passenger has gone, the game is
## built as Godot builds it.
func _build() -> bool:
	if not welcomed:
		return true
	var answer = _ask("build", {"editor": OS.get_process_id()}, BUILD_LIMIT_MS)
	if not (answer is Dictionary and answer.get("result") is Dictionary):
		_lost()
		return _built_by_godot()
	var result: Dictionary = answer["result"]
	if not result.get("built", false):
		push_error("Passenger: " + str(result.get("said", "the build failed")))
		return false
	_hold(result.get("variables", {}))
	return true


## A message for Passenger that asks no answer, while it is connected.
func tell(method: String, params: Dictionary) -> void:
	if welcomed:
		_send({"method": method, "params": params})


func _connect() -> void:
	var folder := ProjectSettings.globalize_path("res://").trim_suffix("/")
	for listed in _listed(folder):
		var tried := StreamPeerTCP.new()
		if tried.connect_to_host("127.0.0.1", int(listed.get("port", 0))) != OK:
			continue
		var began := Time.get_ticks_msec()
		tried.poll()
		while tried.get_status() == StreamPeerTCP.STATUS_CONNECTING and Time.get_ticks_msec() - began < 1000:
			OS.delay_msec(1)
			tried.poll()
		if tried.get_status() != StreamPeerTCP.STATUS_CONNECTED:
			continue
		peer = tried
		received = PackedByteArray()
		peer.put_data((str(listed.get("token", "")) + "\n").to_utf8_buffer())
		if _first_line(ANSWER_LIMIT_MS) == str(listed.get("reply", "")):
			var hello = _ask("hello", {"folder": folder, "editor": OS.get_process_id(), "godot": Engine.get_version_info().get("string", ""),
				"addon": VERSION}, ANSWER_LIMIT_MS)
			if hello is Dictionary and hello.get("result") is Dictionary:
				welcomed = true
				_skip_godot_build(true)
				print("Passenger: ", str(hello["result"].get("said", "Play builds and debugs in Passenger")))
				return
		peer.disconnect_from_host()
		peer = null


## The Passenger windows listed in Passenger's state whose folder is this project's.
func _listed(folder: String) -> Array:
	var listing := _state_root().path_join("godot-editors")
	var found := []
	if not DirAccess.dir_exists_absolute(listing):
		return found
	for name in DirAccess.get_files_at(listing):
		if not name.ends_with(".json"):
			continue
		var read = JSON.parse_string(FileAccess.get_file_as_string(listing.path_join(name)))
		if not (read is Dictionary and read.get("folders") is Array):
			continue
		for each in read["folders"]:
			if _same(str(each), folder):
				found.append(read)
				break
	return found


## Passenger's state folder, as Passenger finds it: PASSENGER_SETTINGS, else the user's local application data.
func _state_root() -> String:
	var named := OS.get_environment("PASSENGER_SETTINGS")
	if named != "":
		return named
	match OS.get_name():
		"Windows":
			return OS.get_environment("LOCALAPPDATA").path_join("Passenger")
		"macOS":
			return OS.get_environment("HOME").path_join("Library/Application Support/Passenger")
		_:
			var data := OS.get_environment("XDG_DATA_HOME")
			if not data.is_absolute_path():
				data = OS.get_environment("HOME").path_join(".local/share")
			return data.path_join("Passenger")


func _same(one: String, other: String) -> bool:
	var a := one.replace("\\", "/").trim_suffix("/")
	var b := other.replace("\\", "/").trim_suffix("/")
	return a.nocasecmp_to(b) == 0 if OS.get_name() in ["Windows", "macOS"] else a == b


func _ask(method: String, params: Dictionary, limit_ms: int):
	asked += 1
	var id := asked
	_send({"id": id, "method": method, "params": params})
	var began := Time.get_ticks_msec()
	while peer != null and not answers.has(id) and Time.get_ticks_msec() - began < limit_ms:
		OS.delay_msec(5)
		_read()
	var answer = answers.get(id)
	answers.erase(id)
	return answer


func _send(message: Dictionary) -> void:
	if peer != null:
		peer.put_data((JSON.stringify(message) + "\n").to_utf8_buffer())


## Passenger's first line, its secret, waited for; empty where none came.
func _first_line(limit_ms: int) -> String:
	var began := Time.get_ticks_msec()
	while peer != null and Time.get_ticks_msec() - began < limit_ms:
		_take()
		var end := received.find(10)
		if end >= 0:
			var line := received.slice(0, end).get_string_from_utf8()
			received = received.slice(end + 1)
			return line
		OS.delay_msec(5)
	return ""


func _take() -> void:
	peer.poll()
	if peer.get_status() != StreamPeerTCP.STATUS_CONNECTED:
		_lost()
		return
	var available := peer.get_available_bytes()
	if available > 0:
		var read := peer.get_data(available)
		if read[0] == OK:
			received.append_array(read[1])


func _read() -> void:
	_take()
	var end := received.find(10)
	while peer != null and end >= 0:
		var message = JSON.parse_string(received.slice(0, end).get_string_from_utf8())
		received = received.slice(end + 1)
		if message is Dictionary:
			if message.has("method"):
				_heard(message)
			elif message.has("id"):
				answers[int(message["id"])] = message
		end = received.find(10)


## A request of Passenger's: this version answers none, saying so.
func _heard(message: Dictionary) -> void:
	if message.has("id"):
		_send({"id": message["id"], "error": "Passenger's addon %d takes no %s" % [VERSION, str(message["method"])]})


## The variables Passenger gives the game Play starts now, in the editor's environment until it has started.
func _hold(variables: Dictionary) -> void:
	for name in variables.keys():
		if not held.has(name):
			held[name] = OS.get_environment(name) if OS.has_environment(name) else null
		OS.set_environment(name, str(variables[name]))
	if not held.is_empty():
		_let_go.call_deferred()


func _let_go() -> void:
	for name in held.keys():
		if held[name] == null:
			OS.unset_environment(name)
		else:
			OS.set_environment(name, held[name])
	held.clear()


func _lost() -> void:
	if peer != null:
		peer.disconnect_from_host()
	peer = null
	welcomed = false
	answers.clear()
	_skip_godot_build(false)


## Godot's .NET plugin, whose own build before Play is skipped while Passenger builds the game.
func _godot_sharp() -> Object:
	if get_parent() == null:
		return null
	for plugin in get_parent().get_children():
		if plugin != self and plugin is EditorPlugin and plugin.get("SkipBuildBeforePlaying") != null:
			return plugin
	return null


func _skip_godot_build(skip: bool) -> void:
	var godot_sharp := _godot_sharp()
	if godot_sharp != null:
		godot_sharp.set("SkipBuildBeforePlaying", skip)


func _built_by_godot() -> bool:
	var godot_sharp := _godot_sharp()
	if godot_sharp == null:
		return true
	var built = godot_sharp.call("_build")
	return built if built is bool else true
