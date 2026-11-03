using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>Expressions, by Godot's table of each token's prefix and infix rule and precedence.</summary>
internal sealed partial class GDScriptParser
{
    private enum Rule : byte
    {
        None,
        Identifier,
        Literal,
        Binary,
        Unary,
        NotIn,
        GetNode,
        Assignment,
        Ternary,
        Cast,
        Await,
        Lambda,
        TypeTest,
        Preload,
        Self,
        Call,
        Yield,
        Array,
        Subscript,
        Dictionary,
        Grouping,
        Attribute,
        BuiltinConstant,
        InvalidToken,
    }

    private static (Rule Prefix, Rule Infix, Precedence Precedence) RuleOf(K kind) => kind switch
    {
        K.Identifier => (Rule.Identifier, Rule.None, Precedence.None),
        K.Literal => (Rule.Literal, Rule.None, Precedence.None),
        K.Less or K.LessEqual or K.Greater or K.GreaterEqual or K.EqualEqual or K.BangEqual => (Rule.None, Rule.Binary, Precedence.Comparison),
        K.And or K.AmpersandAmpersand => (Rule.None, Rule.Binary, Precedence.LogicAnd),
        K.Or or K.PipePipe => (Rule.None, Rule.Binary, Precedence.LogicOr),
        K.Not => (Rule.Unary, Rule.NotIn, Precedence.ContentTest),
        K.Bang => (Rule.Unary, Rule.None, Precedence.None),
        K.Ampersand => (Rule.None, Rule.Binary, Precedence.BitAnd),
        K.Pipe => (Rule.None, Rule.Binary, Precedence.BitOr),
        K.Tilde => (Rule.Unary, Rule.None, Precedence.None),
        K.Caret => (Rule.None, Rule.Binary, Precedence.BitXor),
        K.LessLess or K.GreaterGreater => (Rule.None, Rule.Binary, Precedence.BitShift),
        K.Plus or K.Minus => (Rule.Unary, Rule.Binary, Precedence.AdditionSubtraction),
        K.Star or K.Slash => (Rule.None, Rule.Binary, Precedence.Factor),
        K.StarStar => (Rule.None, Rule.Binary, Precedence.Power),
        K.Percent => (Rule.GetNode, Rule.Binary, Precedence.Factor),
        K.Equal or K.PlusEqual or K.MinusEqual or K.StarEqual or K.StarStarEqual or K.SlashEqual or K.PercentEqual or K.LessLessEqual
            or K.GreaterGreaterEqual or K.AmpersandEqual or K.PipeEqual or K.CaretEqual => (Rule.None, Rule.Assignment, Precedence.Assignment),
        K.If => (Rule.None, Rule.Ternary, Precedence.Ternary),
        K.As => (Rule.None, Rule.Cast, Precedence.Cast),
        K.Await => (Rule.Await, Rule.None, Precedence.None),
        K.Func => (Rule.Lambda, Rule.None, Precedence.None),
        K.In => (Rule.None, Rule.Binary, Precedence.ContentTest),
        K.Is => (Rule.None, Rule.TypeTest, Precedence.TypeTest),
        K.Preload => (Rule.Preload, Rule.None, Precedence.None),
        K.Self => (Rule.Self, Rule.None, Precedence.None),
        K.Super => (Rule.Call, Rule.None, Precedence.None),
        K.Yield => (Rule.Yield, Rule.None, Precedence.None),
        K.BracketOpen => (Rule.Array, Rule.Subscript, Precedence.Subscript),
        K.BraceOpen => (Rule.Dictionary, Rule.None, Precedence.None),
        K.ParenthesisOpen => (Rule.Grouping, Rule.Call, Precedence.Call),
        K.Period => (Rule.None, Rule.Attribute, Precedence.Attribute),
        K.Dollar => (Rule.GetNode, Rule.None, Precedence.None),
        K.ConstPi or K.ConstTau or K.ConstInf or K.ConstNan => (Rule.BuiltinConstant, Rule.None, Precedence.None),
        K.QuestionMark => (Rule.None, Rule.InvalidToken, Precedence.Cast),
        _ => (Rule.None, Rule.None, Precedence.None),
    };

    private GDScriptExpression? Run(Rule rule, GDScriptExpression? left, bool canAssign) => rule switch
    {
        Rule.Identifier => ParseIdentifier(),
        Rule.Literal => ParseLiteral(left),
        Rule.Binary => ParseBinary(left),
        Rule.Unary => ParseUnary(),
        Rule.NotIn => ParseNotIn(left),
        Rule.GetNode => ParseGetNode(),
        Rule.Assignment => ParseAssignment(left, canAssign),
        Rule.Ternary => ParseTernary(left),
        Rule.Cast => ParseCast(left),
        Rule.Await => ParseAwait(),
        Rule.Lambda => ParseLambda(),
        Rule.TypeTest => ParseTypeTest(left),
        Rule.Preload => ParsePreload(),
        Rule.Self => ParseSelf(),
        Rule.Call => ParseCall(left),
        Rule.Yield => ParseYield(),
        Rule.Array => ParseArray(),
        Rule.Subscript => ParseSubscript(left),
        Rule.Dictionary => ParseDictionary(),
        Rule.Grouping => ParseGrouping(),
        Rule.Attribute => ParseAttribute(left),
        Rule.BuiltinConstant => ParseBuiltinConstant(),
        Rule.InvalidToken => ParseInvalidToken(left),
        _ => null,
    };

    private GDScriptExpression? ParseExpression(bool canAssign, bool stopOnAssign = false) => ParsePrecedence(Precedence.Assignment, canAssign, stopOnAssign);

    private GDScriptExpression? ParsePrecedence(Precedence precedence, bool canAssign, bool stopOnAssign = false)
    {
        if (current.Kind is K.ParenthesisOpen or K.BraceOpen or K.BracketOpen)
        {
            PushMultiline(true);
        }
        Rule prefix = RuleOf(current.IsIdentifier ? K.Identifier : current.Kind).Prefix;
        if (prefix == Rule.None)
        {
            return null;
        }
        Advance();
        GDScriptExpression? left = Run(prefix, null, canAssign);
        while (precedence <= RuleOf(current.Kind).Precedence)
        {
            if (left is null || stopOnAssign && current.Kind == K.Equal || lambdaEnded)
            {
                return left;
            }
            if (current.Kind is K.ParenthesisOpen or K.BracketOpen)
            {
                PushMultiline(true);
            }
            GDScriptToken token = Advance();
            left = Run(RuleOf(token.Kind).Infix, left, canAssign);
        }
        return left;
    }

    private GDScriptLiteral ParseLiteral(GDScriptExpression? left)
    {
        GDScriptLiteral literal = Alloc<GDScriptLiteral>();
        literal.Value = previous.Value;
        literal.Kind = previous.Literal;
        ResetExtents(literal, left);
        UpdateExtents(literal);
        Complete(literal);
        return literal;
    }

    private GDScriptSelf ParseSelf()
    {
        if (currentFunction is { IsStatic: true })
        {
            PushError("Cannot use \"self\" inside a static function.");
        }
        GDScriptSelf self = Alloc<GDScriptSelf>();
        Complete(self);
        return self;
    }

    private GDScriptLiteral ParseBuiltinConstant()
    {
        GDScriptLiteral constant = Alloc<GDScriptLiteral>();
        Complete(constant);
        constant.Kind = GDScriptLiteralKind.Float;
        constant.Value = previous.Kind switch
        {
            K.ConstPi => Math.PI,
            K.ConstTau => Math.Tau,
            K.ConstInf => double.PositiveInfinity,
            _ => double.NaN,
        };
        return constant;
    }

    private GDScriptUnary ParseUnary()
    {
        K op = previous.Kind;
        GDScriptUnary operation = Alloc<GDScriptUnary>();
        operation.Operator = op;
        operation.Operand = ParsePrecedence(op switch
        {
            K.Minus or K.Plus => Precedence.Sign,
            K.Tilde => Precedence.BitNot,
            _ => Precedence.LogicNot,
        }, false);
        if (operation.Operand is null)
        {
            PushError($"Expected expression after \"{(op == K.Not ? "not" : GDScriptToken.KindName(op))}\" operator.");
        }
        Complete(operation);
        return operation;
    }

    private GDScriptUnary ParseNotIn(GDScriptExpression? left)
    {
        GDScriptUnary operation = Alloc<GDScriptUnary>();
        ResetExtents(operation, left);
        UpdateExtents(operation);
        Consume(K.In, "Expected \"in\" after \"not\" in content-test operator.");
        operation.Operator = K.Not;
        operation.Operand = ParseBinary(left);
        Complete(operation);
        return operation;
    }

    private GDScriptBinary ParseBinary(GDScriptExpression? left)
    {
        GDScriptToken op = previous;
        GDScriptBinary operation = Alloc<GDScriptBinary>();
        ResetExtents(operation, left);
        UpdateExtents(operation);
        operation.Operator = op.Kind;
        operation.Left = left;
        operation.Right = ParsePrecedence(RuleOf(op.Kind).Precedence + 1, false);
        Complete(operation);
        if (operation.Right is null)
        {
            PushError($"Expected expression after \"{GDScriptToken.KindName(op.Kind)}\" operator.");
        }
        return operation;
    }

    private GDScriptTernary ParseTernary(GDScriptExpression? left)
    {
        GDScriptTernary operation = Alloc<GDScriptTernary>();
        ResetExtents(operation, left);
        UpdateExtents(operation);
        operation.True = left;
        operation.Condition = ParsePrecedence(Precedence.Ternary, false);
        if (operation.Condition is null)
        {
            PushError("Expected expression as ternary condition after \"if\".");
        }
        Consume(K.Else, "Expected \"else\" after ternary operator condition.");
        operation.False = ParsePrecedence(Precedence.Ternary, false);
        if (operation.False is null)
        {
            PushError("Expected expression after \"else\".");
        }
        Complete(operation);
        return operation;
    }

    private GDScriptExpression? ParseAssignment(GDScriptExpression? left, bool canAssign)
    {
        if (!canAssign)
        {
            PushError("Assignment is not allowed inside an expression.");
            return ParseExpression(false);
        }
        if (left is null)
        {
            return ParseExpression(false);
        }
        if (left is not (GDScriptSubscript or GDScriptIdentifier))
        {
            PushError("Only identifier, attribute access, and subscription access can be used as assignment target.");
            return ParseExpression(false);
        }
        GDScriptAssignment assignment = Alloc<GDScriptAssignment>();
        ResetExtents(assignment, left);
        UpdateExtents(assignment);
        assignment.Operator = previous.Kind;
        assignment.Assignee = left;
        assignment.Value = ParseExpression(false);
        if (assignment.Value is null)
        {
            PushError("Expected an expression after \"=\".");
        }
        Complete(assignment);
        return assignment;
    }

    private GDScriptAwait ParseAwait()
    {
        GDScriptAwait awaited = Alloc<GDScriptAwait>();
        awaited.Awaited = ParsePrecedence(Precedence.Await, false);
        if (awaited.Awaited is null)
        {
            PushError("Expected signal or coroutine after \"await\".");
        }
        Complete(awaited);
        return awaited;
    }

    private GDScriptArray ParseArray()
    {
        GDScriptArray array = Alloc<GDScriptArray>();
        if (!Check(K.BracketClose))
        {
            do
            {
                if (Check(K.BracketClose))
                {
                    break;
                }
                GDScriptExpression? element = ParseExpression(false);
                if (element is null)
                {
                    PushError("Expected expression as array element.");
                }
                else
                {
                    array.Elements.Add(element);
                }
            }
            while (Match(K.Comma) && !AtEnd);
        }
        PopMultiline();
        Consume(K.BracketClose, "Expected closing \"]\" after array elements.");
        Complete(array);
        return array;
    }

    private GDScriptDictionary ParseDictionary()
    {
        GDScriptDictionary dictionary = Alloc<GDScriptDictionary>();
        bool decided = false;
        if (!Check(K.BraceClose))
        {
            do
            {
                if (Check(K.BraceClose))
                {
                    break;
                }
                GDScriptExpression? key = ParseExpression(false, true);
                if (key is null)
                {
                    PushError("Expected expression as dictionary key.");
                }
                if (!decided)
                {
                    switch (current.Kind)
                    {
                        case K.Colon:
                            break;
                        case K.Equal:
                            dictionary.LuaTable = true;
                            break;
                        default:
                            PushError("Expected \":\" or \"=\" after dictionary key.");
                            break;
                    }
                    decided = true;
                }
                if (dictionary.LuaTable)
                {
                    if (key is not null and not (GDScriptIdentifier or GDScriptLiteral) || key is GDScriptLiteral { Kind: not GDScriptLiteralKind.String })
                    {
                        PushError("Expected identifier or string as Lua-style dictionary key (e.g \"{ key = value }\").");
                    }
                    if (!Match(K.Equal))
                    {
                        if (Match(K.Colon))
                        {
                            PushError("Expected \"=\" after dictionary key. Mixing dictionary styles is not allowed.");
                            Advance();
                        }
                        else
                        {
                            PushError("Expected \"=\" after dictionary key.");
                        }
                    }
                }
                else if (!Match(K.Colon))
                {
                    if (Match(K.Equal))
                    {
                        PushError("Expected \":\" after dictionary key. Mixing dictionary styles is not allowed.");
                        Advance();
                    }
                    else
                    {
                        PushError("Expected \":\" after dictionary key.");
                    }
                }
                GDScriptExpression? value = ParseExpression(false);
                if (value is null)
                {
                    PushError("Expected expression as dictionary value.");
                }
                if (key is not null || value is not null)
                {
                    dictionary.Elements.Add((key ?? new GDScriptLiteral { Kind = GDScriptLiteralKind.Null }, value ?? new GDScriptLiteral { Kind = GDScriptLiteralKind.Null }));
                }
            }
            while (Match(K.Comma) && !AtEnd);
        }
        PopMultiline();
        Consume(K.BraceClose, "Expected closing \"}\" after dictionary elements.");
        Complete(dictionary);
        return dictionary;
    }

    private GDScriptExpression? ParseGrouping()
    {
        GDScriptExpression? grouped = ParseExpression(false);
        PopMultiline();
        if (grouped is null)
        {
            PushError("Expected grouping expression.");
        }
        else
        {
            Consume(K.ParenthesisClose, "Expected closing \")\" after grouping expression.");
        }
        return grouped;
    }

    private GDScriptSubscript ParseAttribute(GDScriptExpression? left)
    {
        GDScriptSubscript attribute = Alloc<GDScriptSubscript>();
        ResetExtents(attribute, left);
        UpdateExtents(attribute);
        attribute.Base = left;
        if (current.IsNodeName)
        {
            current = current with { Kind = K.Identifier };
        }
        if (!Consume(K.Identifier, "Expected identifier after \".\" for attribute access."))
        {
            Complete(attribute);
            return attribute;
        }
        attribute.IsAttribute = true;
        attribute.Attribute = ParseIdentifier();
        Complete(attribute);
        return attribute;
    }

    private GDScriptSubscript ParseSubscript(GDScriptExpression? left)
    {
        GDScriptSubscript subscript = Alloc<GDScriptSubscript>();
        ResetExtents(subscript, left);
        UpdateExtents(subscript);
        subscript.Base = left;
        subscript.Index = ParseExpression(false);
        if (subscript.Index is null)
        {
            PushError("Expected expression after \"[\".");
        }
        PopMultiline();
        Consume(K.BracketClose, "Expected \"]\" after subscription index.");
        Complete(subscript);
        return subscript;
    }

    private GDScriptExpression? ParseCast(GDScriptExpression? left)
    {
        GDScriptCast cast = Alloc<GDScriptCast>();
        ResetExtents(cast, left);
        UpdateExtents(cast);
        cast.Operand = left;
        cast.Type = ParseType();
        Complete(cast);
        if (cast.Type is null)
        {
            PushError("Expected type specifier after \"as\".");
            return left;
        }
        return cast;
    }

    private GDScriptCall? ParseCall(GDScriptExpression? left)
    {
        GDScriptCall call = Alloc<GDScriptCall>();
        ResetExtents(call, left);
        if (previous.Kind == K.Super)
        {
            call.IsSuper = true;
            if (Check(K.ParenthesisOpen))
            {
                PushMultiline(true);
                Advance();
                if (currentFunction is null)
                {
                    PushError("Cannot use implicit \"super\" call outside of a function.");
                    PopMultiline();
                    Complete(call);
                    return null;
                }
                call.FunctionName = currentFunction.Identifier?.Name ?? "<anonymous>";
            }
            else
            {
                Consume(K.Period, "Expected \".\" or \"(\" after \"super\".");
                if (!Consume(K.Identifier, "Expected function name after \".\"."))
                {
                    Complete(call);
                    return null;
                }
                GDScriptIdentifier identifier = ParseIdentifier();
                call.Callee = identifier;
                call.FunctionName = identifier.Name;
                if (Check(K.ParenthesisOpen))
                {
                    PushMultiline(true);
                    Advance();
                }
                else
                {
                    PushError("Expected \"(\" after function name.");
                    Complete(call);
                    return null;
                }
            }
        }
        else
        {
            call.Callee = left;
            if (left is GDScriptIdentifier identifier)
            {
                call.FunctionName = identifier.Name;
            }
            else if (left is GDScriptSubscript { IsAttribute: true } attribute)
            {
                call.FunctionName = attribute.Attribute?.Name;
            }
            else
            {
                PushError("Cannot call on an expression. Use \".call()\" if it's a Callable.");
            }
        }
        do
        {
            if (Check(K.ParenthesisClose))
            {
                break;
            }
            GDScriptExpression? argument = ParseExpression(false);
            if (argument is null)
            {
                PushError("Expected expression as the function argument.");
            }
            else
            {
                call.Arguments.Add(argument);
            }
        }
        while (Match(K.Comma));
        PopMultiline();
        Consume(K.ParenthesisClose, "Expected closing \")\" after call arguments.");
        Complete(call);
        return call;
    }

    private GDScriptGetNode? ParseGetNode()
    {
        if (!current.IsNodeName && !Check(K.Literal) && !Check(K.Slash) && !Check(K.Percent)
            || Check(K.Literal) && current.Literal != GDScriptLiteralKind.String)
        {
            PushError($"Expected node path as string or identifier after \"{GDScriptToken.KindName(previous.Kind)}\".");
            return null;
        }
        GDScriptGetNode node = Alloc<GDScriptGetNode>();
        System.Text.StringBuilder path = new();
        int state = 0;
        if (previous.Kind == K.Dollar)
        {
            Match(K.Slash);
        }
        else
        {
            node.UsesDollar = false;
        }
        do
        {
            if (previous.Kind == K.Percent)
            {
                if (state is not (0 or 1))
                {
                    PushError("\"%\" is only valid in the beginning of a node name (either after \"$\" or after \"/\")");
                    Complete(node);
                    return null;
                }
                path.Append('%');
                state = 2;
            }
            else if (previous.Kind == K.Slash)
            {
                if (state is not (0 or 3))
                {
                    PushError("\"/\" is only valid at the beginning of the path or after a node name.");
                    Complete(node);
                    return null;
                }
                path.Append('/');
                state = 1;
            }
            if (Match(K.Literal))
            {
                if (previous.Literal != GDScriptLiteralKind.String)
                {
                    PushError($"Expected node path as string or identifier after \"{state switch { 0 => "$", 2 => "%", 1 => "/", _ => string.Empty }}\".");
                    Complete(node);
                    return null;
                }
                path.Append((string)previous.Value!);
                state = 3;
            }
            else if (current.IsNodeName)
            {
                Advance();
                path.Append(IdentifierName(previous));
                state = 3;
            }
            else if (!Check(K.Slash) && !Check(K.Percent))
            {
                PushError($"Unexpected \"{GDScriptToken.KindName(current.Kind)}\" in node path.");
                Complete(node);
                return null;
            }
        }
        while (Match(K.Slash) || Match(K.Percent));
        node.Path = path.ToString();
        Complete(node);
        return node;
    }

    private GDScriptPreload ParsePreload()
    {
        GDScriptPreload preload = Alloc<GDScriptPreload>();
        PushMultiline(true);
        Consume(K.ParenthesisOpen, "Expected \"(\" after \"preload\".");
        preload.Path = ParseExpression(false);
        if (preload.Path is null)
        {
            PushError("Expected resource path after \"(\".");
        }
        Match(K.Comma);
        PopMultiline();
        Consume(K.ParenthesisClose, "Expected \")\" after preload path.");
        Complete(preload);
        return preload;
    }

    private GDScriptLambda ParseLambda()
    {
        GDScriptLambda lambda = Alloc<GDScriptLambda>();
        GDScriptFunction function = Alloc<GDScriptFunction>();
        function.IsLambda = true;
        function.IsStatic = currentFunction?.IsStatic ?? false;
        if (Match(K.Identifier))
        {
            function.Identifier = ParseIdentifier();
        }
        bool multilineContext = multilineStack.Count > 0 && multilineStack[^1];
        PushMultiline(false);
        if (multilineContext)
        {
            tokenizer.PushExpressionIndentedBlock();
        }
        PushMultiline(true);
        if (Consume(K.ParenthesisOpen, function.Identifier is not null ? "Expected opening \"(\" after lambda name." : "Expected opening \"(\" after \"func\"."))
        {
            function.ParametersStart = Relative(previous.Start);
        }
        GDScriptFunction? outer = currentFunction;
        currentFunction = function;
        GDScriptSuite body = Alloc<GDScriptSuite>();
        body.Parent = currentSuite;
        GDScriptSuite? outerSuite = currentSuite;
        currentSuite = body;
        ParseFunctionSignature(function, body, "lambda");
        currentSuite = outerSuite;
        bool wasInLambda = inLambda;
        inLambda = true;
        (bool couldBreak, bool couldContinue) = (canBreak, canContinue);
        (canBreak, canContinue) = (false, false);
        function.Body = ParseSuite("lambda declaration", body, true);
        Complete(function);
        Complete(lambda);
        PopMultiline();
        if (multilineContext)
        {
            while (Check(K.Dedent) || Check(K.Indent) || Check(K.Newline))
            {
                current = tokenizer.Scan();
            }
            tokenizer.PopExpressionIndentedBlock();
        }
        currentFunction = outer;
        inLambda = wasInLambda;
        lambda.Function = function;
        (canBreak, canContinue) = (couldBreak, couldContinue);
        return lambda;
    }

    private GDScriptExpression ParseTypeTest(GDScriptExpression? left)
    {
        GDScriptUnary? not = null;
        if (Match(K.Not))
        {
            not = Alloc<GDScriptUnary>();
            not.Operator = K.Not;
            ResetExtents(not, left);
            UpdateExtents(not);
        }
        GDScriptTypeTest test = Alloc<GDScriptTypeTest>();
        ResetExtents(test, left);
        UpdateExtents(test);
        test.Operand = left;
        test.Type = ParseType();
        Complete(test);
        if (not is not null)
        {
            not.Operand = test;
            Complete(not);
        }
        if (test.Type is null)
        {
            PushError(not is null ? "Expected type specifier after \"is\"." : "Expected type specifier after \"is not\".");
        }
        return (GDScriptExpression?)not ?? test;
    }

    private GDScriptExpression? ParseYield()
    {
        PushError("\"yield\" was removed in Godot 4. Use \"await\" instead.");
        return null;
    }

    private GDScriptExpression? ParseInvalidToken(GDScriptExpression? left)
    {
        if (previous.Kind == K.QuestionMark)
        {
            PushError("Unexpected \"?\" in source. If you want a ternary operator, use \"truthy_value if true_condition else falsy_value\".");
        }
        return left;
    }

    private GDScriptTypeNode? ParseType(bool allowVoid = false)
    {
        GDScriptTypeNode type = Alloc<GDScriptTypeNode>();
        ResetExtents(type, current);
        if (!Match(K.Identifier))
        {
            if (Match(K.Void))
            {
                if (allowVoid)
                {
                    Complete(type);
                    type.IsVoid = true;
                    return type;
                }
                PushError("\"void\" is only allowed for a function return type.");
            }
            Complete(type);
            return null;
        }
        type.Chain.Add(ParseIdentifier());
        if (Match(K.BracketOpen))
        {
            bool first = true;
            GDScriptTypeNode? made = type;
            do
            {
                GDScriptTypeNode? element = ParseType();
                if (element is null)
                {
                    PushError($"Expected type for collection after \"{(first ? "[" : ",")}\".");
                    Complete(type);
                    made = null;
                    break;
                }
                if (element.Elements.Count > 0)
                {
                    PushError("Nested typed collections are not supported.");
                }
                else
                {
                    type.Elements.Add(element);
                }
                first = false;
            }
            while (Match(K.Comma));
            Consume(K.BracketClose, "Expected closing \"]\" after collection type.");
            if (made is not null)
            {
                Complete(type);
            }
            return made;
        }
        while (Match(K.Period))
        {
            if (Consume(K.Identifier, "Expected inner type name after \".\"."))
            {
                type.Chain.Add(ParseIdentifier());
            }
        }
        Complete(type);
        return type;
    }
}
