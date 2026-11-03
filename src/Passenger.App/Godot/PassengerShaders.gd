extends SceneTree
## Passenger's shader helper: compiles the shaders Passenger hands it with Godot's own compiler, which a headless
## Godot's dummy renderer runs, and answers with what Godot's logger heard as it did and the uniforms each declares.
## It connects to Passenger on loopback, on the port its first user argument names, says the token Passenger put in its
## environment, then answers each request, a JSON line, with one, until Passenger lets go. It writes no file.

## What Godot's logger hears, kept until taken: errors and warnings, from any thread.
class Heard extends Logger:
	var mutex := Mutex.new()
	var messages: Array = []

	func _log_error(_function: String, file: String, line: int, code: String, rationale: String, _editor_notify: bool,
			error_type: int, _script_backtraces: Array[ScriptBacktrace]) -> void:
		mutex.lock()
		messages.append({"type": error_type, "file": file, "line": line, "text": code, "rationale": rationale})
		mutex.unlock()

	func _log_message(_message: String, _error: bool) -> void:
		pass

	func taken() -> Array:
		mutex.lock()
		var all := messages
		messages = []
		mutex.unlock()
		return all


var heard := Heard.new()
var peer := StreamPeerTCP.new()
var received := PackedByteArray()
var connected := false
## The includes handed over, by res:// path, which Godot's preprocessor finds in its resource cache as their files.
var held := {}


func _initialize() -> void:
	OS.add_logger(heard)
	OS.low_processor_usage_mode = true
	OS.low_processor_usage_mode_sleep_usec = 16000
	var arguments := OS.get_cmdline_user_args()
	var port := int(arguments[0]) if arguments.size() > 0 else 0
	if port <= 0 or peer.connect_to_host("127.0.0.1", port) != OK:
		quit(1)


func _process(_delta: float) -> bool:
	peer.poll()
	var status := peer.get_status()
	if status == StreamPeerTCP.STATUS_CONNECTING:
		return false
	if status != StreamPeerTCP.STATUS_CONNECTED:
		return true
	if not connected:
		connected = true
		var version := Engine.get_version_info()
		send({"token": OS.get_environment("PASSENGER_SHADER_TOKEN"),
			"version": "%d.%d.%d.%s" % [version.major, version.minor, version.patch, version.status], "build": version.hash})
	var available := peer.get_available_bytes()
	if available > 0:
		var read := peer.get_data(available)
		if read[0] != OK:
			return true
		received.append_array(read[1])
	var end := received.find(10)
	while end >= 0:
		var line := received.slice(0, end).get_string_from_utf8()
		received = received.slice(end + 1)
		answer(line)
		end = received.find(10)
	return false


func send(message: Dictionary) -> void:
	peer.put_data((JSON.stringify(message) + "\n").to_utf8_buffer())


func answer(line: String) -> void:
	var request = JSON.parse_string(line)
	if typeof(request) != TYPE_DICTIONARY:
		return
	var id := int(request.get("id", 0))
	if request.has("compile"):
		var asked: Dictionary = request["compile"]
		hold(asked.get("includes", {}))
		heard.taken()
		var shader := compiled(str(asked.get("path", "")), str(asked.get("code", "")))
		var uniforms := []
		for uniform in shader.get_shader_uniform_list():
			uniforms.append({"name": str(uniform["name"]), "type": uniform["type"], "hint": uniform["hint"],
				"hint_string": str(uniform["hint_string"]), "usage": uniform["usage"]})
		send({"id": id, "messages": heard.taken(), "uniforms": uniforms})
	elif request.has("probe"):
		var refused := []
		var index := 0
		for code in request["probe"]:
			heard.taken()
			compiled("", str(code))
			for message in heard.taken():
				if message["type"] == Logger.ERROR_TYPE_SHADER:
					refused.append(index)
					break
			index += 1
		send({"id": id, "refused": refused})


## A shader of this code compiled now, at its res:// path, which its relative includes are found from.
func compiled(path: String, code: String) -> Shader:
	var shader := Shader.new()
	if path != "":
		shader.take_over_path(path)
	shader.code = code
	shader.get_rid()
	return shader


## The includes of this request held as resources at their paths, their code as given; those no longer named let go.
func hold(includes: Dictionary) -> void:
	for path in held.keys():
		if not includes.has(path):
			held.erase(path)
	for path in includes.keys():
		var include: ShaderInclude = held.get(path)
		if include == null:
			include = ShaderInclude.new()
			include.take_over_path(path)
			held[path] = include
		if include.code != includes[path]:
			include.code = includes[path]
