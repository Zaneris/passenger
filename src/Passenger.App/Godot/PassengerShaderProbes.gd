## Passenger's probe of the shading language the Godot found takes, run headless by Passenger with two files as its
## arguments: a JSON array of tiny shaders to read, and the file to write, a JSON array of the indices of those Godot's
## compiler refused (its logger heard a shader error while compiling them, Godot 4.5's OS.add_logger).
extends SceneTree

## What Godot's logger hears, kept until taken: whether a shader error was heard, from any thread.
class Heard extends Logger:
	var mutex := Mutex.new()
	var refused := false

	func _log_error(_function: String, _file: String, _line: int, _code: String, _rationale: String, _editor_notify: bool,
			error_type: int, _script_backtraces: Array[ScriptBacktrace]) -> void:
		if error_type == Logger.ERROR_TYPE_SHADER:
			mutex.lock()
			refused = true
			mutex.unlock()

	func _log_message(_message: String, _error: bool) -> void:
		pass

	func taken() -> bool:
		mutex.lock()
		var was := refused
		refused = false
		mutex.unlock()
		return was


func _initialize() -> void:
	var arguments := OS.get_cmdline_user_args()
	var heard := Heard.new()
	OS.add_logger(heard)
	var probes = JSON.parse_string(FileAccess.get_file_as_string(arguments[0]))
	var refused := []
	var index := 0
	for code in probes:
		heard.taken()
		var shader := Shader.new()
		shader.code = str(code)
		shader.get_rid()
		if heard.taken():
			refused.append(index)
		index += 1
	var file := FileAccess.open(arguments[1], FileAccess.WRITE)
	file.store_string(JSON.stringify(refused))
	file.close()
	quit()
