using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.App.Godot;

/// <summary>A GDScript's outline from Passenger's own parse of it (<see cref="GDScriptTree"/>): its signals, enums (their
/// values under them), constants, variables and functions in the order written, an inner class's members under it, each
/// named as written (a function's and signal's parameters after its name) with the type written after it. Read from the
/// open file's text as the editor has it, off the window's thread, whether or not Godot's server runs.</summary>
internal sealed class GDScriptOutline(TextDocument document) : FileOutline
{
    public override Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
    {
        TextSnapshot text = document.Snapshot;
        return BackgroundWork.Run(() => Of(text, path, before, cancel), cancel);
    }

    /// <summary>The outline of a script's text, its rows leading to <paramref name="path"/>.</summary>
    public static Outline<SymbolRow> Of(TextSnapshot text, string path, Outline<SymbolRow>? before = null, CancellationToken cancel = default)
    {
        string written = text.ToString();
        GDScriptTree tree = GDScriptTree.Parse(written);
        List<Part> roots = [.. tree.Members.Select(m => PartOf(m.Node, m.Offset)).OfType<Part>()];
        return Outline<SymbolRow>.Of<Part>(roots, part => part.Children, (part, depth) =>
        {
            int start = Math.Clamp(part.Start, 0, text.Length);
            int line = text.GetLineFromOffset(start);
            SymbolRow row = new(part.Name, part.Detail, null, part.Kind, path, new TextPosition(line, start - text.GetLineStart(line)), depth,
                text.GetLineFromOffset(Math.Clamp(part.End, start, text.Length)));
            return (row, row.Position.Line, row.EndLine ?? row.Position.Line);
        }, before, cancel);

        Part? PartOf(GDScriptNode node, int offset)
        {
            string? Written(GDScriptNode? part) => part is { Start: >= 0 } && offset + part.End <= written.Length ? written[(offset + part.Start)..(offset + part.End)] : null;
            string Typed(GDScriptNode? type) => Written(type) is { } shown ? $": {shown}" : string.Empty;
            switch (node)
            {
                case GDScriptFunction { Identifier: { } name } function:
                    GDScriptSignature signature = GDScriptSignature.Of(function, written, offset);
                    return new Part($"{name.Name}({string.Join(", ", signature.Shown)})", function.ReturnType is null ? null : Typed(function.ReturnType), SymbolKind.Method,
                        offset + name.Start, offset + node.End);
                case GDScriptSignal { Identifier: { } name } signal:
                    return new Part(signal.HasParentheses ? $"{name.Name}({string.Join(", ", GDScriptSignature.Of(signal, written, offset).Shown)})" : name.Name, null,
                        SymbolKind.Event, offset + name.Start, offset + node.End);
                case GDScriptConstant { Identifier: { } name } constant:
                    return new Part(name.Name, constant.Type is null ? null : Typed(constant.Type), SymbolKind.Constant, offset + name.Start, offset + node.End);
                case GDScriptVariable { Identifier: { } name } variable:
                    return new Part(name.Name, variable.Type is null ? null : Typed(variable.Type), variable.Property == GDScriptPropertyStyle.None ? SymbolKind.Field : SymbolKind.Property,
                        offset + name.Start, offset + node.End);
                case GDScriptEnum enumerated:
                    Part values = new(enumerated.Identifier?.Name ?? "enum", null, SymbolKind.Enum, offset + (enumerated.Identifier?.Start ?? node.Start), offset + node.End);
                    values.Children.AddRange(enumerated.Values.Select(v => new Part(v.Name.Name, null, SymbolKind.EnumMember, offset + v.Name.Start, offset + v.Name.End)));
                    return values;
                case GDScriptClass { Identifier: { } name } inner:
                    Part members = new(name.Name, inner.Extends.Count > 0 ? $": {string.Join('.', inner.Extends.Select(e => e.Name))}" : null, SymbolKind.Class,
                        offset + name.Start, offset + node.End);
                    members.Children.AddRange(inner.Members.Select(m => PartOf(m, offset)).OfType<Part>());
                    return members;
                default:
                    return null;
            }
        }
    }

    /// <summary>A member's row as it will be made: its name, the type after it, what it is, where its name starts and its
    /// declaration ends, and the members under it.</summary>
    private sealed record Part(string Name, string? Detail, SymbolKind Kind, int Start, int End)
    {
        public List<Part> Children { get; } = [];
    }
}
