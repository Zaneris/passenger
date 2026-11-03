@tool
extends EditorPlugin
## Passenger's addon for Godot's editor (MIT, Copyright (c) 2026 Ted L; written here by Passenger when asked). It finds
## the Passenger window open on this project through the file that window keeps in Passenger's state, which only this
## user may read, naming its loopback port and two secrets: it says the first, and talks on only once Passenger has said
## the second. While Passenger answers, Play builds the game's C# there, its errors in Passenger's Problems, Passenger's
## debugger attaches to each game Play starts, held at its start until it has, and what each game tells this editor is
## relayed to Passenger, which answers through the game's session here; this editor keeps the game's debugger. With no
## Passenger listening, or one that refuses, it does nothing, and Godot plays as it always did.

## The addon's version, which Passenger reads to update an older one in place.
const VERSION := 7
## How long a build may take to be answered.
const BUILD_LIMIT_MS := 30 * 60 * 1000
## How long any other request may take to be answered.
const ANSWER_LIMIT_MS := 5000
## How often a Passenger is looked for while none answers.
const LOOK_EVERY_MS := 2000
## How long a game held at its start waits for Passenger to take it before Play is stopped, rather than leave it waiting.
const HOLD_LIMIT_MS := 60 * 1000
## The one key of a dictionary that stands for an object the game sent by its id, which Passenger reads back as that id.
const OBJECT_KEY := "passenger:object"
## What an agent asks of the scenes this editor edits, through Passenger.
const Scenes := preload("scenes.gd")

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
## Whether the games of this Play were started held for Passenger, until Play ends.
var play_held := false
## The sessions of games held at their start that Passenger has yet to take or let go, each by when it began.
var holding := {}
## GodotTools' own setting of whether Play skips its build, as it was before Passenger built instead; null while untaken.
var godot_build_skipped = null
## The editor's debugger of each session whose messages are relayed, by session id.
var relayed := {}
## The tab Passenger's addon adds to each session's debugger, by session id, by which its debugger is found.
var tabs := {}
## The sessions told to Passenger as begun, by id, until they stop.
var announced := {}
## What Passenger was last told of the scenes this editor holds, as its JSON, and when it was looked at.
var scenes_told := ""
var scenes_looked := 0
## How often the scenes this editor holds are looked at for a change to tell.
const SCENES_EVERY_MS := 300


## Each game session Godot's editor holds, told to Passenger as it starts and stops.
class Sessions extends EditorDebuggerPlugin:
	var addon: EditorPlugin

	# Passenger's helper answers it through the game's debugger, which the editor would warn of as a message it does not know.
	func _has_capture(capture: String) -> bool:
		return capture == "passenger"

	func _capture(_message: String, _data: Array, _session_id: int) -> bool:
		return true

	func _setup_session(session_id: int) -> void:
		var session := get_session(session_id)
		session.started.connect(func(): addon.started(session_id))
		session.stopped.connect(func(): addon.stopped(session_id))
		var tab := Label.new()
		tab.name = "Passenger"
		tab.text = "While Passenger is open on this project, this session's game is shown there: its tree, errors and values."
		session.add_session_tab(tab)
		addon.tabs[session_id] = tab
		# Its debugger holds the tab once the session is set up.
		addon.relay.call_deferred(session_id)


func _enter_tree() -> void:
	sessions.addon = self
	add_debugger_plugin(sessions)
	scene_changed.connect(func(_root): _tell_scene())
	EditorInterface.get_selection().selection_changed.connect(_selection_changed)


func _exit_tree() -> void:
	if EditorInterface.get_selection().selection_changed.is_connected(_selection_changed):
		EditorInterface.get_selection().selection_changed.disconnect(_selection_changed)
	for session_id in tabs.keys():
		if sessions.get_session(session_id) != null and is_instance_valid(tabs[session_id]):
			sessions.get_session(session_id).remove_session_tab(tabs[session_id])
			tabs[session_id].queue_free()
	tabs.clear()
	remove_debugger_plugin(sessions)
	_let_go()
	_lost()


func _process(_delta: float) -> void:
	if play_held and holding.is_empty() and not EditorInterface.is_playing_scene():
		play_held = false
	for session_id in holding.keys():
		if Time.get_ticks_msec() - holding[session_id] > HOLD_LIMIT_MS:
			_stop_held("Passenger did not take the game within %d s" % (HOLD_LIMIT_MS / 1000))
			break
	if peer == null:
		if Time.get_ticks_msec() - looked_at >= LOOK_EVERY_MS:
			looked_at = Time.get_ticks_msec()
			_connect()
		return
	_read()
	if welcomed and Time.get_ticks_msec() - scenes_looked >= SCENES_EVERY_MS:
		scenes_looked = Time.get_ticks_msec()
		_tell_scenes()


## The scenes this editor has open and those it holds unsaved edits in (a Godot before 4.7 cannot say which), and the
## resource file its inspector shows, told to Passenger as they change, so its writes ask before going over them.
func _tell_scenes() -> void:
	var sure := EditorInterface.has_method("get_unsaved_scenes")
	var inspected := ""
	var edited := EditorInterface.get_inspector().get_edited_object()
	if edited is Resource and edited.resource_path.begins_with("res://") and not edited.resource_path.contains("::"):
		inspected = edited.resource_path
	var said := {"open": Array(EditorInterface.get_open_scenes()), "unsaved": Array(EditorInterface.call("get_unsaved_scenes")) if sure else [],
		"sure": sure, "inspected": inspected}
	var text := JSON.stringify(said)
	if text != scenes_told:
		scenes_told = text
		tell("scenes", said)


## The node selected in the scene dock, told to Passenger, which shows its scene there.
func _selection_changed() -> void:
	var nodes := EditorInterface.get_selection().get_selected_nodes()
	var root := EditorInterface.get_edited_scene_root()
	if welcomed and not nodes.is_empty() and root != null and root.scene_file_path != "":
		tell("selected", {"scene": root.scene_file_path, "node": str(root.get_path_to(nodes[0]))})


## Godot's Play builds through Passenger: false stops Play where its build failed. Where Passenger has gone, the game is
## built as Godot builds it.
func _build() -> bool:
	if not welcomed:
		return true
	# Godot draws nothing while a build callback runs: Passenger's window shows the build as it goes.
	print("Passenger: building the game's C# in Passenger, whose window shows the build as it goes")
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


## A game's session began, told to Passenger, and one held at its start waited for. The editor hears the game's first
## message, its process, before the session says it began, so whichever comes first tells it.
func started(session_id: int) -> void:
	if not welcomed or announced.has(session_id):
		return
	announced[session_id] = true
	if play_held:
		holding[session_id] = Time.get_ticks_msec()
	tell("started", {"session": session_id, "held": play_held})


func stopped(session_id: int) -> void:
	announced.erase(session_id)
	holding.erase(session_id)
	tell("stopped", {"session": session_id})


## Every message a session's game tells this editor, as its debugger hears it, sent on to Passenger from now: the
## session's own debugger, the one holding the tab added to the session, whatever its place among the editor's.
func relay(session_id: int) -> void:
	var debugger: Node = tabs.get(session_id)
	while debugger != null and not debugger.is_class("ScriptEditorDebugger"):
		debugger = debugger.get_parent()
	if debugger == null or relayed.get(session_id) == debugger:
		return
	relayed[session_id] = debugger
	debugger.connect("debug_data", _heard_game.bind(session_id))


func _heard_game(message: String, data: Array, session_id: int) -> void:
	# The profilers' frames, many a second, are this editor's alone.
	if not welcomed or message.begins_with("servers:") or message.begins_with("visual:"):
		return
	started(session_id)
	tell("game", {"session": session_id, "message": Marshalls.raw_to_base64(var_to_bytes([message, 1, _plain(data)]))})


## A value with each object sent by its id as a dictionary of OBJECT_KEY, which Godot's encoding would write as the id of
## the editor's own holder instead; a container holding none is the same container, its element types kept.
func _plain(value: Variant) -> Variant:
	if value is EncodedObjectAsID:
		return {OBJECT_KEY: value.get_object_id()}
	if value is Array:
		var items := []
		var changed := false
		for item in value:
			var plain = _plain(item)
			changed = changed or not is_same(plain, item)
			items.append(plain)
		return items if changed else value
	if value is Dictionary:
		var entries := {}
		var changed := false
		for key in value:
			var plain = _plain(value[key])
			changed = changed or not is_same(plain, value[key])
			entries[key] = plain
		return entries if changed else value
	return value


func _tell_scene() -> void:
	var root := EditorInterface.get_edited_scene_root()
	tell("scene", {"path": root.scene_file_path if root != null else ""})


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
				_tell_scene()
				# Games playing already are relayed from now, none of them held.
				for session_id in tabs.keys():
					relay(session_id)
					if sessions.get_session(session_id) != null and sessions.get_session(session_id).is_active():
						started(session_id)
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


## Passenger's messages: one for a game sent on through its session, a held game taken or let go, Play and Stop asked
## for; a request this version does not answer refused, saying so.
func _heard(message: Dictionary) -> void:
	var method := str(message["method"])
	var params: Dictionary = message.get("params") if message.get("params") is Dictionary else {}
	if method == "send":
		var session: EditorDebuggerSession = sessions.get_session(int(params.get("session", -1))) if tabs.has(int(params.get("session", -1))) else null
		var sent = bytes_to_var(Marshalls.base64_to_raw(str(params.get("message", ""))))
		if session != null and session.is_active() and sent is Array and sent.size() == 3 and sent[2] is Array:
			session.send_message(str(sent[0]), sent[2])
	elif method == "released":
		holding.erase(int(params.get("session", -1)))
		if str(params.get("said", "")) != "":
			print("Passenger: ", str(params["said"]))
	elif method == "play":
		# Played once this message is read through, as Play's build asks Passenger itself.
		_play.call_deferred(str(params.get("scene", "")))
		_send({"id": int(message.get("id", 0)), "result": {}})
	elif method == "reload":
		# Passenger wrote a scene this editor has open and saved: read again, so no question of a newer file comes.
		var path := str(params.get("path", ""))
		var unsaved = EditorInterface.call("get_unsaved_scenes") if EditorInterface.has_method("get_unsaved_scenes") else [path]
		if path in EditorInterface.get_open_scenes() and not path in unsaved:
			EditorInterface.reload_scene_from_path(path)
			print("Passenger: ", path, " read again, as Passenger wrote it")
		_send({"id": int(message.get("id", 0)), "result": {}})
	elif method.begins_with("scene_"):
		var answer = Scenes.asked(method, params)
		if answer is String:
			_send({"id": int(message.get("id", 0)), "error": answer})
		else:
			_send({"id": int(message.get("id", 0)), "result": answer})
	elif method == "stop":
		if EditorInterface.is_playing_scene():
			EditorInterface.stop_playing_scene()
		_send({"id": int(message.get("id", 0)), "result": {}})
	elif message.has("id"):
		# JSON reads every number as a float, which Passenger would not take for its request's id.
		_send({"id": int(message["id"]), "error": "Passenger's addon %d takes no %s" % [VERSION, method]})


## Play, as Passenger asks it: the scene given (res://), else the main scene, as Godot's own Play plays it.
func _play(scene: String) -> void:
	if EditorInterface.is_playing_scene():
		EditorInterface.stop_playing_scene()
	if scene == "":
		EditorInterface.play_main_scene()
	else:
		EditorInterface.play_custom_scene(scene)


## The variables Passenger gives the game Play starts now, in the editor's environment until it has started.
func _hold(variables: Dictionary) -> void:
	play_held = not variables.is_empty()
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
	announced.clear()
	scenes_told = ""
	_skip_godot_build(false)
	if play_held:
		_stop_held("Passenger went before it took the game")


## Play stopped where a game held at its start would wait for good for a Passenger that will not take it.
func _stop_held(why: String) -> void:
	holding.clear()
	play_held = false
	if EditorInterface.is_playing_scene():
		push_warning("Passenger: %s, which waited at its start for Passenger's debugger: Play stopped; play again" % why)
		EditorInterface.stop_playing_scene()


## Godot's .NET plugin, whose own build before Play is skipped while Passenger builds the game.
func _godot_sharp() -> Object:
	if get_parent() == null:
		return null
	for plugin in get_parent().get_children():
		if plugin != self and plugin is EditorPlugin and plugin.get("SkipBuildBeforePlaying") != null:
			return plugin
	return null


## GodotTools' build before Play skipped while Passenger builds, its setting put back as it was found once Passenger goes.
func _skip_godot_build(skip: bool) -> void:
	var godot_sharp := _godot_sharp()
	if godot_sharp == null:
		return
	if skip:
		if godot_build_skipped == null:
			godot_build_skipped = godot_sharp.get("SkipBuildBeforePlaying")
		godot_sharp.set("SkipBuildBeforePlaying", true)
	elif godot_build_skipped != null:
		godot_sharp.set("SkipBuildBeforePlaying", godot_build_skipped)
		godot_build_skipped = null


func _built_by_godot() -> bool:
	var godot_sharp := _godot_sharp()
	if godot_sharp == null:
		return true
	var built = godot_sharp.call("_build")
	return built if built is bool else true
