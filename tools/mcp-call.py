#!/usr/bin/env python3
"""One tool of Passenger's MCP server called from the shell over a folder, for an agent whose checkout is not the one its
own client's server runs over, a git worktree's (`just mcp-call FOLDER TOOL ['{"name": value}']`).

The first call over a folder starts a keeper: a headless `Passenger --mcp FOLDER` through tools/mcp.sh (a clone of this
checkout's last build, else the main checkout's), held for later calls over a Unix socket, so the language server loads
once; its state is its own, under artifacts/mcp-call. It ends after IDLE_MINUTES with no call, or at --stop; mcp.sh
removes its clone once the server has ended. A call told its progress prints it to standard error. It opens no window.

python3 -I tools/mcp-call.py [--from DIR] FOLDER [TOOL [JSON | -] | --list | --stop]
"""
import base64
import fcntl
import hashlib
import json
import os
import queue
import select
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = os.path.join("artifacts", "bin", "Passenger.App", "debug", "Passenger")
IDLE_VARIABLE = "PASSENGER_MCP_CALL_IDLE"
PROTOCOL = "2025-06-18"
# How long the keeper's socket is waited for as it starts, and its server's answer to initialize.
START_LIMIT = 60
# How long a server whose input has closed is given to end, then one asked to end by its signal.
END_LIMIT = 20
# How long a call may go untold: the server tells a long call's progress every 10 s.
SILENCE_LIMIT = 600


def idle_minutes():
    """Minutes with no call before a keeper ends its server: IDLE_VARIABLE's, else 20; a malformed one is fatal."""
    given = os.environ.get(IDLE_VARIABLE, "20")
    try:
        minutes = float(given)
    except ValueError:
        minutes = 0
    if minutes <= 0:
        sys.exit(f"mcp-call: {IDLE_VARIABLE} takes a number of minutes above 0, not '{given}'")
    return minutes


def serving_checkout():
    """The checkout whose build serves: this one's, else (a worktree not yet built) the main checkout's."""
    if os.access(os.path.join(ROOT, BUILD), os.X_OK):
        return ROOT
    common = subprocess.run(["git", "-C", ROOT, "rev-parse", "--path-format=absolute", "--git-common-dir"],
                            capture_output=True, text=True, check=False).stdout.strip()
    main = os.path.dirname(common) if common else ""
    if main and os.access(os.path.join(main, BUILD), os.X_OK):
        print(f"mcp-call: no build in {ROOT}, so the main checkout's at {main} serves", file=sys.stderr)
        return main
    sys.exit(f"mcp-call: no build at {os.path.join(ROOT, BUILD)}; run just build first")


def places(folder, serving):
    """The keeper's socket, lock and log for a folder served by a checkout's build. The socket's folder is short (a Unix
    socket's path holds about 104 bytes) and the user's alone."""
    key = hashlib.sha256(f"{serving}\0{folder}".encode()).hexdigest()[:16]
    sockets = os.path.join(tempfile.gettempdir(), f"passenger-mcp-call-{os.getuid()}")
    os.makedirs(sockets, mode=0o700, exist_ok=True)
    if os.stat(sockets).st_uid != os.getuid():
        sys.exit(f"mcp-call: {sockets} is not this user's")
    os.chmod(sockets, 0o700)
    logs = os.path.join(ROOT, "artifacts", "mcp-call")
    os.makedirs(logs, exist_ok=True)
    return (os.path.join(sockets, key + ".sock"), os.path.join(sockets, key + ".lock"),
            os.path.join(logs, f"{os.path.basename(folder) or 'root'}-{key}.log"))


def connect(path):
    client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        client.connect(path)
        return client
    except (FileNotFoundError, ConnectionRefusedError):
        client.close()
        return None


def reach(folder, serving, sock, lock, log):
    """The folder's keeper, started first when none answers, one start at a time."""
    if (client := connect(sock)) is not None:
        return client
    with open(lock, "w") as held:
        fcntl.flock(held, fcntl.LOCK_EX)
        if (client := connect(sock)) is not None:
            return client
        if os.path.exists(sock):
            os.unlink(sock)
        with open(log, "a") as written:
            # A session of its own, so the shell that made the first call takes nothing of it down as it ends.
            subprocess.Popen([sys.executable, "-I", os.path.abspath(__file__), "--keep", folder, serving, sock], cwd="/",
                             stdin=subprocess.DEVNULL, stdout=written, stderr=written, start_new_session=True, close_fds=True)
        deadline = time.monotonic() + START_LIMIT
        while time.monotonic() < deadline:
            if (client := connect(sock)) is not None:
                return client
            time.sleep(0.1)
    sys.exit(f"mcp-call: the keeper did not start within {START_LIMIT} s; its log is {log}")


def lines_of(connection):
    """Each JSON line a socket sends, until it closes."""
    pending = b""
    while True:
        chunk = connection.recv(65536)
        if not chunk:
            return
        pending += chunk
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            if line.strip():
                yield json.loads(line)


def send(connection, message):
    connection.sendall(json.dumps(message).encode() + b"\n")


def call(start, folder, tool, text):
    """The client: one request to the folder's keeper, its answer printed. Exit 1 on the tool's error, 2 on a failed call."""
    folder = os.path.realpath(os.path.join(start, folder))
    if not os.path.isdir(folder):
        sys.exit(f"mcp-call: no folder at {folder}")
    serving = serving_checkout()
    sock, lock, log = places(folder, serving)
    if tool == "--stop":
        if (client := connect(sock)) is None:
            print(f"mcp-call: no server runs over {folder}")
            return 0
        request = {"stop": True}
    else:
        request = {"list": True} if tool in ("", "--list") else {"tool": tool, "arguments": given(text)}
        client = reach(folder, serving, sock, lock, log)
    with client:
        send(client, request)
        client.settimeout(SILENCE_LIMIT)
        try:
            for message in lines_of(client):
                if "progress" in message:
                    print(f"mcp-call: {message['progress']}", file=sys.stderr, flush=True)
                elif "stopped" in message:
                    print(f"mcp-call: the server over {folder} is ending")
                    return 0
                elif "failed" in message:
                    print(f"mcp-call: {message['failed']} (the keeper's log: {log})", file=sys.stderr)
                    return 2
                elif "answer" in message:
                    return shown(message["answer"], request, log)
        except socket.timeout:
            print(f"mcp-call: no word from the server in {SILENCE_LIMIT} s; its log is {log}", file=sys.stderr)
            return 2
    print(f"mcp-call: the keeper ended without answering; its log is {log}", file=sys.stderr)
    return 2


def given(text):
    """A tool's arguments: a JSON object, '-' reading it from standard input."""
    text = sys.stdin.read() if text == "-" else text or "{}"
    try:
        value = json.loads(text)
    except ValueError as error:
        sys.exit(f"mcp-call: the arguments are not JSON ({error}): {text}")
    if not isinstance(value, dict):
        sys.exit(f"mcp-call: the arguments are a JSON object of the tool's parameters, not {text}")
    return value


def shown(answer, request, log):
    if "error" in answer:
        print(f"mcp-call: {answer['error'].get('message', answer['error'])}", file=sys.stderr)
        return 2
    result = answer.get("result", {})
    if "list" in request:
        for tool in result.get("tools", []):
            print(f"{tool['name']}({parameters(tool)}): {tool.get('description', '').split('. ')[0]}")
        return 0
    for item in result.get("content", []):
        if item.get("type") == "text":
            print(item["text"])
        elif item.get("type") == "image":
            extension = {"image/png": ".png", "image/jpeg": ".jpg"}.get(item.get("mimeType"), ".bin")
            picture = os.path.join(os.path.dirname(log), f"{request['tool']}-{time.strftime('%H%M%S')}{extension}")
            with open(picture, "wb") as written:
                written.write(base64.b64decode(item["data"]))
            print(f"(an image, written to {picture})")
        else:
            print(json.dumps(item))
    return 1 if result.get("isError") else 0


def parameters(tool):
    """A tool's parameters as its schema gives them, each with its type, an optional one marked '?': "path: string, line?: integer"."""
    schema = tool.get("inputSchema", {})
    required = set(schema.get("required", []))
    def typed(shape):
        given = shape.get("type", "any")
        return "|".join(given) if isinstance(given, list) else given
    return ", ".join(f"{name}{'' if name in required else '?'}: {typed(shape)}" for name, shape in schema.get("properties", {}).items())


class Keeper:
    """A headless Passenger over one folder, its calls made for the clients that reach its socket, each answer to the
    client that asked; it ends its server after IDLE_MINUTES with none, at a client's stop, at a signal, or as the
    server ends."""

    def __init__(self, folder, serving, sock):
        self.folder, self.serving, self.sock = folder, serving, sock
        self.idle = idle_minutes() * 60
        self.lock = threading.Lock()
        self.writing = threading.Lock()
        self.waiting = {}
        self.next_id = 1
        self.busy = 0
        self.last = time.monotonic()
        self.ready = threading.Event()
        self.ending = threading.Event()
        self.failure = None
        self.server = None

    def run(self):
        listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        listener.bind(self.sock)
        os.chmod(self.sock, 0o600)
        listener.listen(16)
        listener.settimeout(1)
        for each in (signal.SIGTERM, signal.SIGINT):
            signal.signal(each, lambda *_: self.ending.set())
        threading.Thread(target=self.start, daemon=True).start()
        log(f"keeping a server over {self.folder} from {self.serving}'s build")
        try:
            while not self.ending.is_set():
                try:
                    connection, _ = listener.accept()
                except socket.timeout:
                    with self.lock:
                        if self.busy == 0 and time.monotonic() - self.last > self.idle:
                            log(f"no call for {self.idle / 60:g} minutes")
                            break
                    continue
                threading.Thread(target=self.serve, args=(connection,), daemon=True).start()
        finally:
            listener.close()
            os.unlink(self.sock)
            self.end()

    def start(self):
        # A state of its own beside the logs, so a worktree's server never writes into the person's Passenger.
        state = os.path.join(ROOT, "artifacts", "mcp-call", "state-" + os.path.basename(self.sock).removesuffix(".sock"))
        try:
            os.makedirs(state, exist_ok=True)
            self.server = subprocess.Popen([os.path.join(self.serving, "tools", "mcp.sh"), self.folder], stdin=subprocess.PIPE,
                                           stdout=subprocess.PIPE, process_group=0, bufsize=0,
                                           env={**os.environ, "PASSENGER_SETTINGS": state})
            threading.Thread(target=self.read, daemon=True).start()
            _, answer = self.ask({"method": "initialize", "params": {"protocolVersion": PROTOCOL, "capabilities": {},
                                                                  "clientInfo": {"name": "mcp-call", "version": "1"}}})
            reply = answer.get(timeout=START_LIMIT)
            if "error" in reply:
                raise RuntimeError(f"the server refused to initialize: {reply['error']}")
            self.write({"jsonrpc": "2.0", "method": "notifications/initialized"})
            log(f"the server {self.server.pid} answers")
        except (OSError, RuntimeError, queue.Empty) as error:
            self.failure = f"the server did not start: {error or 'no answer to initialize'}"
            log(self.failure)
            self.ending.set()
        self.ready.set()

    def ask(self, message):
        with self.lock:
            ident = self.next_id
            self.next_id += 1
            answers = queue.Queue()
            self.waiting[ident] = answers
        message = {"jsonrpc": "2.0", "id": ident, **message}
        if message["method"] == "tools/call":
            message["params"]["_meta"] = {"progressToken": ident}
        self.write(message)
        return ident, answers

    def write(self, message):
        with self.writing:
            self.server.stdin.write(json.dumps(message).encode() + b"\n")
            self.server.stdin.flush()

    def read(self):
        """The server's messages: an answer or progress to the call it is for, a request of the server's answered."""
        for line in self.server.stdout:
            try:
                message = json.loads(line)
            except ValueError:
                log(f"not JSON from the server: {line[:200]!r}")
                continue
            method = message.get("method")
            if method == "notifications/progress":
                answers = self.waiting.get(message.get("params", {}).get("progressToken"))
            elif method is None:
                answers = self.waiting.pop(message.get("id"), None)
            else:
                if "id" in message:
                    self.write({"jsonrpc": "2.0", "id": message["id"], "result": {}} if method == "ping" else
                               {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32601, "message": f"mcp-call has no {method}"}})
                continue
            if answers is not None:
                answers.put(message)
        log("the server's output ended")
        self.failure = self.failure or "the server ended"
        self.ending.set()
        for answers in list(self.waiting.values()):
            answers.put({"error": {"message": self.failure}})

    def serve(self, connection):
        with self.lock:
            self.busy += 1
        try:
            request = next(lines_of(connection), None)
            if request is None:
                return
            if request.get("stop"):
                send(connection, {"stopped": True})
                log("asked to stop")
                self.ending.set()
                return
            self.ready.wait(START_LIMIT)
            if self.failure is not None or not self.ready.is_set():
                send(connection, {"failed": self.failure or "the server did not answer"})
                return
            if "tool" in request:
                ident, answers = self.ask({"method": "tools/call", "params": {"name": request["tool"], "arguments": request["arguments"]}})
            else:
                ident, answers = self.ask({"method": "tools/list", "params": {}})
            while True:
                try:
                    message = answers.get(timeout=0.5)
                except queue.Empty:
                    if gone(connection):
                        # The shell's call ended (interrupted or timed out): the tool is told to give up.
                        self.write({"jsonrpc": "2.0", "method": "notifications/cancelled",
                                    "params": {"requestId": ident, "reason": "the shell's call ended"}})
                        self.waiting.pop(ident, None)
                        return
                    continue
                if message.get("method") == "notifications/progress":
                    said = message.get("params", {})
                    send(connection, {"progress": said.get("message") or f"{said.get('progress')} of {said.get('total', '?')}"})
                    continue
                send(connection, {"answer": message})
                return
        except (OSError, ValueError) as error:
            log(f"a call failed: {error}")
        finally:
            connection.close()
            with self.lock:
                self.busy -= 1
                self.last = time.monotonic()

    def end(self):
        """The server ended as its input's end ends it, then by its signal, then killed with its process group."""
        if self.server is None:
            return
        try:
            self.server.stdin.close()
        except OSError:
            pass
        for then in (signal.SIGTERM, signal.SIGKILL, None):
            try:
                self.server.wait(END_LIMIT)
                break
            except subprocess.TimeoutExpired:
                if then is None:
                    break
                log(f"the server has not ended; sending it {then.name}")
            try:
                # mcp.sh passes a SIGTERM on and removes its clone after; SIGKILL takes its whole process group.
                os.killpg(self.server.pid, then) if then == signal.SIGKILL else self.server.send_signal(then)
            except ProcessLookupError:
                break
        log(f"ended (the server's status {self.server.returncode})")


def gone(connection):
    readable, _, _ = select.select([connection], [], [], 0)
    return bool(readable) and connection.recv(1, socket.MSG_PEEK) == b""


def log(said):
    print(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {said}", flush=True)


def main():
    if len(sys.argv) == 5 and sys.argv[1] == "--keep":
        Keeper(sys.argv[2], sys.argv[3], sys.argv[4]).run()
        return 0
    given_arguments = sys.argv[1:]
    start = os.getcwd()
    if given_arguments[:1] == ["--from"] and len(given_arguments) >= 2:
        start, given_arguments = given_arguments[1], given_arguments[2:]
    if not 1 <= len(given_arguments) <= 3 or given_arguments[0].startswith("-"):
        sys.exit(__doc__.split("\n\n")[-1].strip())
    folder, tool, text = (given_arguments + ["", ""])[:3]
    idle_minutes()
    return call(start, folder, tool, text)


if __name__ == "__main__":
    sys.exit(main())
