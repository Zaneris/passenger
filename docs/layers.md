# Layer rules

A folder's `passenger-layers.json`, at its top, puts the code into layers and says which layer's code may use which.
Passenger checks every open C# file against it as you write: a use of code in a layer its own may not use is a warning
where it is written, listed in Problems, whose fix (Show Context Actions) lets the one layer use the other in the file.
The Dependency Map draws each edge that breaks the rules in the error's colour, and its list of a node's edges says so.

## Format 1

```json
{
  "passengerLayers": 1,
  "layers": [
    { "name": "App", "projects": ["Shop.App"], "mayUse": ["Data", "Domain"] },
    { "name": "Data", "namespaces": ["Shop.Data.*"], "mayUse": ["Domain"] },
    { "name": "Domain", "folders": ["src/Domain/**"] }
  ]
}
```

- `passengerLayers` is the format's version, `1`. A Passenger refuses a file of a later format, saying so in the status bar.
- Each layer has a `name` and holds code by any of:
  - `projects`, by a project's name (its file's, without `.csproj`);
  - `namespaces`, by pattern: `*` stands for any run of a name's characters, `**` for any number of its parts, and a
    last part of `*` for the namespace before it and every namespace under it (`Shop.Data.*` holds `Shop.Data` and
    `Shop.Data.Sql`);
  - `folders`, by the folder of the file a type is declared in, named from the folder `passenger-layers.json` is in, in
    forward slashes, `*` and `**` as for namespaces (`src/Domain/**` holds `src/Domain` and every folder under it).
- Code is in the first layer that holds it, in the file's order; code in no layer is held to nothing, and every layer may use it.
- `mayUse` names the layers a layer's code may use beside its own. Code using code in any other layer breaks the rules.
- Comments and trailing commas are read as JSON with comments reads them.

A use is what the compiler binds: a type named anywhere in the code, a member called or read, a base type, an attribute,
an operator. The code using is placed by its project, its type's namespace and the file it is written in; the code used
by its project, its type's namespace and the file its type is first declared in.
