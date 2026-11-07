using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App.CSharp;

/// <summary>A GDScript's Parameter Info from Passenger's own reading: the call the caret is in read from the text
/// (<see cref="GDScriptAsked"/>), its function resolved as the analyzer resolves the name (a script's function as written,
/// the engine's method or utility function as its API gives it, a signal's <c>emit</c>, a built-in type's constructor),
/// its signature, the parameter the caret is at, and the first of its words.</summary>
internal sealed partial class GDScriptAssist : IOwnSignatures
{
    public async Task<bool?> SignAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (!Answers(document) || context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return null;
        }
        GodotScriptTypes.Typed? typed = await types.TypedAsync(document);
        cancel.ThrowIfCancellationRequested();
        if (typed is null)
        {
            return Served(document) ? null : false;
        }
        if (GDScriptAsked.At(typed.Text, offset).Call is not { } call)
        {
            return false;
        }
        (GDScriptSignature? signature, Named? named) = CalledBy(typed, call);
        if (signature is null)
        {
            return Served(document) ? null : false;
        }
        card.AddCode(signature.ToString());
        IReadOnlyList<string> shown = signature.Shown;
        if (shown.Count > 0)
        {
            card.AddText(call.Argument < shown.Count ? shown[call.Argument] : signature.Varargs ? "..." : $"{call.Argument + 1} arguments given, of {shown.Count}");
        }
        if (named is not null && WordsOf(typed, named, ReferenceRead(types)) is { } words && GodotDocText.Parts(words, 1) is [{ Code: false } first, ..])
        {
            card.AddText(first.Text);
        }
        return true;
    }

    /// <summary>The signature of what a call calls, with what that is: its name as the script's reading resolves it, else
    /// as the text gives it (a half-written call parses as none), a signal's emit, a built-in type's constructor that takes
    /// as many arguments as given.</summary>
    private static (GDScriptSignature? Signature, Named? Named) CalledBy(GodotScriptTypes.Typed typed, GDScriptCallAt call)
    {
        GodotEngineApi? api = typed.Analyzer.Engine;
        if (call.Name == "emit" && call.Receiver is not null && call.NameStart >= 2
            && typed.Analysis.ValueBefore(call.NameStart - 1) is ({ } before, int from) && (before as GDScriptIdentifier ?? (before as GDScriptSubscript)?.Attribute) is { } signalName
            && NamedAt(typed, signalName, from + signalName.Start) is { } signalled && signalled is OfScript { Member.Kind: "signal" } or OfEngine { Kind: "signal" })
        {
            return (SignatureOf(typed, signalled), signalled);
        }
        Named? named = typed.Analysis.NodeAt(call.NameStart) is ({ } node, int offset) && node is GDScriptIdentifier name && name.Name == call.Name
            ? NamedAt(typed, name, offset + name.Start) : null;
        if (named is null && call.Receiver is null)
        {
            named = typed.Analyzer.Member(GDScriptAnalyzer.ScriptClass(typed.Tree, typed.Analysis.Path), call.Name) is { } own ? NamedOf(own, null)
                : api?.Functions.ContainsKey(call.Name) == true ? new OfEngine(GlobalScope, "func", call.Name, GDScriptType.Variant)
                : api?.Builtins.ContainsKey(call.Name) == true ? new OfClass(call.Name, "type")
                : null;
        }
        else if (named is null && ReceiverType(typed, call) is { } owner)
        {
            named = owner.Kind == GDScriptTypeKind.Builtin ? new OfEngine(owner.Name, "func", call.Name, GDScriptType.Variant)
                : typed.Analyzer.Member(owner, call.Name) is { } member ? NamedOf(member, owner) : null;
        }
        if (named is OfClass { Kind: "type" } builtin && api?.Builtins.GetValueOrDefault(builtin.Name) is { Constructors.Count: > 0 } made)
        {
            IReadOnlyList<(string Name, string Type)> fits = made.Constructors.FirstOrDefault(c => c.Count > call.Argument) ?? made.Constructors[^1];
            return (new GDScriptSignature(string.Empty, null, builtin.Name, [.. fits.Select(p => new GDScriptParameterShown(p.Name, typed.Analyzer.FromApi(p.Type).ToString(), null))],
                null, false), named);
        }
        return named is null ? (null, null) : (SignatureOf(typed, named), named);
    }
}
