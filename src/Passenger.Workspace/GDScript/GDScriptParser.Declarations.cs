using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>The script's top level, its classes and their members.</summary>
internal sealed partial class GDScriptParser
{
    private GDScriptClass head = new();
    private readonly List<GDScriptNode> headMembers = [];
    private readonly List<GDScriptAnnotation> regions = [];

    /// <summary>A parser of a whole text from its start.</summary>
    public static GDScriptParser Of(string text)
    {
        GDScriptParser parser = new(text, 0, '\0');
        parser.current = parser.tokenizer.Scan();
        while (parser.current.Kind is K.Error or K.Newline)
        {
            if (parser.current.Kind == K.Error)
            {
                parser.PushError((string)parser.current.Value!, parser.current);
            }
            parser.current = parser.tokenizer.Scan();
        }
        return parser;
    }

    /// <summary>A parser of the script's members from a clean line start (<see cref="AtMemberStart"/>), the file's
    /// indentation character as it stood there.</summary>
    public static GDScriptParser At(string text, int from, char indentChar)
    {
        GDScriptParser parser = new(text, from, indentChar);
        parser.currentClass = parser.head;
        parser.PushMultiline(false);
        parser.current = parser.tokenizer.Scan();
        while (parser.current.Kind == K.Error)
        {
            parser.PushError((string)parser.current.Value!, parser.current);
            parser.current = parser.tokenizer.Scan();
        }
        return parser;
    }

    /// <summary>The indentation character the file has chosen by where the parser stands.</summary>
    public char IndentChar => tokenizer.IndentChar;

    /// <summary>Where the parser stands: the start of its current token.</summary>
    public int Position => current.Start;

    /// <summary>The script's members parsed since it was last asked, their positions counted from where they began.</summary>
    public List<GDScriptNode> TakeMembers()
    {
        List<GDScriptNode> taken = [.. headMembers];
        headMembers.Clear();
        return taken;
    }

    /// <summary>The <c>@warning_ignore_start</c> and <c>@warning_ignore_restore</c> annotations parsed since it was last asked.</summary>
    public List<GDScriptAnnotation> TakeRegions()
    {
        List<GDScriptAnnotation> taken = [.. regions];
        regions.Clear();
        return taken;
    }

    /// <summary>The errors of warning regions, each annotation with the offset its positions count from, in the order
    /// written: a warning Godot has no name for, one begun twice, one ended that was not begun.</summary>
    public static IEnumerable<GDScriptError> RegionErrors(string text, IEnumerable<(GDScriptAnnotation Annotation, int Offset)> annotations)
    {
        Dictionary<string, int> begun = new(StringComparer.Ordinal);
        foreach ((GDScriptAnnotation annotation, int offset) in annotations)
        {
            bool start = annotation.Name == "@warning_ignore_start";
            foreach (GDScriptExpression argument in annotation.Arguments)
            {
                string warning = argument is GDScriptLiteral { Value: string named } ? named : string.Empty;
                string upper = warning.ToUpperInvariant();
                (int from, int to) = (annotation.Start + offset, annotation.End + offset);
                if (!WarningNames.Contains(upper))
                {
                    yield return new GDScriptError($"Invalid warning name: \"{warning}\".", from, to);
                }
                else if (start && begun.TryGetValue(upper, out int line))
                {
                    yield return new GDScriptError($"Warning \"{upper}\" is already being ignored by \"@warning_ignore_start\" at line {line}.", from, to);
                }
                else if (start)
                {
                    begun[upper] = text.AsSpan(0, Math.Min(from, text.Length)).Count('\n') + 1;
                }
                else if (!begun.Remove(upper))
                {
                    yield return new GDScriptError($"Warning \"{upper}\" is not being ignored by \"@warning_ignore_start\".", from, to);
                }
            }
        }
    }

    private static readonly HashSet<string> WarningNames = new(StringComparer.Ordinal)
    {
        "UNASSIGNED_VARIABLE", "UNASSIGNED_VARIABLE_OP_ASSIGN", "UNUSED_VARIABLE", "UNUSED_LOCAL_CONSTANT", "UNUSED_PRIVATE_CLASS_VARIABLE", "UNUSED_PARAMETER",
        "UNUSED_SIGNAL", "SHADOWED_VARIABLE", "SHADOWED_VARIABLE_BASE_CLASS", "SHADOWED_GLOBAL_IDENTIFIER", "UNREACHABLE_CODE", "UNREACHABLE_PATTERN",
        "STANDALONE_EXPRESSION", "STANDALONE_TERNARY", "INCOMPATIBLE_TERNARY", "UNTYPED_DECLARATION", "INFERRED_DECLARATION", "UNSAFE_PROPERTY_ACCESS",
        "UNSAFE_METHOD_ACCESS", "UNSAFE_CAST", "UNSAFE_CALL_ARGUMENT", "UNSAFE_VOID_RETURN", "RETURN_VALUE_DISCARDED", "STATIC_CALLED_ON_INSTANCE",
        "MISSING_TOOL", "REDUNDANT_STATIC_UNLOAD", "REDUNDANT_AWAIT", "MISSING_AWAIT", "ASSERT_ALWAYS_TRUE", "ASSERT_ALWAYS_FALSE", "INTEGER_DIVISION",
        "NARROWING_CONVERSION", "INT_AS_ENUM_WITHOUT_CAST", "INT_AS_ENUM_WITHOUT_MATCH", "ENUM_VARIABLE_WITHOUT_DEFAULT", "EMPTY_FILE", "DEPRECATED_KEYWORD",
        "CONFUSABLE_IDENTIFIER", "CONFUSABLE_LOCAL_DECLARATION", "CONFUSABLE_LOCAL_USAGE", "CONFUSABLE_CAPTURE_REASSIGNMENT",
        "CONFUSABLE_TEMPORARY_MODIFICATION", "INFERENCE_ON_VARIANT", "NATIVE_METHOD_OVERRIDE", "GET_NODE_DEFAULT_WITHOUT_ONREADY", "ONREADY_WITH_EXPORT",
        "ONREADY_WITH_CAST", "PROPERTY_USED_AS_FUNCTION", "CONSTANT_USED_AS_FUNCTION", "FUNCTION_USED_AS_PROPERTY",
    };

    /// <summary>The errors found since it was last asked.</summary>
    public List<GDScriptError> TakeErrors()
    {
        List<GDScriptError> taken = [.. errors];
        errors.Clear();
        return taken;
    }

    /// <summary>The script's own class as its first lines declare it: annotations, <c>class_name</c> and <c>extends</c>.</summary>
    public GDScriptClass ParsePreamble()
    {
        head = new GDScriptClass { Start = 0, End = 0 };
        currentClass = head;
        PushMultiline(false);
        bool classOrExtends = true;
        while (!Check(K.Eof))
        {
            if (Match(K.Annotation))
            {
                GDScriptAnnotation? annotation = ParseAnnotation(Targets.Script | Targets.ClassLevel | Targets.Standalone);
                if (annotation is null)
                {
                    continue;
                }
                if (AppliesTo(annotation, Targets.Class))
                {
                    annotationStack.Add(annotation);
                }
                else if (AppliesTo(annotation, Targets.Script))
                {
                    PendingToHead();
                    if (annotation.Name is "@tool" or "@icon" or "@static_unload")
                    {
                        ApplyScriptAnnotation(annotation);
                    }
                    head.Annotate([annotation]);
                }
                else if (AppliesTo(annotation, Targets.Standalone))
                {
                    if (previous.Kind != K.Newline)
                    {
                        PushError("Expected newline after a standalone annotation.");
                    }
                    if (annotation.Name is "@export_category" or "@export_group" or "@export_subgroup")
                    {
                        headMembers.Add(annotation);
                        classOrExtends = false;
                        break;
                    }
                    if (annotation.Name is "@warning_ignore_start" or "@warning_ignore_restore")
                    {
                        regions.Add(annotation);
                    }
                    else
                    {
                        PushError("Unexpected standalone annotation.");
                    }
                }
                else
                {
                    annotationStack.Add(annotation);
                    classOrExtends = false;
                    break;
                }
            }
            else if (!ParseStandaloneString())
            {
                break;
            }
        }
        if (current.Kind is K.ClassName or K.Extends)
        {
            ResetExtents(head, current);
        }
        while (classOrExtends)
        {
            switch (current.Kind)
            {
                case K.ClassName:
                    PendingToHead();
                    Advance();
                    if (head.Identifier is not null)
                    {
                        PushError("\"class_name\" can only be used once.");
                    }
                    else
                    {
                        ParseClassName();
                    }
                    break;
                case K.Extends:
                    PendingToHead();
                    Advance();
                    if (head.ExtendsUsed)
                    {
                        PushError("\"extends\" can only be used once.");
                    }
                    else
                    {
                        ParseExtends();
                        EndStatement("superclass");
                    }
                    break;
                case K.Eof:
                    PendingToHead();
                    classOrExtends = false;
                    break;
                case K.Literal when ParseStandaloneString():
                    break;
                default:
                    classOrExtends = false;
                    break;
            }
            if (panic)
            {
                Synchronize();
            }
        }
        foreach (GDScriptAnnotation annotation in head.Annotations.Where(a => a.Name == "@abstract"))
        {
            if (head.IsAbstract)
            {
                PushError("\"@abstract\" annotation can only be used once per class.", annotation);
            }
            head.IsAbstract = true;
        }
        return head;
    }

    private void PendingToHead()
    {
        head.Annotate(annotationStack);
        annotationStack.Clear();
    }

    private void ApplyScriptAnnotation(GDScriptAnnotation annotation)
    {
        switch (annotation.Name)
        {
            case "@tool":
                if (head.IsTool)
                {
                    PushError("\"@tool\" annotation can only be used once.", annotation);
                }
                head.IsTool = true;
                break;
            case "@icon":
                string path = annotation.Arguments is [GDScriptLiteral { Value: string written }] ? written : string.Empty;
                if (head.IconPath is not null)
                {
                    PushError("\"@icon\" annotation can only be used once.", annotation);
                }
                else if (path.Length == 0)
                {
                    PushError("\"@icon\" annotation argument must contain the path to the icon.", annotation.Arguments[0]);
                }
                else
                {
                    head.IconPath = path;
                }
                break;
            default:
                break;
        }
    }

    /// <summary>Whether the parser stands at a clean start of one of the script's members: a member's keyword at a line's
    /// start, nothing pending, so a parser begun there parses on as this one does.</summary>
    private bool AtMemberStart(bool nextIsStatic) =>
        !nextIsStatic && !panic && annotationStack.Count == 0 && inProgress.Count == 0 && previous.Kind is K.Newline or K.Dedent
        && current.Kind is K.Func or K.Var or K.Const or K.Signal or K.Class or K.Enum or K.Static or K.Annotation
        && (current.Start == 0 || text[current.Start - 1] == '\n') && tokenizer.Clean;

    /// <summary>The script's members to its end, or until <paramref name="boundary"/>, told each clean member start past the
    /// first (its offset), says to stop there. Positions count from the last such start.</summary>
    public void ParseBody(Func<int, bool> boundary)
    {
        currentClass = head;
        ParseClassBody(true, boundary);
    }

    /// <summary>What the end of the script says: anything left past the body, and annotations nothing took.</summary>
    public void Finish()
    {
        if (!Check(K.Eof))
        {
            PushError("Expected end of file.");
        }
        ClearUnusedAnnotations();
    }

    /// <summary>Starts counting positions from an offset, the start of the run of members parsed next.</summary>
    public void CountFrom(int offset) => origin = offset;

    private GDScriptClass? ParseClass(bool isStatic)
    {
        GDScriptClass node = Alloc<GDScriptClass>();
        GDScriptClass? outer = currentClass;
        currentClass = node;
        if (Consume(K.Identifier, "Expected identifier for the class name after \"class\"."))
        {
            node.Identifier = ParseIdentifier();
        }
        if (Match(K.Extends))
        {
            ParseExtends();
        }
        Consume(K.Colon, "Expected \":\" after class declaration.");
        bool multiline = Match(K.Newline);
        if (multiline && !Consume(K.Indent, "Expected indented block after class declaration."))
        {
            currentClass = outer;
            Complete(node);
            return node;
        }
        if (Match(K.Extends))
        {
            if (node.ExtendsUsed)
            {
                PushError("Cannot use \"extends\" more than once in the same class.");
            }
            ParseExtends();
            EndStatement("superclass");
        }
        int outerNesting = nesting;
        Deeper();
        ParseClassBody(multiline, null);
        nesting = outerNesting;
        Complete(node);
        if (multiline)
        {
            Consume(K.Dedent, "Missing unindent at the end of the class body.");
        }
        errors.AddRange(Duplicates(node.Members.Select(m => (m, origin))));
        currentClass = outer;
        return node;
    }

    /// <summary>The errors of members named as one before them was (in the order written), each member with the offset
    /// its positions count from: Godot's own words.</summary>
    public static IEnumerable<GDScriptError> Duplicates(IEnumerable<(GDScriptNode Member, int Offset)> members)
    {
        Dictionary<string, string> declared = new(StringComparer.Ordinal);
        foreach ((GDScriptNode member, int offset) in members)
        {
            if (member is GDScriptEnum { Identifier: null } unnamed)
            {
                HashSet<string> own = new(StringComparer.Ordinal);
                foreach ((GDScriptIdentifier value, _) in unnamed.Values)
                {
                    if (own.Add(value.Name) && declared.TryGetValue(value.Name, out string? was))
                    {
                        yield return new GDScriptError($"Name \"{value.Name}\" is already used as a class {was}.", value.Start + offset, value.End + offset);
                    }
                    declared[value.Name] = "enum value";
                }
                continue;
            }
            (GDScriptIdentifier? name, string? kind) = member switch
            {
                GDScriptVariable v => (v.Identifier, "variable"),
                GDScriptConstant c => (c.Identifier, "constant"),
                GDScriptSignal s => (s.Identifier, "signal"),
                GDScriptFunction f => (f.Identifier, "function"),
                GDScriptClass c => (c.Identifier, "class"),
                GDScriptEnum e => (e.Identifier, "enum"),
                _ => (null, null),
            };
            if (name is null || kind is null)
            {
                continue;
            }
            if (declared.TryGetValue(name.Name, out string? before))
            {
                yield return new GDScriptError($"{char.ToUpperInvariant(kind[0])}{kind[1..]} \"{name.Name}\" has the same name as a previously declared {before}.",
                    name.Start + offset, name.End + offset);
            }
            else
            {
                declared[name.Name] = kind;
            }
        }
    }

    private void ParseClassName()
    {
        if (Consume(K.Identifier, "Expected identifier for the global class name after \"class_name\"."))
        {
            currentClass!.Identifier = ParseIdentifier();
        }
        if (Match(K.Extends))
        {
            ParseExtends();
            EndStatement("superclass");
        }
        else
        {
            EndStatement("class_name statement");
        }
    }

    private void ParseExtends()
    {
        GDScriptClass node = currentClass!;
        node.ExtendsUsed = true;
        if (Match(K.Literal))
        {
            if (previous.Literal != GDScriptLiteralKind.String)
            {
                PushError($"Only strings or identifiers can be used after \"extends\", found \"{VariantName(previous.Literal)}\" instead.");
            }
            node.ExtendsPath = previous.Value as string ?? string.Empty;
            if (!Match(K.Period))
            {
                return;
            }
        }
        if (!Consume(K.Identifier, "Expected superclass name after \"extends\"."))
        {
            return;
        }
        node.Extends.Add(ParseIdentifier());
        while (Match(K.Period))
        {
            if (!Consume(K.Identifier, "Expected superclass name after \".\"."))
            {
                return;
            }
            node.Extends.Add(ParseIdentifier());
        }
    }

    private static string VariantName(GDScriptLiteralKind kind) => kind switch
    {
        GDScriptLiteralKind.String => "String",
        GDScriptLiteralKind.StringName => "StringName",
        GDScriptLiteralKind.NodePath => "NodePath",
        GDScriptLiteralKind.Int => "int",
        GDScriptLiteralKind.Float => "float",
        GDScriptLiteralKind.Bool => "bool",
        _ => "Nil",
    };

    private void ParseClassMember(Func<bool, GDScriptNode?> parse, Targets target, string kind, bool isStatic = false)
    {
        Advance();
        List<GDScriptAnnotation> annotations = [];
        while (annotationStack.Count > 0)
        {
            GDScriptAnnotation last = annotationStack[^1];
            if (AppliesTo(last, target))
            {
                annotations.Insert(0, last);
                annotationStack.RemoveAt(annotationStack.Count - 1);
            }
            else
            {
                PushError($"Annotation \"{last.Name}\" cannot be applied to a {kind}.");
                ClearUnusedAnnotations();
            }
        }
        GDScriptNode? member = parse(isStatic);
        if (member is null)
        {
            return;
        }
        member.Annotate(annotations);
        foreach (GDScriptAnnotation annotation in annotations.Where(a => a.Name == "@abstract"))
        {
            ApplyAbstract(annotation, member);
        }
        if (currentClass == head)
        {
            headMembers.Add(member);
        }
        else
        {
            currentClass!.Members.Add(member);
        }
    }

    private void ApplyAbstract(GDScriptAnnotation annotation, GDScriptNode target)
    {
        if (target is GDScriptClass node)
        {
            if (node.IsAbstract)
            {
                PushError("\"@abstract\" annotation can only be used once per class.", annotation);
            }
            node.IsAbstract = true;
        }
        else if (target is GDScriptFunction function)
        {
            if (function.IsStatic)
            {
                PushError("\"@abstract\" annotation cannot be applied to static functions.", annotation);
            }
            else if (function.IsAbstract)
            {
                PushError("\"@abstract\" annotation can only be used once per function.", annotation);
            }
            function.IsAbstract = true;
        }
    }

    private void ParseClassBody(bool multiline, Func<int, bool>? boundary)
    {
        bool end = false;
        bool nextIsStatic = false;
        bool first = true;
        while (!end && !AtEnd)
        {
            if (boundary is not null && !first && AtMemberStart(nextIsStatic) && boundary(current.Start))
            {
                return;
            }
            first = false;
            GDScriptToken token = current;
            switch (token.Kind)
            {
                case K.Var:
                    ParseClassMember(s => ParseVariable(s, true), Targets.Variable, "variable", nextIsStatic);
                    break;
                case K.Const:
                    ParseClassMember(ParseConstant, Targets.Constant, "constant");
                    break;
                case K.Signal:
                    ParseClassMember(_ => ParseSignal(), Targets.Signal, "signal");
                    break;
                case K.Func:
                    ParseClassMember(ParseFunction, Targets.Function, "function", nextIsStatic);
                    break;
                case K.Class:
                    ParseClassMember(ParseClass, Targets.Class, "class");
                    break;
                case K.Enum:
                    ParseClassMember(_ => ParseEnum(), Targets.None, "enum");
                    break;
                case K.Static:
                    Advance();
                    nextIsStatic = true;
                    if (!Check(K.Func) && !Check(K.Var))
                    {
                        PushError("Expected \"func\" or \"var\" after \"static\".");
                    }
                    break;
                case K.Annotation:
                    Advance();
                    if (ParseAnnotation(Targets.ClassLevel | Targets.Standalone) is { } annotation)
                    {
                        if (AppliesTo(annotation, Targets.Standalone))
                        {
                            if (previous.Kind != K.Newline)
                            {
                                PushError("Expected newline after a standalone annotation.");
                            }
                            if (annotation.Name is "@export_category" or "@export_group" or "@export_subgroup")
                            {
                                if (currentClass == head)
                                {
                                    headMembers.Add(annotation);
                                }
                                else
                                {
                                    currentClass!.Members.Add(annotation);
                                }
                            }
                            else if (annotation.Name is "@warning_ignore_start" or "@warning_ignore_restore")
                            {
                                regions.Add(annotation);
                            }
                            else
                            {
                                PushError("Unexpected standalone annotation.");
                            }
                        }
                        else
                        {
                            annotationStack.Add(annotation);
                        }
                    }
                    break;
                case K.Pass:
                    Advance();
                    EndStatement("\"pass\"");
                    break;
                case K.Dedent:
                    end = true;
                    break;
                case K.Literal when ParseStandaloneString():
                    break;
                default:
                    Advance();
                    PushError(previous.Name switch
                    {
                        "export" => "The \"export\" keyword was removed in Godot 4. Use an export annotation (\"@export\", \"@export_range\", etc.) instead.",
                        "tool" => "The \"tool\" keyword was removed in Godot 4. Use the \"@tool\" annotation instead.",
                        "onready" => "The \"onready\" keyword was removed in Godot 4. Use the \"@onready\" annotation instead.",
                        "remote" => "The \"remote\" keyword was removed in Godot 4. Use the \"@rpc\" annotation with \"any_peer\" instead.",
                        "remotesync" => "The \"remotesync\" keyword was removed in Godot 4. Use the \"@rpc\" annotation with \"any_peer\" and \"call_local\" instead.",
                        "puppet" => "The \"puppet\" keyword was removed in Godot 4. Use the \"@rpc\" annotation with \"authority\" instead.",
                        "puppetsync" => "The \"puppetsync\" keyword was removed in Godot 4. Use the \"@rpc\" annotation with \"authority\" and \"call_local\" instead.",
                        "master" => "The \"master\" keyword was removed in Godot 4. Use the \"@rpc\" annotation with \"any_peer\" and perform a check inside the function instead.",
                        "mastersync" => "The \"mastersync\" keyword was removed in Godot 4. Use the \"@rpc\" annotation with \"any_peer\" and \"call_local\", and perform a check inside the function instead.",
                        _ => $"Unexpected {previous.DebugName} in class body.",
                    });
                    break;
            }
            if (token.Kind != K.Static)
            {
                nextIsStatic = false;
            }
            if (panic)
            {
                Synchronize();
            }
            if (!multiline)
            {
                end = true;
            }
        }
    }

    private GDScriptVariable? ParseVariable(bool isStatic, bool allowProperty)
    {
        GDScriptVariable variable = Alloc<GDScriptVariable>();
        if (!Consume(K.Identifier, "Expected variable name after \"var\"."))
        {
            Complete(variable);
            return null;
        }
        variable.Identifier = ParseIdentifier();
        variable.IsStatic = isStatic;
        if (Match(K.Colon))
        {
            if (Check(K.Newline))
            {
                if (allowProperty)
                {
                    Advance();
                    return ParseProperty(variable, true);
                }
                PushError("Expected type after \":\"");
                Complete(variable);
                return null;
            }
            if (Check(K.Equal))
            {
                variable.Infers = true;
            }
            else
            {
                if (allowProperty && Check(K.Identifier) && current.Name is "get" or "set")
                {
                    return ParseProperty(variable, false);
                }
                variable.Type = ParseType();
            }
        }
        if (Match(K.Equal))
        {
            variable.Initializer = ParseExpression(false);
            if (variable.Initializer is null)
            {
                PushError("Expected expression for variable initial value after \"=\".");
            }
        }
        if (allowProperty && Match(K.Colon))
        {
            return ParseProperty(variable, Match(K.Newline));
        }
        Complete(variable);
        EndStatement("variable declaration");
        return variable;
    }

    private GDScriptVariable? ParseProperty(GDScriptVariable variable, bool needIndent)
    {
        if (needIndent && !Consume(K.Indent, "Expected indented block for property after \":\"."))
        {
            Complete(variable);
            return null;
        }
        if (!Consume(K.Identifier, "Expected \"get\" or \"set\" for property declaration."))
        {
            Complete(variable);
            return null;
        }
        GDScriptIdentifier function = ParseIdentifier();
        if (Check(K.Equal))
        {
            variable.Property = GDScriptPropertyStyle.SetGet;
        }
        else
        {
            variable.Property = GDScriptPropertyStyle.Inline;
            if (!needIndent)
            {
                PushError("Property with inline code must go to an indented block.");
            }
        }
        bool getter = false;
        bool setter = false;
        for (int i = 0; i < 2; i++)
        {
            if (function.Name == "set")
            {
                if (setter)
                {
                    PushError("Properties can only have one setter.");
                }
                else
                {
                    ParseSetter(variable);
                    setter = true;
                }
            }
            else if (function.Name == "get")
            {
                if (getter)
                {
                    PushError("Properties can only have one getter.");
                }
                else
                {
                    ParseGetter(variable);
                    getter = true;
                }
            }
            else
            {
                PushError("Expected \"get\" or \"set\" for property declaration.");
            }
            if (i == 0 && variable.Property == GDScriptPropertyStyle.SetGet)
            {
                if (Match(K.Comma))
                {
                    if (Match(K.Newline) && !needIndent)
                    {
                        PushError("Inline setter/getter setting cannot span across multiple lines (use \"\\\\\" if needed).");
                    }
                }
                else
                {
                    break;
                }
            }
            if (!Match(K.Identifier))
            {
                break;
            }
            function = ParseIdentifier();
        }
        Complete(variable);
        if (variable.Property == GDScriptPropertyStyle.SetGet)
        {
            EndStatement("property declaration");
        }
        if (needIndent)
        {
            Consume(K.Dedent, "Expected end of indented block for property.");
        }
        return variable;
    }

    private void ParseSetter(GDScriptVariable variable)
    {
        if (variable.Property == GDScriptPropertyStyle.SetGet)
        {
            Consume(K.Equal, "Expected \"=\" after \"set\"");
            if (Consume(K.Identifier, "Expected setter function name after \"=\"."))
            {
                variable.SetterName = ParseIdentifier();
            }
            return;
        }
        GDScriptFunction function = Alloc<GDScriptFunction>();
        function.IsStatic = variable.IsStatic;
        Consume(K.ParenthesisOpen, "Expected \"(\" after \"set\".");
        GDScriptParameter parameter = Alloc<GDScriptParameter>();
        if (Consume(K.Identifier, "Expected parameter name after \"(\"."))
        {
            ResetExtents(parameter, previous);
            parameter.Identifier = ParseIdentifier();
            function.Parameters.Add(parameter);
        }
        Complete(parameter);
        Consume(K.ParenthesisClose, "Expected \")\" after parameter name.");
        Consume(K.Colon, "Expected \":\" after \")\".");
        function.HeaderEnd = Relative(previous.Start);
        GDScriptFunction? outer = currentFunction;
        currentFunction = function;
        if (parameter.Identifier is not null)
        {
            GDScriptSuite body = Alloc<GDScriptSuite>();
            body.AddLocal(parameter.Identifier.Name, "parameter", parameter);
            function.Body = ParseSuite("setter declaration", body);
            variable.Setter = function;
        }
        currentFunction = outer;
        Complete(function);
    }

    private void ParseGetter(GDScriptVariable variable)
    {
        if (variable.Property == GDScriptPropertyStyle.SetGet)
        {
            Consume(K.Equal, "Expected \"=\" after \"get\"");
            if (Consume(K.Identifier, "Expected getter function name after \"=\"."))
            {
                variable.GetterName = ParseIdentifier();
            }
            return;
        }
        GDScriptFunction function = Alloc<GDScriptFunction>();
        if (Match(K.ParenthesisOpen))
        {
            Consume(K.ParenthesisClose, "Expected \")\" after \"get(\".");
            Consume(K.Colon, "Expected \":\" after \"get()\".");
        }
        else
        {
            Consume(K.Colon, "Expected \":\" or \"(\" after \"get\".");
        }
        function.HeaderEnd = Relative(previous.Start);
        function.IsStatic = variable.IsStatic;
        GDScriptFunction? outer = currentFunction;
        currentFunction = function;
        GDScriptSuite body = Alloc<GDScriptSuite>();
        function.Body = ParseSuite("getter declaration", body);
        variable.Getter = function;
        currentFunction = outer;
        Complete(function);
    }

    private GDScriptConstant? ParseConstant(bool isStatic)
    {
        GDScriptConstant constant = Alloc<GDScriptConstant>();
        if (!Consume(K.Identifier, "Expected constant name after \"const\"."))
        {
            Complete(constant);
            return null;
        }
        constant.Identifier = ParseIdentifier();
        if (Match(K.Colon))
        {
            if (Check(K.Equal))
            {
                constant.Infers = true;
            }
            else
            {
                constant.Type = ParseType();
            }
        }
        if (!Consume(K.Equal, "Expected initializer after constant name."))
        {
            Complete(constant);
            return null;
        }
        constant.Initializer = ParseExpression(false);
        if (constant.Initializer is null)
        {
            PushError("Expected initializer expression for constant.");
            Complete(constant);
            return null;
        }
        Complete(constant);
        EndStatement("constant declaration");
        return constant;
    }

    private GDScriptParameter? ParseParameter()
    {
        if (!Consume(K.Identifier, "Expected parameter name."))
        {
            return null;
        }
        GDScriptParameter parameter = Alloc<GDScriptParameter>();
        parameter.Identifier = ParseIdentifier();
        if (Match(K.Colon))
        {
            if (Check(K.Equal))
            {
                parameter.Infers = true;
            }
            else
            {
                parameter.Type = ParseType();
            }
        }
        if (Match(K.Equal))
        {
            parameter.Initializer = ParseExpression(false);
        }
        Complete(parameter);
        return parameter;
    }

    private GDScriptSignal? ParseSignal()
    {
        GDScriptSignal signal = Alloc<GDScriptSignal>();
        if (!Consume(K.Identifier, "Expected signal name after \"signal\"."))
        {
            Complete(signal);
            return null;
        }
        signal.Identifier = ParseIdentifier();
        if (Check(K.ParenthesisOpen))
        {
            signal.HasParentheses = true;
            PushMultiline(true);
            Advance();
            HashSet<string> names = new(StringComparer.Ordinal);
            do
            {
                if (Check(K.ParenthesisClose))
                {
                    break;
                }
                GDScriptParameter? parameter = ParseParameter();
                if (parameter is null)
                {
                    PushError("Expected signal parameter name.");
                    break;
                }
                if (parameter.Initializer is not null)
                {
                    PushError("Signal parameters cannot have a default value.");
                }
                if (!names.Add(parameter.Identifier!.Name))
                {
                    PushError($"Parameter with name \"{parameter.Identifier.Name}\" was already declared for this signal.");
                }
                else
                {
                    signal.Parameters.Add(parameter);
                }
            }
            while (Match(K.Comma) && !AtEnd);
            PopMultiline();
            Consume(K.ParenthesisClose, "Expected closing \")\" after signal parameters.");
            signal.ParametersEnd = Relative(previous.End);
        }
        Complete(signal);
        EndStatement("signal declaration");
        return signal;
    }

    private GDScriptEnum ParseEnum()
    {
        GDScriptEnum node = Alloc<GDScriptEnum>();
        bool named = false;
        if (Match(K.Identifier))
        {
            node.Identifier = ParseIdentifier();
            named = true;
        }
        PushMultiline(true);
        Consume(K.BraceOpen, $"Expected \"{{\" after {(named ? "enum name" : "\"enum\"")}.");
        Dictionary<string, int> elements = new(StringComparer.Ordinal);
        do
        {
            if (Check(K.BraceClose))
            {
                break;
            }
            if (Consume(K.Identifier, "Expected identifier for enum key."))
            {
                GDScriptIdentifier identifier = ParseIdentifier();
                if (elements.TryGetValue(identifier.Name, out int was))
                {
                    PushError($"Name \"{identifier.Name}\" was already in this enum (at line {LineOf(was)}).", identifier);
                }
                elements[identifier.Name] = previous.Start;
                GDScriptExpression? value = null;
                if (Match(K.Equal))
                {
                    value = ParseExpression(false);
                    if (value is null)
                    {
                        PushError("Expected expression value after \"=\".");
                    }
                }
                node.Values.Add((identifier, value));
            }
        }
        while (Match(K.Comma));
        PopMultiline();
        Consume(K.BraceClose, "Expected closing \"}\" for enum.");
        Complete(node);
        EndStatement("enum");
        return node;
    }

    private int LineOf(int offset) => text.AsSpan(0, offset).Count('\n') + 1;

    private bool ParseFunctionSignature(GDScriptFunction function, GDScriptSuite body, string kind)
    {
        if (!Check(K.ParenthesisClose) && !AtEnd)
        {
            bool defaulted = false;
            HashSet<string> names = new(StringComparer.Ordinal);
            do
            {
                if (Check(K.ParenthesisClose))
                {
                    break;
                }
                bool rest = Match(K.PeriodPeriodPeriod);
                GDScriptParameter? parameter = ParseParameter();
                if (parameter is null)
                {
                    break;
                }
                if (function.Rest is not null)
                {
                    PushError("Cannot have parameters after the rest parameter.");
                    continue;
                }
                if (parameter.Initializer is not null)
                {
                    if (rest)
                    {
                        PushError("The rest parameter cannot have a default value.");
                        continue;
                    }
                    defaulted = true;
                }
                else if (defaulted && !rest)
                {
                    PushError("Cannot have mandatory parameters after optional parameters.");
                    continue;
                }
                if (!names.Add(parameter.Identifier!.Name))
                {
                    PushError($"Parameter with name \"{parameter.Identifier.Name}\" was already declared for this {kind}.");
                }
                else if (rest)
                {
                    function.Rest = parameter;
                    body.AddLocal(parameter.Identifier.Name, "parameter", parameter);
                }
                else
                {
                    function.Parameters.Add(parameter);
                    body.AddLocal(parameter.Identifier.Name, "parameter", parameter);
                }
            }
            while (Match(K.Comma));
        }
        PopMultiline();
        Consume(K.ParenthesisClose, $"Expected closing \")\" after {kind} parameters.");
        function.ParametersEnd = Relative(previous.End);
        if (Match(K.ForwardArrow))
        {
            function.ReturnType = ParseType(true);
            if (function.ReturnType is null)
            {
                PushError("Expected return type or \"void\" after \"->\".");
            }
        }
        if (!function.IsLambda && function.Identifier?.Name == "_static_init")
        {
            if (!function.IsStatic)
            {
                PushError("Static constructor must be declared static.");
            }
            if (function.Parameters.Count > 0 || function.Rest is not null)
            {
                PushError("Static constructor cannot have parameters.");
            }
        }
        function.HeaderEnd = Relative(previous.End);
        if (kind == "lambda")
        {
            return Consume(K.Colon, "Expected \":\" after lambda declaration.");
        }
        return Match(K.Colon);
    }

    private GDScriptFunction? ParseFunction(bool isStatic)
    {
        GDScriptFunction function = Alloc<GDScriptFunction>();
        function.IsStatic = isStatic;
        if (!Consume(K.Identifier, "Expected function name after \"func\"."))
        {
            Complete(function);
            return null;
        }
        GDScriptFunction? outer = currentFunction;
        currentFunction = function;
        function.Identifier = ParseIdentifier();
        GDScriptSuite body = Alloc<GDScriptSuite>();
        GDScriptSuite? outerSuite = currentSuite;
        currentSuite = body;
        PushMultiline(true);
        if (Consume(K.ParenthesisOpen, "Expected opening \"(\" after function name."))
        {
            function.ParametersStart = Relative(previous.Start);
        }
        bool hasBody = ParseFunctionSignature(function, body, "function");
        currentSuite = outerSuite;
        if (!hasBody)
        {
            EndStatement("bodyless function declaration");
            ResetExtents(body, current);
            Complete(body);
            function.Body = body;
        }
        else
        {
            function.Body = ParseSuite("function declaration", body);
        }
        currentFunction = outer;
        Complete(function);
        return function;
    }

    private GDScriptAnnotation? ParseAnnotation(Targets valid)
    {
        GDScriptAnnotation annotation = Alloc<GDScriptAnnotation>();
        annotation.Name = previous.Name ?? text[previous.Start..previous.End];
        bool ok = true;
        if (!KnownAnnotations.TryGetValue(annotation.Name, out (Targets Targets, int Arguments, int Defaults, bool Varargs) known))
        {
            PushError(annotation.Name switch
            {
                "@deprecated" => "\"@deprecated\" annotation does not exist. Use \"## @deprecated: Reason here.\" instead.",
                "@experimental" => "\"@experimental\" annotation does not exist. Use \"## @experimental: Reason here.\" instead.",
                "@tutorial" => "\"@tutorial\" annotation does not exist. Use \"## @tutorial(Title): https://example.com\" instead.",
                _ => $"Unrecognized annotation: \"{annotation.Name}\".",
            });
            ok = false;
        }
        else if ((known.Targets & valid) == 0)
        {
            PushError((known.Targets & Targets.Script) != 0 ? $"Annotation \"{annotation.Name}\" must be at the top of the script, before \"extends\" and \"class_name\"."
                : $"Annotation \"{annotation.Name}\" is not allowed in this level.");
            ok = false;
        }
        if (Check(K.ParenthesisOpen))
        {
            PushMultiline(true);
            Advance();
            do
            {
                if (Check(K.ParenthesisClose))
                {
                    break;
                }
                GDScriptExpression? argument = ParseExpression(false);
                if (argument is null)
                {
                    PushError("Expected expression as the annotation argument.");
                    ok = false;
                }
                else
                {
                    annotation.Arguments.Add(argument);
                }
            }
            while (Match(K.Comma));
            PopMultiline();
            Consume(K.ParenthesisClose, "Expected \")\" after annotation arguments.");
        }
        Complete(annotation);
        Match(K.Newline);
        if (ok)
        {
            ok = ValidateArguments(annotation, known);
        }
        return ok ? annotation : null;
    }

    private bool ValidateArguments(GDScriptAnnotation annotation, (Targets Targets, int Arguments, int Defaults, bool Varargs) known)
    {
        if (!known.Varargs && annotation.Arguments.Count > known.Arguments)
        {
            PushError($"Annotation \"{annotation.Name}\" requires at most {known.Arguments} arguments, but {annotation.Arguments.Count} were given.");
            return false;
        }
        if (annotation.Arguments.Count < known.Arguments - known.Defaults)
        {
            PushError($"Annotation \"{annotation.Name}\" requires at least {known.Arguments - known.Defaults} arguments, but {annotation.Arguments.Count} were given.");
            return false;
        }
        if (annotation.Name is "@icon" or "@warning_ignore_start" or "@warning_ignore_restore")
        {
            for (int i = 0; i < annotation.Arguments.Count; i++)
            {
                if (annotation.Arguments[i] is not GDScriptLiteral { Kind: GDScriptLiteralKind.String })
                {
                    PushError($"Argument {i + 1} of annotation \"{annotation.Name}\" must be a string literal.", annotation.Arguments[i]);
                    return false;
                }
            }
        }
        return true;
    }

    private void ClearUnusedAnnotations()
    {
        foreach (GDScriptAnnotation annotation in annotationStack)
        {
            PushError($"Annotation \"{annotation.Name}\" does not precede a valid target, so it will have no effect.", annotation);
        }
        annotationStack.Clear();
    }

    private bool ParseStandaloneString()
    {
        if (Check(K.Literal) && current.Literal == GDScriptLiteralKind.String)
        {
            Advance();
            if (!Match(K.Newline))
            {
                PushError("Expected newline after comment string.");
            }
            return true;
        }
        return false;
    }
}
