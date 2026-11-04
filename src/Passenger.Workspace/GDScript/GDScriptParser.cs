using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>One error the parser found: Godot's own words and where it lies (offsets, its end exclusive).</summary>
public readonly record struct GDScriptError(string Message, int Start, int End);

/// <summary>GDScript parsed as Godot's own parser does (a port of modules/gdscript/gdscript_parser.cpp, MIT): a recursive
/// descent over <see cref="GDScriptTokenizer"/>'s tokens, expressions by precedence, the same errors in the same words,
/// recovering at the next statement as Godot's does. The script's own members are parsed a run at a time between the
/// clean line starts where one begins, and a parse may begin at one; node positions count from their run's start.</summary>
internal sealed partial class GDScriptParser
{
    [Flags]
    private enum Targets
    {
        None = 0,
        Script = 1,
        Class = 2,
        Variable = 4,
        Constant = 8,
        Signal = 16,
        Function = 32,
        Statement = 64,
        Standalone = 128,
        ClassLevel = Class | Variable | Constant | Signal | Function,
    }

    private enum Precedence
    {
        None,
        Assignment,
        Cast,
        Ternary,
        LogicOr,
        LogicAnd,
        LogicNot,
        ContentTest,
        Comparison,
        BitOr,
        BitXor,
        BitAnd,
        BitShift,
        AdditionSubtraction,
        Factor,
        Sign,
        BitNot,
        Power,
        TypeTest,
        Await,
        Call,
        Attribute,
        Subscript,
        Primary,
    }

    private readonly string text;
    private readonly GDScriptTokenizer tokenizer;
    private readonly List<GDScriptError> errors = [];
    private readonly List<bool> multilineStack = [];
    private readonly List<GDScriptAnnotation> annotationStack = [];
    private readonly List<GDScriptNode> inProgress = [];
    private GDScriptToken previous;
    private GDScriptToken current;
    private bool panic;
    private bool canBreak;
    private bool canContinue;
    private bool inLambda;
    private bool lambdaEnded;
    private GDScriptClass? currentClass;
    private GDScriptFunction? currentFunction;
    private GDScriptSuite? currentSuite;
    private int origin;

    private GDScriptParser(string text, int from, char indentChar)
    {
        this.text = text;
        tokenizer = new GDScriptTokenizer(text, from, indentChar);
        origin = from;
        previous = new GDScriptToken(K.Newline, Math.Max(0, from - 1), from);
    }

    /// <summary>Its errors so far, in the order found.</summary>
    public List<GDScriptError> Errors => errors;

    private static readonly Dictionary<string, (Targets Targets, int Arguments, int Defaults, bool Varargs)> KnownAnnotations = new(StringComparer.Ordinal)
    {
        ["@tool"] = (Targets.Script, 0, 0, false),
        ["@icon"] = (Targets.Script, 1, 0, false),
        ["@static_unload"] = (Targets.Script, 0, 0, false),
        ["@abstract"] = (Targets.Script | Targets.Class | Targets.Function, 0, 0, false),
        ["@onready"] = (Targets.Variable, 0, 0, false),
        ["@export"] = (Targets.Variable, 0, 0, false),
        ["@export_enum"] = (Targets.Variable, 1, 0, true),
        ["@export_file"] = (Targets.Variable, 1, 1, true),
        ["@export_file_path"] = (Targets.Variable, 1, 1, true),
        ["@export_dir"] = (Targets.Variable, 0, 0, false),
        ["@export_global_file"] = (Targets.Variable, 1, 1, true),
        ["@export_global_dir"] = (Targets.Variable, 0, 0, false),
        ["@export_multiline"] = (Targets.Variable, 1, 1, true),
        ["@export_placeholder"] = (Targets.Variable, 1, 0, false),
        ["@export_range"] = (Targets.Variable, 4, 2, true),
        ["@export_exp_easing"] = (Targets.Variable, 1, 1, true),
        ["@export_color_no_alpha"] = (Targets.Variable, 0, 0, false),
        ["@export_node_path"] = (Targets.Variable, 1, 1, true),
        ["@export_flags"] = (Targets.Variable, 1, 0, true),
        ["@export_flags_2d_render"] = (Targets.Variable, 0, 0, false),
        ["@export_flags_2d_physics"] = (Targets.Variable, 0, 0, false),
        ["@export_flags_2d_navigation"] = (Targets.Variable, 0, 0, false),
        ["@export_flags_3d_render"] = (Targets.Variable, 0, 0, false),
        ["@export_flags_3d_physics"] = (Targets.Variable, 0, 0, false),
        ["@export_flags_3d_navigation"] = (Targets.Variable, 0, 0, false),
        ["@export_flags_avoidance"] = (Targets.Variable, 0, 0, false),
        ["@export_storage"] = (Targets.Variable, 0, 0, false),
        ["@export_custom"] = (Targets.Variable, 3, 1, false),
        ["@export_tool_button"] = (Targets.Variable, 2, 1, false),
        ["@export_category"] = (Targets.Standalone, 1, 0, false),
        ["@export_group"] = (Targets.Standalone, 2, 1, false),
        ["@export_subgroup"] = (Targets.Standalone, 2, 1, false),
        ["@warning_ignore"] = (Targets.ClassLevel | Targets.Statement, 1, 0, true),
        ["@warning_ignore_start"] = (Targets.Standalone, 1, 0, true),
        ["@warning_ignore_restore"] = (Targets.Standalone, 1, 0, true),
        ["@rpc"] = (Targets.Function, 4, 4, false),
    };

    /// <summary>The annotations Godot takes at a class's level (before the script, a member, or standing alone), else those a
    /// statement takes, in the order Godot lists them.</summary>
    internal static IEnumerable<string> AnnotationNames(bool classLevel) =>
        KnownAnnotations.Where(a => (a.Value.Targets & (classLevel ? Targets.Script | Targets.ClassLevel | Targets.Standalone : Targets.Statement)) != 0).Select(a => a.Key);

    private static bool AppliesTo(GDScriptAnnotation annotation, Targets targets) =>
        KnownAnnotations.TryGetValue(annotation.Name, out (Targets Targets, int, int, bool) known) && (known.Targets & targets) != 0;

    // Positions and errors.

    private int Relative(int offset) => offset - origin;

    private void PushError(string message, GDScriptNode? node = null)
    {
        panic = true;
        errors.Add(node is null ? new GDScriptError(message, previous.Start, previous.End) : new GDScriptError(message, node.Start + origin, node.End + origin));
    }

    private void PushError(string message, GDScriptToken token)
    {
        panic = true;
        errors.Add(new GDScriptError(message, token.Start, token.End));
    }

    private T Alloc<T>() where T : GDScriptNode, new()
    {
        T node = new();
        ResetExtents(node, previous);
        inProgress.Add(node);
        return node;
    }

    private void Complete(GDScriptNode node)
    {
        while (inProgress.Count > 0 && inProgress[^1] != node)
        {
            inProgress.RemoveAt(inProgress.Count - 1);
        }
        if (inProgress.Count > 0)
        {
            inProgress.RemoveAt(inProgress.Count - 1);
        }
    }

    private void UpdateExtents(GDScriptNode node) => node.End = Relative(previous.End);

    private void ResetExtents(GDScriptNode node, GDScriptToken token)
    {
        node.Start = Relative(token.Start);
        node.End = Relative(token.End);
    }

    private static void ResetExtents(GDScriptNode node, GDScriptNode? from)
    {
        if (from is not null)
        {
            node.Start = from.Start;
            node.End = from.End;
        }
    }

    // Tokens.

    private GDScriptToken Advance()
    {
        lambdaEnded = false;
        if (current.Kind == K.Eof)
        {
            return current;
        }
        previous = current;
        current = tokenizer.Scan();
        while (current.Kind == K.Error)
        {
            PushError((string)current.Value!, current);
            current = tokenizer.Scan();
        }
        if (previous.Kind != K.Dedent)
        {
            foreach (GDScriptNode node in inProgress)
            {
                UpdateExtents(node);
            }
        }
        return previous;
    }

    private bool Match(K kind)
    {
        if (!Check(kind))
        {
            return false;
        }
        Advance();
        return true;
    }

    private bool Check(K kind) => kind == K.Identifier ? current.IsIdentifier : current.Kind == kind;

    private bool Consume(K kind, string error)
    {
        if (Match(kind))
        {
            return true;
        }
        PushError(error);
        return false;
    }

    private bool AtEnd => Check(K.Eof);

    private void Synchronize()
    {
        panic = false;
        while (!AtEnd)
        {
            if (previous.Kind is K.Newline or K.Semicolon)
            {
                return;
            }
            if (current.Kind is K.Class or K.Func or K.Static or K.Var or K.Const or K.Signal or K.For or K.While or K.Match or K.Return or K.Annotation)
            {
                return;
            }
            Advance();
        }
    }

    private void PushMultiline(bool state)
    {
        multilineStack.Add(state);
        tokenizer.Multiline = state;
        if (state)
        {
            while (current.Kind is K.Newline or K.Indent or K.Dedent)
            {
                current = tokenizer.Scan();
            }
        }
    }

    private void PopMultiline()
    {
        if (multilineStack.Count > 0)
        {
            multilineStack.RemoveAt(multilineStack.Count - 1);
        }
        tokenizer.Multiline = multilineStack.Count > 0 && multilineStack[^1];
    }

    private bool IsStatementEndToken => Check(K.Newline) || Check(K.Semicolon) || Check(K.Eof);

    private bool IsStatementEnd => lambdaEnded || inLambda || IsStatementEndToken;

    private void EndStatement(string context)
    {
        bool found = false;
        while (IsStatementEnd && !AtEnd)
        {
            if (IsStatementEndToken)
            {
                Advance();
            }
            else if (lambdaEnded)
            {
                lambdaEnded = false;
                found = true;
                break;
            }
            else
            {
                if (!found)
                {
                    lambdaEnded = true;
                    found = true;
                }
                break;
            }
            found = true;
        }
        if (!found && !AtEnd)
        {
            PushError($"Expected end of statement after {context}, found \"{GDScriptToken.KindName(current.Kind)}\" instead.");
        }
    }

    private string IdentifierName(GDScriptToken token) => token.Name ?? text[token.Start..token.End];

    private GDScriptIdentifier ParseIdentifier()
    {
        GDScriptIdentifier identifier = Alloc<GDScriptIdentifier>();
        Complete(identifier);
        identifier.Name = IdentifierName(previous);
        return identifier;
    }
}
