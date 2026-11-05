using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Workspace.GDScript;

namespace Passenger.App.CSharp;

/// <summary>A GDScript's cards, the hover's and a list's item's alike: what a name names written as code (a function's or
/// signal's signature, a variable's type, a class's base), then its words: the engine's from Godot's class reference
/// (<see cref="GodotClassReference"/>), a script's member's from its <c>##</c> doc comment.</summary>
internal sealed partial class GDScriptAssist
{
    /// <summary>The class reference where it has been read, its reading begun where it has not: a card never waits for it.</summary>
    private static GodotClassReference? ReferenceRead(GodotScriptTypes types) =>
        types.ReferenceAsync() is { IsCompletedSuccessfully: true } read ? read.Result : null;

    public override bool Describe(CompletionItem item, IHoverCard card)
    {
        if ((offered.GetValueOrDefault(item)?.What ?? (GlobalsNamed.TryGetValue(item, out Named? global) ? global : null)) is not { } named
            || listedFrom is not { } typed || context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return false;
        }
        AddCard(card, typed, named, ReferenceRead(types));
        return true;
    }

    /// <summary>A name's card: what it names as code, then its words where there are some; whether there were.</summary>
    private static bool AddCard(IHoverCard card, GodotScriptTypes.Typed typed, Named named, GodotClassReference? reference)
    {
        if (named is OfCSharp csharp)
        {
            Describe(card, csharp.Member);
            return true;
        }
        card.AddCode(SaidOf(typed, named));
        if (WordsOf(typed, named, reference) is not { } words)
        {
            return false;
        }
        foreach (GodotDocPart part in GodotDocText.Parts(words, named is OfClass ? 2 : int.MaxValue))
        {
            if (part.Code)
            {
                card.AddCode(part.Text);
            }
            else
            {
                card.AddText(part.Text);
            }
        }
        return true;
    }

    /// <summary>What the documentation says of a name, as Godot's BBCode: the engine's class reference for its member or
    /// class (a class's brief, then its description), a script's doc comment for its member or its class; null for none.</summary>
    private static string? WordsOf(GodotScriptTypes.Typed typed, Named named, GodotClassReference? reference)
    {
        switch (named)
        {
            case OfEngine engine:
                return reference?.Of(engine.Owner == GlobalScope ? GodotClassReference.GlobalScope : engine.Owner, engine.Kind, engine.Name);
            case OfClass { Kind: "class" or "type" } engineClass:
                return reference?.Brief(engineClass.Name) is { } brief ? $"{brief}\n{reference.Description(engineClass.Name)}" : null;
            case OfClass { Kind: "class_name" } scripted:
                return typed.Analyzer.Project.ClassPath(scripted.Name) is { } path && typed.Analyzer.Project.Script(path) is { } tree ? GodotDocText.ScriptComment(tree) : null;
            case OfScript { Member.Declaration: { Script: { } script } declared }:
                GDScriptTree? declaring = script == typed.Analysis.Path ? typed.Tree : typed.Analyzer.Project.Script(script);
                return declaring is null ? null : GodotDocText.CommentAbove(declaring.Text, declared.Start);
            default:
                return null;
        }
    }

    /// <summary>What a name of a script names, read from its declaration: a script's member or local, the engine's member
    /// (a built-in type's after its value's dot), class, utility function or constant, a project's class or autoload; null
    /// where its reading resolves it to none of these.</summary>
    private static Named? NamedAt(GodotScriptTypes.Typed typed, GDScriptIdentifier name, int start)
    {
        GDScriptDeclaration? declared = typed.Analysis.DeclarationOf(name);
        GDScriptType type = declared?.Type ?? typed.Analysis.TypeOf(name);
        GodotEngineApi? api = typed.Analyzer.Engine;
        if (declared is { Script: { } script })
        {
            return new OfScript(new GDScriptMemberInfo(name.Name, GDScriptSignature.KindAt(script == typed.Analysis.Path ? typed.Tree : typed.Analyzer.Project.Script(script), declared.Start, type), type, declared));
        }
        if (declared is { EngineClass: { } engine })
        {
            return new OfEngine(engine, api is null ? "var" : EngineKind(api, engine, name.Name), name.Name, type);
        }
        string text = typed.Text;
        bool afterDot = start > 0 && start <= text.Length && text[start - 1] == '.';
        if (afterDot)
        {
            return api is not null && start >= 2 && typed.Analysis.NodeAt(start - 2) is ({ } before, _) && before is GDScriptExpression
                && typed.Analysis.TypeOf(before) is { Kind: GDScriptTypeKind.Builtin } builtin && api.Builtins.GetValueOrDefault(builtin.Name) is { } owner
                ? new OfEngine(builtin.Name, owner.Methods.ContainsKey(name.Name) ? "func" : owner.Constants.ContainsKey(name.Name) ? "const" : "var", name.Name, type)
                : null;
        }
        if (api is not null)
        {
            if (api.Classes.ContainsKey(name.Name))
            {
                return new OfClass(name.Name, "class");
            }
            if (api.Singletons.TryGetValue(name.Name, out string? singleton))
            {
                return new OfClass(singleton, "class");
            }
            if (api.Builtins.ContainsKey(name.Name))
            {
                return new OfClass(name.Name, "type");
            }
            if (api.Functions.ContainsKey(name.Name))
            {
                return new OfEngine(GlobalScope, "func", name.Name, type);
            }
            if (api.GlobalEnums.ContainsKey(name.Name))
            {
                return new OfEngine(GlobalScope, "enum", name.Name, type);
            }
            if (api.GlobalEnumValues.ContainsKey(name.Name) || api.GlobalConstants.Contains(name.Name))
            {
                return new OfEngine(GlobalScope, "const", name.Name, type);
            }
        }
        return typed.Analyzer.Project.ClassPath(name.Name) is not null ? new OfClass(name.Name, "class_name")
            : typed.Analyzer.Project.Autoload(name.Name) is not null ? new OfClass(name.Name, "autoload")
            : null;
    }

    /// <summary>What an engine class's member is, through the classes it extends.</summary>
    private static string EngineKind(GodotEngineApi api, string owner, string name)
    {
        foreach (GodotApiClass engine in api.Chain(owner))
        {
            if (engine.Methods.ContainsKey(name))
            {
                return "func";
            }
            if (engine.Properties.ContainsKey(name))
            {
                return "var";
            }
            if (engine.Signals.ContainsKey(name))
            {
                return "signal";
            }
            if (engine.Enums.ContainsKey(name))
            {
                return "enum";
            }
            if (engine.Constants.ContainsKey(name) || engine.Enums.Values.Any(e => e.Values.ContainsKey(name)))
            {
                return "const";
            }
        }
        return "var";
    }

    /// <summary>The class reference's place of the engine's class or member a script names at an offset, by Passenger's
    /// reading of it; null for the project's own, or a name its reading resolves to none.</summary>
    public async Task<Godot.GodotDocItem?> DocAtAsync(OpenDocument document, int offset)
    {
        if (!Answers(document) || context.GodotScriptTypes?.Invoke() is not { } types
            || (await types.TypedAsync(document))?.Analysis is not { } analysis || analysis.NodeAt(offset) is not ({ } node, int from) || node is not GDScriptIdentifier name)
        {
            return null;
        }
        return types.Latest(document) is { } typed ? NamedAt(typed, name, from + name.Start) switch
        {
            OfClass { Kind: "class" or "type" } named => new Godot.GodotDocItem(named.Name, null, null),
            OfEngine engine => new Godot.GodotDocItem(engine.Owner, engine.Kind switch
            {
                "func" => Godot.GodotDocs.Method,
                "var" => Godot.GodotDocs.Property,
                "signal" => Godot.GodotDocs.Signal,
                "enum" => Godot.GodotDocs.Enum,
                _ => Godot.GodotDocs.Constant,
            }, engine.Name),
            _ => null,
        } : null;
    }

    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (!Answers(document) || context.GodotScriptTypes?.Invoke() is not { } types)
        {
            return false;
        }
        GodotScriptTypes.Typed? typed = await types.TypedAsync(document);
        cancel.ThrowIfCancellationRequested();
        if (typed?.Analysis.NodeAt(offset) is not ({ } node, int from) || node is not GDScriptIdentifier name)
        {
            return false;
        }
        if (await CSharpMemberAtAsync(context, document, offset) is { } csharp)
        {
            cancel.ThrowIfCancellationRequested();
            Describe(card, csharp);
            return true;
        }
        GodotClassReference? read = ReferenceRead(types);
        if (NamedAt(typed, name, from + name.Start) is not { } named)
        {
            GDScriptType type = typed.Analysis.TypeOf(name);
            if (type.IsVariant && Served(document))
            {
                return false;
            }
            card.AddCode(type.IsVariant ? name.Name : $"{name.Name}: {type}");
            return true;
        }
        // The engine's words are Godot's server's to give until its class reference has been read.
        if (read is null && named is OfEngine or OfClass { Kind: "class" or "type" } && Served(document))
        {
            return false;
        }
        AddCard(card, typed, named, read);
        return true;
    }
}
