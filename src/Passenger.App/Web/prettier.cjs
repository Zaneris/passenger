// Passenger's Prettier runner (Web/Prettier.cs), through which a file's text reaches the project's own prettier package.
// One request per line on standard input, one answer per line on standard output, in turn, so a process stays warm for
// a folder and loads each package once:
//   { "id": 1, "prettier": "<the package's folder>", "file": "<the file's path>", "text": "...", "cursor": 12,
//     "ignorePath": "<the nearest .prettierignore>" | null, "rangeStart": 40, "rangeEnd": 90 (only a range formatted) }
// answered { "id": 1, "formatted": "...", "cursor": 10, "version": "3.9.9" }, or { "id": 1, "ignored": true } for a file
// .prettierignore names, { "id": 1, "unsupported": true } for one Prettier has no parser for, { "id": 1, "error": "..." }.
// The file's .prettierrc (any of Prettier's configuration files) and .editorconfig are read by Prettier itself. Standard
// output carries the answers alone: what a plugin or a JavaScript configuration prints goes to standard error.
"use strict";

const path = require("path");
const readline = require("readline");

const answerLine = process.stdout.write.bind(process.stdout);
// console.log and the rest look up the stream's write as they write, so they reach standard error from here on.
process.stdout.write = process.stderr.write.bind(process.stderr);

const loaded = new Map();

function load(folder) {
    let prettier = loaded.get(folder);
    if (prettier === undefined) {
        // The package's own entry (index.cjs from Prettier 3, index.js before), whose API is asynchronous from 3 on.
        prettier = require(folder);
        loaded.set(folder, prettier);
    }
    return prettier;
}

// The project the package belongs to, from which a configuration's plugins are found.
function projectOf(folder, file) {
    const parent = path.dirname(folder);
    return path.basename(parent) === "node_modules" ? path.dirname(parent) : path.dirname(file);
}

async function answer(request) {
    const prettier = load(request.prettier);
    process.chdir(projectOf(request.prettier, request.file));
    // Read afresh each time, so a .prettierrc or .editorconfig edited since is in force at once.
    if (typeof prettier.clearConfigCache === "function") {
        await prettier.clearConfigCache();
    }
    // A string, which Prettier 2 takes (it resolves it as a path) as well as 3 (a string or a list).
    const ignorePath = request.ignorePath || undefined;
    const info = await prettier.getFileInfo(request.file, { ignorePath, resolveConfig: true });
    if (info.ignored) {
        return { ignored: true, version: prettier.version };
    }
    if (!info.inferredParser) {
        return { unsupported: true, version: prettier.version };
    }
    const options = (await prettier.resolveConfig(request.file, { editorconfig: true })) || {};
    const cursor = Math.max(0, Math.min(request.cursor || 0, request.text.length));
    const range = typeof request.rangeEnd === "number" ? { rangeStart: request.rangeStart, rangeEnd: request.rangeEnd } : {};
    const result = await prettier.formatWithCursor(request.text, { ...options, ...range, filepath: request.file, cursorOffset: cursor });
    return { formatted: result.formatted, cursor: result.cursorOffset, version: prettier.version };
}

let queue = Promise.resolve();

readline.createInterface({ input: process.stdin, terminal: false }).on("line", (line) => {
    queue = queue.then(async () => {
        let id = null;
        let reply;
        try {
            const request = JSON.parse(line);
            id = request.id;
            reply = await answer(request);
        } catch (error) {
            reply = { error: error && error.message ? String(error.message) : String(error) };
        }
        answerLine(JSON.stringify({ id, ...reply }) + "\n");
    });
});
