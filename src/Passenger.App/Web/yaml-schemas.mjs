// Passenger's preload for the YAML language server (node --import, Web/JsonSchemas.cs): the server reads a schema's file
// itself, so this has it ask Passenger for each instead (vscode/content, JsonSchemas.ContentAsync), as it asks for one over
// HTTP. Every import of the server's schema request handler is given this module, whose handler calls the server's own
// with a file system whose one read asks Passenger for the file by its URL, and says Passenger's refusal where the file
// names the schema rather than read it as an empty schema. A handler it cannot find ends the server as it starts.
import { createRequire, registerHooks } from "node:module";
import { realpathSync } from "node:fs";
import { pathToFileURL } from "node:url";

// The server as Node.js loads it, by its real path, and the handler its own import finds.
const server = pathToFileURL(realpathSync(process.argv[1]));
const handler = new URL("languageservice/services/schemaRequestHandler.js", server).href;
const own = await import(handler);
if (typeof own.schemaRequestHandler !== "function") {
    throw new Error(`Passenger cannot govern the YAML server's schema reads in ${handler}`);
}
// The server's own URI, the inverse of the fsPath its handler reads, so a share's or a host's path keeps its host.
const { URI } = createRequire(server)("vscode-uri");

registerHooks({
    resolve(specifier, context, nextResolve) {
        const resolved = nextResolve(specifier, context);
        return resolved.url === handler ? { ...resolved, url: import.meta.url } : resolved;
    },
});

export const workspaceContext = own.workspaceContext;

export async function schemaRequestHandler(connection, uri, workspaceFolders, workspaceRoot, useVSCodeContentRequest, fs, ...rest) {
    let refused;
    const asking = {
        readFile: (path) => connection.sendRequest("vscode/content", URI.file(path).toString()).catch((error) => {
            refused = error;
            throw error;
        }),
    };
    const content = await own.schemaRequestHandler(connection, uri, workspaceFolders, workspaceRoot, useVSCodeContentRequest, asking, ...rest);
    if (refused !== undefined) {
        // Said as the server says an answer over HTTP refused.
        throw typeof refused?.message === "string" ? refused.message : String(refused);
    }
    return content;
}
