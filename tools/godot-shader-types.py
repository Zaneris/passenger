#!/usr/bin/env python3
"""Writes src/Passenger.Workspace/GodotShaderTypes.json, the table of Godot's shading language Passenger carries, from
Godot's own sources at one commit (MIT, named in THIRD_PARTY_NOTICES.md):

- servers/rendering/shader_types.cpp: each shader type's processor functions, the built-ins each offers (with "global"
  and "constants", offered in every function), the stage functions each may call, and its render and stencil modes;
- servers/rendering/shader_language.cpp: the uniform hints (its keywords between "// hints" and the list's end), the
  built-in functions (builtin_func_defs, but the constructors) and, for Passenger's compiler, every entry of it in order.
The table only proposes: the Godot found decides which candidates it takes. The sources are fetched once into
artifacts/godot-shader-types/<commit>/; COMMIT is Godot 4.8 beta 2's (godotengine/godot-builds names it).
"""
import json
import os
import re
import urllib.request

COMMIT = "cafc17a192acf67903910a68aeb3c076c0907a4b"
FILES = ["servers/rendering/shader_types.cpp", "servers/rendering/shader_language.cpp"]
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Passenger.Workspace", "GodotShaderTypes.json")

MODES = {"SHADER_SPATIAL": "spatial", "SHADER_CANVAS_ITEM": "canvas_item", "SHADER_PARTICLES": "particles",
         "SHADER_SKY": "sky", "SHADER_FOG": "fog", "SHADER_TEXTURE_BLIT": "texture_blit"}

BUILT_IN = re.compile(r'shader_modes\[RSE::(\w+)\]\.functions\["(\w+)"\]\.built_ins\["(\w+)"\]\s*=\s*(constt\(|constvt\()?ShaderLanguage::TYPE_(\w+)')
FLAG = re.compile(r'shader_modes\[RSE::(\w+)\]\.functions\["(\w+)"\]\.(main_function|can_discard)\s*=\s*(true|false)')
MODE = re.compile(r'shader_modes\[RSE::(\w+)\]\.(modes|stencil_modes)\.push_back\(\{\s*PNAME\("(\w+)"\)(.*)\}\);')
STAGE_ARGUMENT = re.compile(r'(\w+)\.arguments\.push_back\(ShaderLanguage::StageFunctionInfo::Argument\("(\w+)",\s*ShaderLanguage::TYPE_(\w+)\)\)')
STAGE_RETURN = re.compile(r'(\w+)\.return_type\s*=\s*ShaderLanguage::TYPE_(\w+)')
STAGE = re.compile(r'shader_modes\[RSE::(\w+)\]\.functions\["(\w+)"\]\.stage_functions\["(\w+)"\]\s*=\s*(\w+);')
STAGE_DECLARED = re.compile(r'ShaderLanguage::StageFunctionInfo\s+(\w+);')
KEYWORD = re.compile(r'\{\s*TK_\w+,\s*"(\w+)",')
FUNCTION = re.compile(r'\{\s*"(\w+)",\s*TYPE_(\w+),\s*\{([^}]*)\},\s*\{([^}]*)\},\s*TAG_(\w+),\s*(true|false)\s*\}')


def fetch(path):
    cache = os.path.join(ROOT, "artifacts", "godot-shader-types", COMMIT)
    os.makedirs(cache, exist_ok=True)
    local = os.path.join(cache, os.path.basename(path))
    if not os.path.exists(local):
        with urllib.request.urlopen(f"https://raw.githubusercontent.com/godotengine/godot/{COMMIT}/{path}") as response:
            data = response.read()
        with open(local, "wb") as out:
            out.write(data)
    with open(local, encoding="utf-8") as text:
        return text.read()


def type_name(token):
    return {"SAMPLER2D": "sampler2D", "ISAMPLER2D": "isampler2D", "USAMPLER2D": "usampler2D",
            "SAMPLER2DARRAY": "sampler2DArray", "ISAMPLER2DARRAY": "isampler2DArray", "USAMPLER2DARRAY": "usampler2DArray",
            "SAMPLER3D": "sampler3D", "ISAMPLER3D": "isampler3D", "USAMPLER3D": "usampler3D", "SAMPLERCUBE": "samplerCube",
            "SAMPLERCUBEARRAY": "samplerCubeArray", "SAMPLEREXT": "samplerExternalOES"}.get(token, token.lower())


def shader_types(source):
    types = {name: {"name": name, "functions": {}, "modes": [], "stencilModes": []} for name in MODES.values()}

    def function(mode, name):
        return types[MODES[mode]]["functions"].setdefault(name, {"name": name, "builtIns": [], "stageFunctions": []})

    stages = {}
    for line in source.splitlines():
        if m := BUILT_IN.search(line):
            function(m[1], m[2])["builtIns"].append({"name": m[3], "type": type_name(m[5]), "constant": m[4] is not None})
        elif m := FLAG.search(line):
            function(m[1], m[2])["main" if m[3] == "main_function" else "discards"] = m[4] == "true"
        elif m := MODE.search(line):
            options = re.findall(r'"(\w+)"', m[4])
            types[MODES[m[1]]]["modes" if m[2] == "modes" else "stencilModes"].append({"name": m[3], "options": options})
        elif m := STAGE_DECLARED.search(line):
            stages[m[1]] = {"arguments": [], "returns": "void"}
        elif m := STAGE_ARGUMENT.search(line):
            stages[m[1]]["arguments"].append({"name": m[2], "type": type_name(m[3])})
        elif m := STAGE_RETURN.search(line):
            stages[m[1]]["returns"] = type_name(m[2])
        elif m := STAGE.search(line):
            stage = stages[m[4]]
            function(m[1], m[2])["stageFunctions"].append({"name": m[3], "returns": stage["returns"], "arguments": list(stage["arguments"])})
    for kind in types.values():
        kind["functions"] = list(kind["functions"].values())
    return [types[name] for name in MODES.values()]


def hints(source):
    start = source.index("// hints")
    end = source.index("{ TK_ERROR, nullptr", start)
    return KEYWORD.findall(source[start:end])


def functions(source):
    start = source.index("ShaderLanguage::builtin_func_defs[] = {")
    end = source.index("{ nullptr,", start)
    found = []
    for m in FUNCTION.finditer(source[start:end]):
        arguments = [type_name(t.strip()[5:]) for t in m[3].split(",") if t.strip() and t.strip() != "TYPE_VOID"]
        names = [n.strip().strip('"') for n in m[4].split(",")]
        if names == [""]:
            continue
        found.append({"name": m[1], "returns": type_name(m[2]), "tag": m[5].lower(),
                      "arguments": [{"name": names[i] if i < len(names) else f"arg{i}", "type": t} for i, t in enumerate(arguments)]})
    return found


def builtins(source):
    start = source.index("ShaderLanguage::builtin_func_defs[] = {")
    end = source.index("{ nullptr,", start)
    # Its argument types as one string, so each entry is a line.
    return [{"name": m[1], "returns": type_name(m[2]), "takes": ", ".join(type_name(t.strip()[5:]) for t in m[3].split(",") if t.strip() and t.strip() != "TYPE_VOID"),
             "tag": m[5].lower(), "highEnd": m[6] == "true"} for m in FUNCTION.finditer(source[start:end])]


def written(value, depth):
    """JSON with a line for each built-in, mode and function, so a new commit's changes read line by line."""
    if isinstance(value, dict) and any(isinstance(v, list) and v and isinstance(v[0], (dict, str)) for v in value.values()):
        pad = " " * (depth + 1)
        return "{\n" + ",\n".join(f"{pad}{json.dumps(k)}: {written(v, depth + 1)}" for k, v in value.items()) + "\n" + " " * depth + "}"
    if isinstance(value, list) and value and isinstance(value[0], dict):
        pad = " " * (depth + 1)
        return "[\n" + ",\n".join(pad + written(v, depth + 1) for v in value) + "\n" + " " * depth + "]"
    return json.dumps(value, ensure_ascii=False)


def main():
    types_source, language_source = (fetch(path) for path in FILES)
    table = {
        "source": f"https://github.com/godotengine/godot/tree/{COMMIT}",
        "types": shader_types(types_source),
        "hints": hints(language_source),
        "functions": functions(language_source),
        "builtins": builtins(language_source),
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as out:
        out.write(written(table, 0) + "\n")
    count = sum(len(f["builtIns"]) for t in table["types"] for f in t["functions"])
    print(f"Wrote {os.path.relpath(OUT, ROOT)}: {count} built-ins, {len(table['hints'])} hints, {len(table['functions'])} functions, {len(table['builtins'])} entries")


if __name__ == "__main__":
    main()
