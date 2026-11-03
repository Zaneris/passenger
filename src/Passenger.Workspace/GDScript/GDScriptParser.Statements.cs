using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>Blocks and the statements in them.</summary>
internal sealed partial class GDScriptParser
{
    private GDScriptSuite ParseSuite(string context, GDScriptSuite? given = null, bool forLambda = false)
    {
        GDScriptSuite suite = given ?? Alloc<GDScriptSuite>();
        suite.Parent = currentSuite;
        currentSuite = suite;
        if (!forLambda && suite.Parent is { InLoop: true })
        {
            suite.InLoop = true;
        }
        bool multiline = Match(K.Newline);
        if (multiline && !Consume(K.Indent, $"Expected indented block after {context}."))
        {
            currentSuite = suite.Parent;
            Complete(suite);
            return suite;
        }
        ResetExtents(suite, current);
        int failures = 0;
        do
        {
            if (AtEnd || !multiline && previous.Kind == K.Semicolon && Check(K.Newline))
            {
                break;
            }
            GDScriptNode? statement = ParseStatement();
            if (statement is null)
            {
                if (failures++ > 100)
                {
                    PushError("Too many statement errors.", suite);
                    break;
                }
                continue;
            }
            suite.Statements.Add(statement);
            if (statement is GDScriptVariable { Identifier: { } variable })
            {
                if (currentSuite.LocalKind(variable.Name) is { } kind)
                {
                    PushError($"There is already a {kind} named \"{variable.Name}\" declared in this scope.", variable);
                }
                currentSuite.AddLocal(variable.Name, "variable", statement);
            }
            else if (statement is GDScriptConstant { Identifier: { } constant })
            {
                if (currentSuite.LocalKind(constant.Name) is { } kind)
                {
                    PushError($"There is already a {(kind == "constant" ? "constant" : "variable")} named \"{constant.Name}\" declared in this scope.", constant);
                }
                currentSuite.AddLocal(constant.Name, "constant", statement);
            }
        }
        while ((multiline || previous.Kind == K.Semicolon) && !Check(K.Dedent) && !lambdaEnded && !AtEnd);
        Complete(suite);
        if (multiline)
        {
            if (!lambdaEnded)
            {
                Consume(K.Dedent, $"Missing unindent at the end of {context}.");
            }
            else
            {
                Match(K.Dedent);
            }
        }
        else if (previous.Kind == K.Semicolon)
        {
            Consume(K.Newline, $"Expected newline after \";\" at the end of {context}.");
        }
        if (forLambda)
        {
            lambdaEnded = true;
        }
        currentSuite = suite.Parent;
        return suite;
    }

    private GDScriptNode? ParseStatement()
    {
        List<GDScriptAnnotation> annotations = [];
        if (current.Kind != K.Annotation)
        {
            while (annotationStack.Count > 0)
            {
                GDScriptAnnotation last = annotationStack[^1];
                if (AppliesTo(last, Targets.Statement))
                {
                    annotations.Insert(0, last);
                    annotationStack.RemoveAt(annotationStack.Count - 1);
                }
                else
                {
                    PushError($"Annotation \"{last.Name}\" cannot be applied to a statement.");
                    ClearUnusedAnnotations();
                }
            }
        }
        GDScriptNode? result = null;
        switch (current.Kind)
        {
            case K.Pass:
                Advance();
                result = Alloc<GDScriptPass>();
                Complete(result);
                EndStatement("\"pass\"");
                break;
            case K.Var:
                Advance();
                result = ParseVariable(false, false);
                break;
            case K.Const:
                Advance();
                result = ParseConstant(false);
                break;
            case K.If:
                Advance();
                result = ParseIf();
                break;
            case K.For:
                Advance();
                result = ParseFor();
                break;
            case K.While:
                Advance();
                result = ParseWhile();
                break;
            case K.Match:
                Advance();
                result = ParseMatch();
                break;
            case K.Break:
                Advance();
                if (!canBreak)
                {
                    PushError("Cannot use \"break\" outside of a loop.");
                }
                result = Alloc<GDScriptBreak>();
                Complete(result);
                EndStatement("\"break\"");
                break;
            case K.Continue:
                Advance();
                if (!canContinue)
                {
                    PushError("Cannot use \"continue\" outside of a loop.");
                }
                currentSuite!.HasContinue = true;
                result = Alloc<GDScriptContinue>();
                Complete(result);
                EndStatement("\"continue\"");
                break;
            case K.Return:
                Advance();
                GDScriptReturn returned = Alloc<GDScriptReturn>();
                if (!IsStatementEnd)
                {
                    if (currentFunction?.Identifier?.Name is "_init" or "_static_init" && !currentFunction.IsLambda)
                    {
                        PushError("Constructor cannot return a value.");
                    }
                    returned.Value = ParseExpression(false);
                }
                else if (inLambda && !IsStatementEndToken)
                {
                    returned.Value = ParseExpression(false);
                }
                Complete(returned);
                result = returned;
                currentSuite!.HasReturn = true;
                EndStatement("return statement");
                break;
            case K.Breakpoint:
                Advance();
                result = Alloc<GDScriptBreakpoint>();
                Complete(result);
                EndStatement("\"breakpoint\"");
                break;
            case K.Assert:
                Advance();
                result = ParseAssert();
                break;
            case K.Annotation:
                Advance();
                if (ParseAnnotation(Targets.Statement | Targets.Standalone) is { } annotation)
                {
                    if (AppliesTo(annotation, Targets.Standalone))
                    {
                        if (previous.Kind != K.Newline)
                        {
                            PushError("Expected newline after a standalone annotation.");
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
                    }
                }
                break;
            default:
                GDScriptExpression? expression = ParseExpression(true);
                bool endedLambda = false;
                if (expression is null)
                {
                    if (inLambda)
                    {
                        lambdaEnded = true;
                        endedLambda = true;
                    }
                    else
                    {
                        Advance();
                        PushError($"Expected statement, found \"{GDScriptToken.KindName(previous.Kind)}\" instead.");
                    }
                }
                else
                {
                    EndStatement("expression");
                }
                lambdaEnded = lambdaEnded || endedLambda;
                result = expression;
                if (expression is GDScriptLambda)
                {
                    PushError("Standalone lambdas cannot be accessed. Consider assigning it to a variable.", expression);
                }
                break;
        }
        result?.Annotate(annotations);
        if (panic)
        {
            Synchronize();
        }
        return result;
    }

    private GDScriptAssert? ParseAssert()
    {
        GDScriptAssert node = Alloc<GDScriptAssert>();
        PushMultiline(true);
        Consume(K.ParenthesisOpen, "Expected \"(\" after \"assert\".");
        node.Condition = ParseExpression(false);
        if (node.Condition is null)
        {
            PushError("Expected expression to assert.");
            PopMultiline();
            Complete(node);
            return null;
        }
        if (Match(K.Comma) && !Check(K.ParenthesisClose))
        {
            node.Message = ParseExpression(false);
            if (node.Message is null)
            {
                PushError("Expected error message for assert after \",\".");
                PopMultiline();
                Complete(node);
                return null;
            }
            Match(K.Comma);
        }
        PopMultiline();
        Consume(K.ParenthesisClose, "Expected \")\" after assert expression.");
        Complete(node);
        EndStatement("\"assert\"");
        return node;
    }

    private GDScriptFor ParseFor()
    {
        GDScriptFor node = Alloc<GDScriptFor>();
        if (Consume(K.Identifier, "Expected loop variable name after \"for\"."))
        {
            node.Variable = ParseIdentifier();
        }
        if (Match(K.Colon))
        {
            node.Type = ParseType();
            if (node.Type is null)
            {
                PushError("Expected type specifier after \":\".");
            }
        }
        Consume(K.In, node.Type is null ? "Expected \"in\" or \":\" after \"for\" variable name." : "Expected \"in\" after \"for\" variable type specifier.");
        node.List = ParseExpression(false);
        if (node.List is null)
        {
            PushError("Expected iterable after \"in\".");
        }
        if (!Match(K.Colon))
        {
            PushError($"Expected \":\" after \"for\" condition, found \"{GDScriptToken.KindName(current.Kind)}\" instead.", current);
        }
        (bool couldBreak, bool couldContinue) = (canBreak, canContinue);
        (canBreak, canContinue) = (true, true);
        GDScriptSuite suite = Alloc<GDScriptSuite>();
        if (node.Variable is not null)
        {
            if (currentSuite?.LocalKind(node.Variable.Name) is { } kind)
            {
                PushError($"There is already a {kind} named \"{node.Variable.Name}\" declared in this scope.", node.Variable);
            }
            suite.AddLocal(node.Variable.Name, "for loop iterator", node.Variable);
        }
        suite.InLoop = true;
        node.Loop = ParseSuite("\"for\" block", suite);
        Complete(node);
        (canBreak, canContinue) = (couldBreak, couldContinue);
        return node;
    }

    private GDScriptIf ParseIf(string keyword = "if")
    {
        GDScriptIf node = Alloc<GDScriptIf>();
        node.Condition = ParseExpression(false);
        if (node.Condition is null)
        {
            PushError($"Expected conditional expression after \"{keyword}\".");
        }
        if (!Match(K.Colon))
        {
            PushError($"Expected \":\" after \"{keyword}\" condition, found \"{GDScriptToken.KindName(current.Kind)}\" instead.", current);
        }
        node.True = ParseSuite($"\"{keyword}\" block");
        if (node.True.HasContinue)
        {
            currentSuite!.HasContinue = true;
        }
        if (Match(K.Elif))
        {
            GDScriptSuite elseBlock = Alloc<GDScriptSuite>();
            elseBlock.Parent = currentSuite;
            GDScriptSuite? outer = currentSuite;
            currentSuite = elseBlock;
            elseBlock.Statements.Add(ParseIf("elif"));
            Complete(elseBlock);
            node.False = elseBlock;
            currentSuite = outer;
        }
        else if (Match(K.Else))
        {
            Consume(K.Colon, "Expected \":\" after \"else\".");
            node.False = ParseSuite("\"else\" block");
        }
        Complete(node);
        if (node.False is { HasReturn: true } && node.True.HasReturn)
        {
            currentSuite!.HasReturn = true;
        }
        if (node.False is { HasContinue: true })
        {
            currentSuite!.HasContinue = true;
        }
        return node;
    }

    private GDScriptMatch ParseMatch()
    {
        GDScriptMatch node = Alloc<GDScriptMatch>();
        node.Test = ParseExpression(false);
        if (node.Test is null)
        {
            PushError("Expected expression to test after \"match\".");
        }
        Consume(K.Colon, "Expected \":\" after \"match\" expression.");
        Consume(K.Newline, "Expected a newline after \"match\" statement.");
        if (!Consume(K.Indent, "Expected an indented block after \"match\" statement."))
        {
            Complete(node);
            return node;
        }
        bool allReturn = true;
        bool wildcard = false;
        List<GDScriptAnnotation> branchAnnotations = [];
        while (!Check(K.Dedent) && !AtEnd)
        {
            if (Match(K.Pass))
            {
                Consume(K.Newline, "Expected newline after \"pass\".");
                continue;
            }
            if (Match(K.Annotation))
            {
                GDScriptAnnotation? annotation = ParseAnnotation(Targets.Statement);
                if (annotation is null)
                {
                    continue;
                }
                if (annotation.Name != "@warning_ignore")
                {
                    PushError($"Annotation \"{annotation.Name}\" is not allowed in this level.", annotation);
                    continue;
                }
                branchAnnotations.Add(annotation);
                continue;
            }
            GDScriptMatchBranch? branch = ParseMatchBranch(out bool hasWildcard);
            if (branch is null)
            {
                Advance();
                continue;
            }
            branch.Annotate(branchAnnotations);
            branchAnnotations.Clear();
            wildcard = wildcard || hasWildcard;
            allReturn = allReturn && branch.Block is { HasReturn: true };
            node.Branches.Add(branch);
        }
        Complete(node);
        Consume(K.Dedent, "Expected an indented block after \"match\" statement.");
        if (allReturn && wildcard)
        {
            currentSuite!.HasReturn = true;
        }
        foreach (GDScriptAnnotation annotation in branchAnnotations)
        {
            PushError($"Annotation \"{annotation.Name}\" does not precede a valid target, so it will have no effect.", annotation);
        }
        return node;
    }

    private GDScriptMatchBranch ParseMatchBranch(out bool hasWildcard)
    {
        GDScriptMatchBranch branch = Alloc<GDScriptMatchBranch>();
        ResetExtents(branch, current);
        hasWildcard = false;
        bool hasBind = false;
        do
        {
            GDScriptPattern? pattern = ParseMatchPattern(null);
            if (pattern is null)
            {
                continue;
            }
            if (pattern.Binds is { Count: > 0 })
            {
                hasBind = true;
            }
            if (branch.Patterns.Count > 0 && hasBind)
            {
                PushError("Cannot use a variable bind with multiple patterns.");
            }
            if (pattern.Kind == GDScriptPatternKind.Rest)
            {
                PushError("Rest pattern can only be used inside array and dictionary patterns.");
            }
            else if (pattern.Kind is GDScriptPatternKind.Bind or GDScriptPatternKind.Wildcard)
            {
                hasWildcard = true;
            }
            branch.Patterns.Add(pattern);
        }
        while (Match(K.Comma));
        if (branch.Patterns.Count == 0)
        {
            PushError("No pattern found for \"match\" branch.");
        }
        bool guarded = false;
        if (Match(K.When))
        {
            branch.Guard = Alloc<GDScriptSuite>();
            Binds(branch.Guard);
            GDScriptSuite? outer = currentSuite;
            branch.Guard.Parent = outer;
            currentSuite = branch.Guard;
            GDScriptExpression? guard = ParseExpression(false);
            if (guard is null)
            {
                PushError("Expected expression for pattern guard after \"when\".");
            }
            else
            {
                branch.Guard.Statements.Add(guard);
            }
            currentSuite = outer;
            Complete(branch.Guard);
            guarded = true;
            hasWildcard = false;
        }
        if (!Consume(K.Colon, $"Expected \":\"{(guarded ? string.Empty : " or \"when\"")} after \"match\" {(guarded ? "pattern guard" : "patterns")}."))
        {
            branch.Block = new GDScriptSuite { Parent = currentSuite, InLoop = currentSuite?.InLoop ?? false };
            Complete(branch);
            while (current.Kind != K.Newline && !AtEnd)
            {
                Advance();
            }
            if (!AtEnd)
            {
                Advance();
            }
            return branch;
        }
        GDScriptSuite suite = Alloc<GDScriptSuite>();
        Binds(suite);
        branch.Block = ParseSuite("match pattern block", suite);
        Complete(branch);
        return branch;

        void Binds(GDScriptSuite into)
        {
            if (branch.Patterns is [{ Binds: { } binds }, ..])
            {
                foreach ((string name, GDScriptIdentifier bind) in binds)
                {
                    into.AddLocal(name, "pattern bind", bind);
                }
            }
        }
    }

    private GDScriptPattern? ParseMatchPattern(GDScriptPattern? root)
    {
        GDScriptPattern pattern = Alloc<GDScriptPattern>();
        ResetExtents(pattern, current);
        switch (current.Kind)
        {
            case K.Var:
                Advance();
                if (!Consume(K.Identifier, "Expected bind name after \"var\"."))
                {
                    Complete(pattern);
                    return null;
                }
                pattern.Kind = GDScriptPatternKind.Bind;
                pattern.Bind = ParseIdentifier();
                GDScriptPattern owner = root ?? pattern;
                if (root?.Binds is { } taken && taken.ContainsKey(pattern.Bind.Name))
                {
                    PushError($"Bind variable name \"{pattern.Bind.Name}\" was already used in this pattern.");
                    Complete(pattern);
                    return null;
                }
                if (currentSuite?.LocalKind(pattern.Bind.Name) is { } kind)
                {
                    PushError($"There's already a {kind} named \"{pattern.Bind.Name}\" in this scope.");
                    Complete(pattern);
                    return null;
                }
                (owner.Binds ??= new(StringComparer.Ordinal))[pattern.Bind.Name] = pattern.Bind;
                break;
            case K.Underscore:
                Advance();
                pattern.Kind = GDScriptPatternKind.Wildcard;
                break;
            case K.PeriodPeriod:
                Advance();
                pattern.Kind = GDScriptPatternKind.Rest;
                break;
            case K.BracketOpen:
                PushMultiline(true);
                Advance();
                pattern.Kind = GDScriptPatternKind.Array;
                bool restUsed = false;
                do
                {
                    if (AtEnd || Check(K.BracketClose))
                    {
                        break;
                    }
                    GDScriptPattern? sub = ParseMatchPattern(root ?? pattern);
                    if (sub is null)
                    {
                        continue;
                    }
                    if (restUsed)
                    {
                        PushError("The \"..\" pattern must be the last element in the pattern array.");
                    }
                    else if (sub.Kind == GDScriptPatternKind.Rest)
                    {
                        restUsed = true;
                    }
                    pattern.Array.Add(sub);
                }
                while (Match(K.Comma));
                Consume(K.BracketClose, "Expected \"]\" to close the array pattern.");
                PopMultiline();
                break;
            case K.BraceOpen:
                PushMultiline(true);
                Advance();
                pattern.Kind = GDScriptPatternKind.Dictionary;
                bool rest = false;
                do
                {
                    if (Check(K.BraceClose) || AtEnd)
                    {
                        break;
                    }
                    if (Match(K.PeriodPeriod))
                    {
                        if (rest)
                        {
                            PushError("The \"..\" pattern must be the last element in the pattern dictionary.");
                        }
                        else
                        {
                            GDScriptPattern restPattern = Alloc<GDScriptPattern>();
                            Complete(restPattern);
                            restPattern.Kind = GDScriptPatternKind.Rest;
                            pattern.Dictionary.Add((null, restPattern));
                            rest = true;
                        }
                        continue;
                    }
                    GDScriptExpression? key = ParseExpression(false);
                    if (key is null)
                    {
                        PushError("Expected expression as key for dictionary pattern.");
                    }
                    if (Match(K.Colon))
                    {
                        GDScriptPattern? value = ParseMatchPattern(root ?? pattern);
                        if (value is null)
                        {
                            continue;
                        }
                        if (rest)
                        {
                            PushError("The \"..\" pattern must be the last element in the pattern dictionary.");
                        }
                        else if (value.Kind == GDScriptPatternKind.Rest)
                        {
                            PushError("The \"..\" pattern cannot be used as a value.");
                        }
                        else
                        {
                            pattern.Dictionary.Add((key, value));
                        }
                    }
                    else
                    {
                        pattern.Dictionary.Add((key, null));
                    }
                }
                while (Match(K.Comma));
                Consume(K.BraceClose, "Expected \"}\" to close the dictionary pattern.");
                PopMultiline();
                break;
            default:
                GDScriptExpression? expression = ParseExpression(false);
                if (expression is null)
                {
                    PushError("Expected expression for match pattern.");
                    Complete(pattern);
                    return null;
                }
                pattern.Kind = expression is GDScriptLiteral ? GDScriptPatternKind.Literal : GDScriptPatternKind.Expression;
                pattern.Expression = expression;
                break;
        }
        Complete(pattern);
        return pattern;
    }

    private GDScriptWhile ParseWhile()
    {
        GDScriptWhile node = Alloc<GDScriptWhile>();
        node.Condition = ParseExpression(false);
        if (node.Condition is null)
        {
            PushError("Expected conditional expression after \"while\".");
        }
        if (!Match(K.Colon))
        {
            PushError($"Expected \":\" after \"while\" condition, found \"{GDScriptToken.KindName(current.Kind)}\" instead.", current);
        }
        (bool couldBreak, bool couldContinue) = (canBreak, canContinue);
        (canBreak, canContinue) = (true, true);
        GDScriptSuite suite = Alloc<GDScriptSuite>();
        suite.InLoop = true;
        node.Loop = ParseSuite("\"while\" block", suite);
        Complete(node);
        (canBreak, canContinue) = (couldBreak, couldContinue);
        return node;
    }
}
