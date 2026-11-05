using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>Where a running game's node is: the nodes from the scene tree's root down to it (the root's Window left out),
/// each by its name and the scene it is the root of (empty for a node a scene writes inside it), and its script's
/// <c>res://</c> path, where it has one.</summary>
public sealed record GodotNodePlace(IReadOnlyList<(string Name, string Scene)> Chain, string? Script)
{
    /// <summary>The node's path from the tree's root ("Main/Hud/Score").</summary>
    public string Path => string.Join('/', Chain.Select(c => c.Name));
}

/// <summary>A value kept, as it will be written: the file, its text as read, the edits that write it, and the line (from
/// zero) shown before the write, as it reads now (null where the line is new) and as it will.</summary>
public sealed record GodotKept(string File, string Was, IReadOnlyList<TextEdit> Edits, int Line, string? Before, string After, bool InCode);

/// <summary>Where a value changed in a running game is kept (<see cref="Plan"/>): the scene that sets it, outermost first
/// (an instance's own setting is the one in force); else, for a value only the code gives, the C# script's
/// <c>[Export]</c> member's default; else the scene the node is written in, a line added to its node.</summary>
public static class GodotKeep
{
    /// <summary>How a value is written where it is kept: the scene's line, or the edits that keep it, from the file's text
    /// as <paramref name="read"/> reads a <c>res://</c> path (its file and text, open or on disk; null where there is
    /// none), a script parsed with its file's options. WhyNot says why it cannot be kept.</summary>
    public static (GodotKept? Kept, string? WhyNot) Plan(GodotNodePlace place, string property, object? value, GodotProperty? declared,
        Func<string, (string File, string Text)?> read, Func<string, CSharpParseOptions> parsing)
    {
        List<(string Scene, string Path)> scenes = [];
        for (int i = 0; i < place.Chain.Count; i++)
        {
            if (place.Chain[i].Scene.Length > 0)
            {
                string below = string.Join('/', place.Chain.Skip(i + 1).Select(c => c.Name));
                scenes.Add((place.Chain[i].Scene, below.Length == 0 ? "." : below));
            }
        }
        string node = place.Chain.Count > 0 ? place.Chain[^1].Name : place.Path;
        foreach ((string scene, string path) in scenes)
        {
            if (read(scene) is not ({ } file, { } text) || NodeIn(GodotFile.Parse(text), path) is not { } section || section.Property(property) is null)
            {
                continue;
            }
            return InScene(file, text, section, property, value, declared);
        }
        if (place.Script is { } script && script.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && read(script) is ({ } code, { } source)
            && InCode(code, source, parsing(code), property, value, declared) is { } kept)
        {
            return (kept, null);
        }
        // The scene the node is written in: its owner's, the nearest scene above it; the topmost scene's root its own.
        (string Scene, string Path)? owner = scenes.Count switch
        {
            0 => null,
            _ when scenes[^1].Path == "." && scenes.Count > 1 => scenes[^2],
            _ => scenes[^1],
        };
        if (owner is not { } at || read(at.Scene) is not ({ } ownerFile, { } ownerText))
        {
            return (null, $"{node} is in no scene of the project's: the game made it as it ran, so no scene keeps its values");
        }
        return NodeIn(GodotFile.Parse(ownerText), at.Path) is { } written
            ? InScene(ownerFile, ownerText, written, property, value, declared)
            : (null, $"{node} is not written in {System.IO.Path.GetFileName(ownerFile)}: the game made it as it ran, so no scene keeps its values");
    }

    /// <summary>How a value of a resource's own property is written where the resource is: its file's <c>[resource]</c> section
    /// (<c>res://grass.tres</c>), or its <c>[sub_resource]</c> in the scene or resource that holds it, by its id there
    /// (<c>res://main.tscn::StyleBoxFlat_x1y2</c>), read as <see cref="Plan"/> reads; WhyNot says why it cannot be kept.</summary>
    public static (GodotKept? Kept, string? WhyNot) InResource(string resource, string property, object? value, GodotProperty? declared,
        Func<string, (string File, string Text)?> read)
    {
        if (!resource.StartsWith("res://", StringComparison.Ordinal))
        {
            return (null, "its resource was made as the game ran, so no file of the project's keeps its values");
        }
        int split = resource.IndexOf("::", StringComparison.Ordinal);
        string path = split < 0 ? resource : resource[..split];
        string? id = split < 0 ? null : resource[(split + 2)..];
        if (read(path) is not ({ } file, { } text))
        {
            return (null, $"{path} is not a file of the project's");
        }
        GodotFile parsed = GodotFile.Parse(text);
        GodotSection? section = id is null ? parsed.Resource : parsed.Sections.FirstOrDefault(s => s.Tag == "sub_resource" && s.Text("id") == id);
        return section is null
            ? (null, id is null ? $"{System.IO.Path.GetFileName(file)} has no [resource] section to keep it in" : $"{System.IO.Path.GetFileName(file)} writes no resource {id} to keep it in")
            : InScene(file, text, section, property, value, declared);
    }

    /// <summary>The section of a scene that writes the node at a path from its root ("." for the root).</summary>
    public static GodotSection? NodeIn(GodotFile file, string path) => file.Nodes.FirstOrDefault(n => n.PathInScene == path)?.Section;

    /// <summary>A value written in a node's section of a scene: its property's value replaced where the section sets it,
    /// else a line added after the section's last; a resource by an external resource, the scene's own or one added.</summary>
    public static (GodotKept? Kept, string? WhyNot) InScene(string file, string text, GodotSection section, string property, object? value, GodotProperty? declared)
    {
        string lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<TextEdit> edits = [];
        string? written;
        if (declared is { Type: GodotType.Object } && value is string resource && resource.StartsWith("res://", StringComparison.Ordinal))
        {
            GodotFile parsed = GodotFile.Parse(text);
            string id;
            if (parsed.Externals.FirstOrDefault(e => e.Path == resource) is { } known)
            {
                id = known.Id;
            }
            else
            {
                (TextEdit added, id) = External(parsed, text, resource, declared, lineEnding);
                edits.Add(added);
                if (parsed.Header?.Attribute("load_steps") is { Value: { Word: { } word } steps } && int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                {
                    edits.Add(new TextEdit(steps.Start, steps.End - steps.Start, (count + 1).ToString(CultureInfo.InvariantCulture)));
                }
            }
            written = $"ExtResource(\"{id}\")";
        }
        else
        {
            written = GodotText.Scene(value);
        }
        if (written is null)
        {
            return (null, $"{property} holds a {declared?.Type.ToString() ?? "value"} a scene does not write as text");
        }
        TextSnapshot snapshot = TextSnapshot.From(text);
        int at;
        string? before;
        TextEdit edit;
        if (section.Property(property) is { } entry)
        {
            at = entry.Value.Start;
            before = LineText(snapshot, snapshot.GetLineFromOffset(at));
            edit = new TextEdit(entry.Value.Start, entry.Value.End - entry.Value.Start, written);
        }
        else
        {
            int last = section.Properties.Count > 0 ? section.Properties[^1].Value.End : section.End;
            int line = snapshot.GetLineFromOffset(last);
            at = snapshot.GetLineStart(line) + snapshot.GetLine(line).Length;
            before = null;
            edit = new TextEdit(at, 0, $"{lineEnding}{property} = {written}");
        }
        edits.Add(edit);
        List<TextEdit> ordered = [.. edits.OrderBy(e => e.Start)];
        TextSnapshot after = TextSnapshot.From(Applied(text, ordered));
        int shifted = ordered.Where(e => e.Start < edit.Start).Sum(e => e.Text.Length - e.Length);
        int newLine = after.GetLineFromOffset(edit.Start + shifted + (before is null ? lineEnding.Length : 0));
        return (new GodotKept(file, text, ordered, newLine, before, LineText(after, newLine), InCode: false), null);
    }

    /// <summary>An external resource added for a path after the scene's last (else after its header): the edit and its id,
    /// one more than the scene's highest number, with a suffix the path gives.</summary>
    private static (TextEdit Edit, string Id) External(GodotFile file, string text, string resource, GodotProperty declared, string lineEnding)
    {
        int highest = file.Externals.Select(e => int.TryParse(e.Id.Split('_')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0).DefaultIfEmpty(0).Max();
        string suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(resource)))[..5].ToLowerInvariant();
        string id = $"{highest + 1}_{suffix}";
        string type = declared.HintString.Split(',')[0].Trim() is { Length: > 0 } hinted ? hinted : "Resource";
        GodotSection? after = file.Sections.LastOrDefault(s => s.Tag == "ext_resource") ?? file.Header;
        TextSnapshot snapshot = TextSnapshot.From(text);
        int at = after is null ? 0 : snapshot.GetLineStart(snapshot.GetLineFromOffset(after.End)) + snapshot.GetLine(snapshot.GetLineFromOffset(after.End)).Length;
        string added = $"[ext_resource type=\"{type}\" path=\"{resource}\" id=\"{id}\"]";
        return (after is null ? new TextEdit(0, 0, added + lineEnding + lineEnding) : new TextEdit(at, 0, lineEnding + (file.Externals.Count == 0 ? lineEnding : string.Empty) + added), id);
    }

    /// <summary>A value written as the default of a C# script's <c>[Export]</c> member of the property's name: its
    /// initializer replaced, or one given to an auto-property or field that has none. The script is the file's class named
    /// as the file is, the one Godot runs, nested classes and any other beside it no part of it. Null where it has no such
    /// member, or its declared type takes no value written here.</summary>
    public static GodotKept? InCode(string file, string code, CSharpParseOptions options, string property, object? value, GodotProperty? declared)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(code, options).GetRoot();
        string script = System.IO.Path.GetFileNameWithoutExtension(file);
        IEnumerable<MemberDeclarationSyntax> members = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText == script).SelectMany(c => c.Members);
        foreach (MemberDeclarationSyntax member in members)
        {
            if (!member.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name.ToString() is "Export" or "ExportAttribute" or "Godot.Export" or "Godot.ExportAttribute"))
            {
                continue;
            }
            IReadOnlyList<GodotChoice>? choices = declared is { Hint: GodotHints.Enum } ? GodotHints.Choices(declared.HintString, numbered: true) : null;
            TextEdit? edit = null;
            switch (member)
            {
                case PropertyDeclarationSyntax { } declaredProperty when declaredProperty.Identifier.Text == property:
                    if (Written(value, declaredProperty.Type.ToString(), choices, declared) is not { } expression)
                    {
                        return null;
                    }
                    if (declaredProperty.Initializer is { } initializer)
                    {
                        edit = new TextEdit(initializer.Value.SpanStart, initializer.Value.Span.Length, expression);
                    }
                    else if (declaredProperty.AccessorList is { } accessors && accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null))
                    {
                        edit = new TextEdit(accessors.Span.End, 0, $" = {expression};");
                    }
                    break;
                case FieldDeclarationSyntax field when field.Declaration.Variables.FirstOrDefault(v => v.Identifier.Text == property) is { } variable:
                    if (Written(value, field.Declaration.Type.ToString(), choices, declared) is not { } fieldExpression)
                    {
                        return null;
                    }
                    edit = variable.Initializer is { } fieldInitializer
                        ? new TextEdit(fieldInitializer.Value.SpanStart, fieldInitializer.Value.Span.Length, fieldExpression)
                        : new TextEdit(variable.Identifier.Span.End, 0, $" = {fieldExpression}");
                    break;
            }
            if (edit is not { } made)
            {
                continue;
            }
            TextSnapshot before = TextSnapshot.From(code);
            int line = before.GetLineFromOffset(made.Start);
            TextSnapshot after = TextSnapshot.From(Applied(code, [made]));
            return new GodotKept(file, code, [made], line, LineText(before, line), LineText(after, line), InCode: true);
        }
        return null;
    }

    /// <summary>A value as C# writes it for a member of a declared type: a resource's path loaded as its type.</summary>
    private static string? Written(object? value, string type, IReadOnlyList<GodotChoice>? choices, GodotProperty? declared) =>
        declared is { Type: GodotType.Object } && value is string resource && resource.StartsWith("res://", StringComparison.Ordinal)
            ? $"GD.Load<{type}>({GodotText.CSharp(resource, "string")})"
            : GodotText.CSharp(value, type, choices);

    private static string LineText(TextSnapshot text, int line)
    {
        LineSpan span = text.GetLine(line);
        return text.GetText(span.Start, span.Length).TrimEnd('\r');
    }

    private static string Applied(string text, IReadOnlyList<TextEdit> edits)
    {
        System.Text.StringBuilder made = new(text);
        foreach (TextEdit edit in edits.OrderByDescending(e => e.Start))
        {
            made.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return made.ToString();
    }
}
