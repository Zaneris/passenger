namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's checks of a call (shader_language.cpp, MIT): the overload of a built-in, stage or shader function it calls,
/// its arguments read and converted, those it writes to, and the samplers it passes on with their filter and repeat.</summary>
public sealed partial class GodotShaderLanguage
{
    private bool ValidateFunctionCall(BlockNode? pBlock, FunctionInfo pFunctionInfo, OperatorNode pFunc, ref bool rIsCustomFunction)
    {
        if (pFunc.Op != Operator.Call && pFunc.Op != Operator.Construct)
        {
            return false;
        }

        List<DataType> args = [];
        List<string> args2 = [];
        List<int> args3 = [];

        if (pFunc.Arguments[0].Type != NodeType.Variable)
        {
            return false;
        }

        string name = ((VariableNode)pFunc.Arguments[0]).Name;
        string rname = ((VariableNode)pFunc.Arguments[0]).Rname;

        for (int i = 1; i < pFunc.Arguments.Count; i++)
        {
            args.Add(pFunc.Arguments[i].GetDatatype());
            args2.Add(pFunc.Arguments[i].GetDatatypeName());
            args3.Add(pFunc.Arguments[i].GetArraySize());
        }

        int argcount = args.Count;

        if (stages != null)
        {
            // Stage functions can be used in custom functions as well, that why need to check them all.
            foreach (KeyValuePair<string, FunctionInfo> E in stages)
            {
                if (E.Value.StageFunctions.ContainsKey(name))
                {
                    // Stage-based function.
                    StageFunctionInfo sf = E.Value.StageFunctions[name];
                    if (argcount != sf.Arguments.Count)
                    {
                        SetError(Format(("Invalid number of arguments when calling stage function '%s', which expects %d argument(s)."), (name), sf.Arguments.Count));
                        return false;
                    }
                    // Validate arguments.
                    for (int i = 0; i < argcount; i++)
                    {
                        if (args[i] != sf.Arguments[i].Type)
                        {
                            SetError(Format(("Invalid argument type when calling stage function '%s', type expected is '%s'."), (name), GetDatatypeName(sf.Arguments[i].Type)));
                            return false;
                        }
                    }

                    pFunc.ReturnCache = sf.ReturnType;
                    pFunc.StructName = "";
                    return true;
                }
            }
        }

        bool failedBuiltin = false;
        bool unsupportedBuiltin = false;
        int builtinIdx = 0;

        if (argcount <= 4)
        {
            // Test builtins.
            int idx = 0;
            int fullMatchIndex = 0;

            while (builtinFuncDefs[idx].Name != null)
            {
                if (completionClass != builtinFuncDefs[idx].Tag)
                {
                    idx++;
                    continue;
                }

                if (name == builtinFuncDefs[idx].Name)
                {
                    bool fail = false;

                    for (int i = 0; i < argcount; i++)
                    {
                        if (args[i] != builtinFuncDefs[idx].Args[i])
                        {
                            fail = true;
                            break;
                        }
                    }

                    if (!fail)
                    {
                        // Full match.
                        fullMatchIndex = idx;
                        break;
                    }
                }

                idx++;
            }
            idx = fullMatchIndex;

            while (builtinFuncDefs[idx].Name != null)
            {
                if (completionClass != builtinFuncDefs[idx].Tag)
                {
                    idx++;
                    continue;
                }

                if (name == builtinFuncDefs[idx].Name)
                {
                    failedBuiltin = true;
                    bool fail = false;
                    for (int i = 0; i < argcount; i++)
                    {
                        if (pFunc.Arguments[i + 1].Type == NodeType.Array)
                        {
                            ArrayNode anode = ((ArrayNode)pFunc.Arguments[i + 1]);
                            if (anode.CallExpression == null && !anode.IsIndexed())
                            {
                                fail = true;
                                break;
                            }
                        }

                        bool converted = false;
                        if (Before48)
                        {
                            converted = GetScalarType(args[i]) == args[i] && pFunc.Arguments[i + 1].Type == NodeType.Constant
                                && ConvertConstant((ConstantNode)pFunc.Arguments[i + 1], builtinFuncDefs[idx].Args[i]);
                        }
                        else if (GetScalarType(args[i]) == args[i])
                        {
                            if (pFunc.Arguments[i + 1].Type == NodeType.Constant)
                            {
                                if (ConvertConstant(((ConstantNode)pFunc.Arguments[i + 1]), builtinFuncDefs[idx].Args[i], null, IsBuiltinVecConstructor(name)))
                                {
                                    converted = true;
                                }
                            }
                            else if (pFunc.Arguments[i + 1].Type == NodeType.Operator)
                            {
                                if (ConvertOperator(((OperatorNode)pFunc.Arguments[i + 1]), builtinFuncDefs[idx].Args[i], null, IsBuiltinVecConstructor(name)))
                                {
                                    converted = true;
                                }
                            }
                        }
                        if (!converted && args[i] != builtinFuncDefs[idx].Args[i])
                        {
                            fail = true;
                            break;
                        }
                    }

                    if (!fail)
                    {
                        if (compileInfo.LowEnd)
                        {
                            if (builtinFuncDefs[idx].HighEnd)
                            {
                                fail = true;
                                unsupportedBuiltin = true;
                                builtinIdx = idx;
                            }
                        }
                    }

                    if (!fail && argcount < 4 && builtinFuncDefs[idx].Args[argcount] != DataType.Void)
                    {
                        fail = true; // Make sure the number of arguments matches.
                    }

                    if (!fail)
                    {
                        {
                            int constargIdx = 0;
                            while (builtinFuncConstArgs[constargIdx].Name != null)
                            {
                                if ((name) == builtinFuncConstArgs[constargIdx].Name)
                                {
                                    int arg = builtinFuncConstArgs[constargIdx].Arg + 1;
                                    if (pFunc.Arguments.Count <= arg)
                                    {
                                        break;
                                    }

                                    int min = builtinFuncConstArgs[constargIdx].Min;
                                    int max = builtinFuncConstArgs[constargIdx].Max;

                                    bool error = false;
                                    List<Scalar> values = GetNodeValues(pBlock, pFunctionInfo, pFunc.Arguments[arg]);
                                    if (pFunc.Arguments[arg].GetDatatype() == DataType.Int && values.Count > 0)
                                    {
                                        if (values[0].Sint < min || values[0].Sint > max)
                                        {
                                            error = true;
                                        }
                                    }
                                    else
                                    {
                                        error = true;
                                    }

                                    if (error)
                                    {
                                        SetError(Format(("Expected integer constant within [%d..%d] range."), min, max));
                                        return false;
                                    }
                                }
                                constargIdx++;
                            }
                        }

                        // Make sure it's not an out argument used in the wrong way.
                        int outargIdx = 0;
                        while (builtinFuncOutArgs[outargIdx].Name != null)
                        {
                            if ((name) == builtinFuncOutArgs[outargIdx].Name)
                            {
                                for (int arg = 0; arg < BuiltinFuncOutArgs.MaxArgs; arg++)
                                {
                                    int argIdx = builtinFuncOutArgs[outargIdx].Arguments[arg];
                                    if (argIdx == -1)
                                    {
                                        break;
                                    }
                                    if (argIdx < argcount)
                                    {
                                        if (pFunc.Arguments[argIdx + 1].Type != NodeType.Variable && pFunc.Arguments[argIdx + 1].Type != NodeType.Member && pFunc.Arguments[argIdx + 1].Type != NodeType.Array)
                                        {
                                            SetError(Format(("Argument %d of function '%s' is not a variable, array, or member."), argIdx + 1, (name)));
                                            return false;
                                        }

                                        if (pFunc.Arguments[argIdx + 1].Type == NodeType.Array)
                                        {
                                            ArrayNode mn = ((ArrayNode)pFunc.Arguments[argIdx + 1]);
                                            if (mn.IsConst)
                                            {
                                                fail = true;
                                            }
                                        }
                                        else if (pFunc.Arguments[argIdx + 1].Type == NodeType.Member)
                                        {
                                            MemberNode mn = ((MemberNode)pFunc.Arguments[argIdx + 1]);
                                            if (mn.BasetypeConst)
                                            {
                                                fail = true;
                                            }
                                        }
                                        else
                                        { // DataType.Variable
                                            VariableNode vn = ((VariableNode)pFunc.Arguments[argIdx + 1]);
                                            if (vn.IsConst)
                                            {
                                                fail = true;
                                            }
                                            else
                                            {
                                                string varname = vn.Name;
                                                if (shader.Uniforms.ContainsKey(varname))
                                                {
                                                    fail = true;
                                                }
                                                else
                                                {
                                                    if (shader.Varyings.ContainsKey(varname))
                                                    {
                                                        SetError(Format(("Varyings cannot be passed for the '%s' parameter."), "out"));
                                                        return false;
                                                    }
                                                    if (pFunctionInfo.BuiltIns.ContainsKey(varname))
                                                    {
                                                        BuiltInInfo info = pFunctionInfo.BuiltIns[varname];
                                                        if (info.Constant)
                                                        {
                                                            fail = true;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        if (fail)
                                        {
                                            SetError(Format(("A constant value cannot be passed for the '%s' parameter."), "out"));
                                            return false;
                                        }

                                        string varName = "";
                                        if (pFunc.Arguments[argIdx + 1].Type == NodeType.Array)
                                        {
                                            varName = ((ArrayNode)pFunc.Arguments[argIdx + 1]).Name;
                                        }
                                        else if (pFunc.Arguments[argIdx + 1].Type == NodeType.Member)
                                        {
                                            Node n = ((MemberNode)pFunc.Arguments[argIdx + 1]).Owner!;
                                            while (n.Type == NodeType.Member)
                                            {
                                                n = ((MemberNode)n).Owner!;
                                            }
                                            if (n.Type != NodeType.Variable && n.Type != NodeType.Array)
                                            {
                                                SetError(Format(("Argument %d of function '%s' is not a variable, array, or member."), argIdx + 1, (name)));
                                                return false;
                                            }
                                            if (n.Type == NodeType.Variable)
                                            {
                                                varName = ((VariableNode)n).Name;
                                            }
                                            else
                                            { // DataType.Array
                                                varName = ((ArrayNode)n).Name;
                                            }
                                        }
                                        else
                                        { // DataType.Variable
                                            varName = ((VariableNode)pFunc.Arguments[argIdx + 1]).Name;
                                        }
                                        BlockNode? b = pBlock;
                                        bool valid = false;
                                        while (b != null)
                                        {
                                            if (b.Variables.ContainsKey(varName) || pFunctionInfo.BuiltIns.ContainsKey(varName))
                                            {
                                                valid = true;
                                                break;
                                            }
                                            if (b.ParentFunction != null)
                                            {
                                                for (int i = 0; i < b.ParentFunction.Arguments.Count; i++)
                                                {
                                                    if (b.ParentFunction.Arguments[i].Name == varName)
                                                    {
                                                        valid = true;
                                                        break;
                                                    }
                                                }
                                            }
                                            b = b.ParentBlock;
                                        }

                                        if (!valid)
                                        {
                                            SetError(Format(("Argument %d of function '%s' can only take a local variable, array, or member."), argIdx + 1, (name)));
                                            return false;
                                        }
                                    }
                                }
                            }
                            outargIdx++;
                        }
                        // Implicitly convert values if possible.
                        for (int i = 0; i < argcount; i++)
                        {
                            if (GetScalarType(args[i]) != args[i] || args[i] == builtinFuncDefs[idx].Args[i] || pFunc.Arguments[i + 1].Type != NodeType.Constant)
                            {
                                // Can't do implicit conversion here.
                                continue;
                            }

                            // This is an implicit conversion.
                            ConstantNode constant = ((ConstantNode)pFunc.Arguments[i + 1]);
                            ConstantNode conversion = new ConstantNode();

                            conversion.Datatype = builtinFuncDefs[idx].Args[i];
                            conversion.Values = [default];

                            ConvertConstant(constant, builtinFuncDefs[idx].Args[i], conversion.Values);
                            pFunc.Arguments[i + 1] = conversion;
                        }

                        pFunc.ReturnCache = builtinFuncDefs[idx].Rettype;

                        return true;
                    }
                }

                idx++;
            }
        }

        if (unsupportedBuiltin)
        {
            string arglist = "";
            for (int i = 0; i < argcount; i++)
            {
                if (i > 0)
                {
                    arglist += ", ";
                }
                arglist += GetDatatypeName(builtinFuncDefs[builtinIdx].Args[i]);
            }

            SetError(Format(("Built-in function \"%s(%s)\" is only supported on high-end platforms."), (name), arglist));
            return false;
        }

        if (failedBuiltin)
        {
            string failedArgList = "";
            for (int i = 0; i < argcount; i++)
            {
                if (i > 0)
                {
                    failedArgList += ",";
                }

                string argName = "";
                if (args[i] == DataType.Struct)
                {
                    argName = args2[i];
                }
                else
                {
                    argName = GetDatatypeName(args[i]);
                }
                if (args3[i] > 0)
                {
                    argName += "[";
                    argName += Itos(args3[i]);
                    argName += "]";
                }
                failedArgList += argName;
            }
            SetError(Format(("Invalid arguments for the built-in function: \"%s(%s)\"."), (name), failedArgList));
            return false;
        }

        // try existing functions..

        string excludeFunction = "";
        BlockNode? block = pBlock;

        while (block != null)
        {
            if (block.ParentFunction != null)
            {
                excludeFunction = block.ParentFunction.Name;
            }
            block = block.ParentBlock;
        }

        if (name == excludeFunction)
        {
            SetError(("Recursion is not allowed."));
            return false;
        }

        int lastArgCount = 0;
        bool exists = false;
        string argList = "";
        bool overloadFail = false;

        List<OverloadErrorInfo> overloadErrors = [];

        for (int i = 0; i < shader.Vfunctions.Count; i++)
        {
            if (rname != shader.Vfunctions[i].Rname)
            {
                continue;
            }
            exists = true;

            if (!shader.Vfunctions[i].Callable)
            {
                SetError(Format(("Function '%s' can't be called from source code."), (name)));
                return false;
            }

            FunctionNode? pfunc = shader.Vfunctions[i].Function;

            argList = "";
            for (int j = 0; j < pfunc.Arguments.Count; j++)
            {
                if (j > 0)
                {
                    argList += ", ";
                }
                string funcArgName = "";
                if (pfunc.Arguments[j].Type == DataType.Struct)
                {
                    funcArgName = pfunc.Arguments[j].StructName;
                }
                else
                {
                    funcArgName = GetDatatypeName(pfunc.Arguments[j].Type);
                }
                if (pfunc.Arguments[j].ArraySize > 0)
                {
                    funcArgName += "[";
                    funcArgName += Itos(pfunc.Arguments[j].ArraySize);
                    funcArgName += "]";
                }
                argList += funcArgName;
            }

            if (pfunc.Arguments.Count != args.Count)
            {
                lastArgCount = pfunc.Arguments.Count;
                continue;
            }

            bool fail = false;
            bool useConstantConversion = functionOverloadCount[rname] == 0;

            for (int j = 0; j < args.Count; j++)
            {
                if (useConstantConversion && GetScalarType(args[j]) == args[j] && pFunc.Arguments[j + 1].Type == NodeType.Constant && args3[j] == 0 && ConvertConstant(((ConstantNode)pFunc.Arguments[j + 1]), pfunc.Arguments[j].Type))
                {
                    //all good, but it needs implicit conversion later
                }
                else if (args[j] != pfunc.Arguments[j].Type || (args[j] == DataType.Struct && args2[j] != pfunc.Arguments[j].StructName) || args3[j] != pfunc.Arguments[j].ArraySize)
                {
                    string funcArgName = "";
                    if (pfunc.Arguments[j].Type == DataType.Struct)
                    {
                        funcArgName = pfunc.Arguments[j].StructName;
                    }
                    else
                    {
                        funcArgName = GetDatatypeName(pfunc.Arguments[j].Type);
                    }
                    if (pfunc.Arguments[j].ArraySize > 0)
                    {
                        funcArgName += "[";
                        funcArgName += Itos(pfunc.Arguments[j].ArraySize);
                        funcArgName += "]";
                    }
                    string argName = "";
                    if (args[j] == DataType.Struct)
                    {
                        argName = args2[j];
                    }
                    else
                    {
                        argName = GetDatatypeName(args[j]);
                    }
                    if (args3[j] > 0)
                    {
                        argName += "[";
                        argName += Itos(args3[j]);
                        argName += "]";
                    }

                    fail = true;
                    OverloadErrorInfo errInfo = new();
                    errInfo.ArgList = argList;
                    errInfo.Index = j + 1;
                    errInfo.FuncArgName = funcArgName;
                    errInfo.ArgName = argName;
                    overloadErrors.Add(errInfo);
                    overloadFail = true;
                    break;
                }
                else
                {
                    overloadFail = false;
                }
            }

            if (!fail)
            {
                //implicitly convert values if possible
                for (int k = 0; k < args.Count; k++)
                {
                    if (GetScalarType(args[k]) != args[k] || args[k] == pfunc.Arguments[k].Type || pFunc.Arguments[k + 1].Type != NodeType.Constant)
                    {
                        //can't do implicit conversion here
                        continue;
                    }

                    //this is an implicit conversion
                    ConstantNode constant = ((ConstantNode)pFunc.Arguments[k + 1]);
                    ConstantNode conversion = new ConstantNode();

                    conversion.Datatype = pfunc.Arguments[k].Type;
                    conversion.Values = [default];

                    ConvertConstant(constant, pfunc.Arguments[k].Type, conversion.Values);
                    pFunc.Arguments[k + 1] = conversion;
                }

                pFunc.ReturnCache = pfunc.ReturnType;
                if (pfunc.ReturnType == DataType.Struct)
                {
                    pFunc.StructName = pfunc.ReturnStructName;
                }

                rIsCustomFunction = true;
                return true;
            }
        }
        if (overloadFail)
        {
            string errStr = "";
            if (overloadErrors.Count == 1)
            {
                OverloadErrorInfo errInfo = overloadErrors[0];
                errStr = Format("No matching function for \"%s(%s)\" call: argument %d should be %s but is %s.", (rname), errInfo.ArgList, errInfo.Index, errInfo.FuncArgName, errInfo.ArgName);
            }
            else
            {
                errStr = Format(("No matching function for \"%s\" call:"), (rname));
                foreach (OverloadErrorInfo errInfo in overloadErrors)
                {
                    errStr += "\n\t" + Format(("candidate function \"%s(%s)\" not viable, argument %d should be %s but is %s."), (rname), errInfo.ArgList, errInfo.Index, errInfo.FuncArgName, errInfo.ArgName);
                }
            }
            SetError(errStr);
        }

        if (exists)
        {
            if (lastArgCount > args.Count)
            {
                SetError(Format(("Too few arguments for \"%s(%s)\" call. Expected at least %d but received %d."), (rname), argList, lastArgCount, args.Count));
            }
            else if (lastArgCount < args.Count)
            {
                SetError(Format(("Too many arguments for \"%s(%s)\" call. Expected at most %d but received %d."), (rname), argList, lastArgCount, args.Count));
            }
        }

        return false;
    }

    private bool CompareDatatypes(DataType pDatatypeA, string pDatatypeNameA, int pArraySizeA, DataType pDatatypeB, string pDatatypeNameB, int pArraySizeB)
    {
        bool result = true;

        if (pDatatypeA == DataType.Struct || pDatatypeB == DataType.Struct)
        {
            if (pDatatypeNameA != pDatatypeNameB)
            {
                result = false;
            }
        }
        else
        {
            if (pDatatypeA != pDatatypeB)
            {
                result = false;
            }
        }

        if (pArraySizeA != pArraySizeB)
        {
            result = false;
        }

        if (!result)
        {
            string typeName = pDatatypeA == DataType.Struct ? pDatatypeNameA : GetDatatypeName(pDatatypeA);
            if (pArraySizeA > 0)
            {
                typeName += "[";
                typeName += Itos(pArraySizeA);
                typeName += "]";
            }

            string typeName2 = pDatatypeB == DataType.Struct ? pDatatypeNameB : GetDatatypeName(pDatatypeB);
            if (pArraySizeB > 0)
            {
                typeName2 += "[";
                typeName2 += Itos(pArraySizeB);
                typeName2 += "]";
            }

            SetError(Format(("Invalid assignment of '%s' to '%s'."), typeName2, typeName));
        }
        return result;
    }

    private bool CompareDatatypesInNodes(Node a, Node b)
    {
        return CompareDatatypes(a.GetDatatype(), a.GetDatatypeName(), a.GetArraySize(), b.GetDatatype(), b.GetDatatypeName(), b.GetArraySize());
    }

    private bool ParseFunctionArguments(BlockNode? pBlock, FunctionInfo pFunctionInfo, OperatorNode pFunc, ref int rCompleteArg)
    {
        TkPos pos = GetTkPos();
        Token tk = GetToken();

        if (tk.Type == Tk.ParenthesisClose)
        {
            return true;
        }

        SetTkPos(pos);

        while (true)
        {
            pos = GetTkPos();
            tk = GetToken();

            if (tk.Type == Tk.Cursor)
            {
                rCompleteArg = pFunc.Arguments.Count - 1;
            }
            else
            {
                SetTkPos(pos);
            }

            Node? arg = ParseAndReduceExpression(pBlock, pFunctionInfo);

            if (arg == null)
            {
                return false;
            }

            if (isConstDecl && arg.Type == NodeType.Variable)
            {
                VariableNode variable = ((VariableNode)arg);
                if (!variable.IsConst)
                {
                    SetError(("Expected constant expression."));
                    return false;
                }
            }

            pFunc.Arguments.Add(arg);

            tk = GetToken();

            if (tk.Type == Tk.ParenthesisClose)
            {
                return true;
            }
            else if (tk.Type != Tk.Comma)
            {
                // something is broken
                SetError(("Expected ',' or ')' after argument."));
                return false;
            }
        }
    }

    private bool CheckRestrictedFunc(string pName, string pCurrentFunction)
    {
        int idx = 0;

        while (fragOnlyFuncDefs[idx].Name != null)
        {
            if ((fragOnlyFuncDefs[idx].Name) == pName)
            {
                if (isSupportedFragOnlyFuncs)
                {
                    if (pCurrentFunction == "vertex" && stages.ContainsKey(pCurrentFunction))
                    {
                        return true;
                    }
                }
                else
                {
                    return true;
                }
                break;
            }
            idx++;
        }

        return false;
    }

    private bool ValidateRestrictedFunc(string pName, CallInfo pFuncInfo, bool pIsBuiltinHint = false)
    {
        bool isInRestrictedFunction = pFuncInfo.Name == "vertex";

        // No need to check up the hierarchy if it's a built-in.
        if (!pIsBuiltinHint)
        {
            foreach (CallInfo funcInfo in pFuncInfo.Calls)
            {
                if (isInRestrictedFunction && funcInfo.Name != pName)
                {
                    // Skips check for non-called method.
                    continue;
                }

                if (!ValidateRestrictedFunc(pName, funcInfo))
                {
                    return false;
                }
            }
        }

        if (pFuncInfo.UsesRestrictedItems.Count > 0)
        {
            (string First, CallItem Second) firstElement = pFuncInfo.UsesRestrictedItems[0];

            if (firstElement.Second.Type == CallItemType.Varying)
            {
                Varying varying = shader.Varyings[firstElement.First];

                if (varying.Stage == VaryingStage.Vertex)
                {
                    return true;
                }
            }

            SetTkPos(firstElement.Second.Pos);

            if (isInRestrictedFunction)
            {
                SetError(Format(("'%s' cannot be used within the '%s' processor function."), firstElement.First, "vertex"));
            }
            else
            {
                SetError(Format(("'%s' cannot be used here, because '%s' is called by the '%s' processor function (which is not allowed)."), firstElement.First, pFuncInfo.Name, "vertex"));
            }
            return false;
        }

        return true;
    }

    private bool PropagateFunctionCallSamplerUniformSettings(string pName, int pArgument, TextureFilter pFilter, TextureRepeat pRepeat, Hint pHint)
    {
        for (int i = 0; i < shader.Vfunctions.Count; i++)
        {
            if (shader.Vfunctions[i].Name == pName)
            {
                if (pArgument < 0 || pArgument >= shader.Vfunctions[i].Function.Arguments.Count)
                {
                    return false;
                }
                FunctionArgument arg = shader.Vfunctions[i].Function.Arguments[pArgument];
                if (arg.TexBuiltinCheck)
                {
                    SetError(Format(("Sampler argument %d of function '%s' called more than once using both built-ins and uniform textures, this is not supported (use either one or the other)."), pArgument, (pName)));
                    return false;
                }
                else if (arg.TexArgumentCheck)
                {
                    // Was checked, verify that filter, repeat, and hint are the same.
                    if (arg.TexArgumentFilter == pFilter && arg.TexArgumentRepeat == pRepeat && arg.TexHint == SanitizeHint(pHint))
                    {
                        return true;
                    }
                    else
                    {
                        SetError(Format(("Sampler argument %d of function '%s' called more than once using textures that differ in either filter, repeat, or texture hint setting."), pArgument, (pName)));
                        return false;
                    }
                }
                else
                {
                    arg.TexArgumentCheck = true;
                    arg.TexArgumentFilter = pFilter;
                    arg.TexArgumentRepeat = pRepeat;
                    arg.TexHint = SanitizeHint(pHint);
                    foreach (KeyValuePair<string, HashSet<int>> E in arg.TexArgumentConnect)
                    {
                        foreach (int F in E.Value)
                        {
                            if (!PropagateFunctionCallSamplerUniformSettings(E.Key, F, pFilter, pRepeat, pHint))
                            {
                                return false;
                            }
                        }
                    }
                    return true;
                }
            }
        }
        return false; //bug? function not found
    }

    private bool PropagateFunctionCallSamplerBuiltinReference(string pName, int pArgument, string pBuiltin)
    {
        for (int i = 0; i < shader.Vfunctions.Count; i++)
        {
            if (shader.Vfunctions[i].Name == pName)
            {
                if (pArgument < 0 || pArgument >= shader.Vfunctions[i].Function.Arguments.Count)
                {
                    return false;
                }
                FunctionArgument arg = shader.Vfunctions[i].Function.Arguments[pArgument];
                if (arg.TexArgumentCheck)
                {
                    SetError(Format(("Sampler argument %d of function '%s' called more than once using both built-ins and uniform textures, this is not supported (use either one or the other)."), pArgument, (pName)));
                    return false;
                }
                else if (arg.TexBuiltinCheck)
                {
                    //was checked, verify that the built-in is the same
                    if (arg.TexBuiltin == pBuiltin)
                    {
                        return true;
                    }
                    else
                    {
                        SetError(Format(("Sampler argument %d of function '%s' called more than once using different built-ins. Only calling with the same built-in is supported."), pArgument, (pName)));
                        return false;
                    }
                }
                else
                {
                    arg.TexBuiltinCheck = true;
                    arg.TexBuiltin = pBuiltin;

                    foreach (KeyValuePair<string, HashSet<int>> E in arg.TexArgumentConnect)
                    {
                        foreach (int F in E.Value)
                        {
                            if (!PropagateFunctionCallSamplerBuiltinReference(E.Key, F, pBuiltin))
                            {
                                return false;
                            }
                        }
                    }
                    return true;
                }
            }
        }
        return false; //bug? function not found
    }

    private static bool IsBuiltinVecConstructor(string pName)
    {
        int i = 0;
        while (builtinVectorizedConstructors[i].Name != null)
        {
            if (pName == builtinVectorizedConstructors[i].Name)
            {
                return true;
            }
            i++;
        }
        return false;
    }

    private static bool IsBuiltinFuncOutParameter(string pName, int pParam)
    {
        int i = 0;
        while (builtinFuncOutArgs[i].Name != null)
        {
            if (pName == builtinFuncOutArgs[i].Name)
            {
                for (int j = 0; j < BuiltinFuncOutArgs.MaxArgs; j++)
                {
                    int arg = builtinFuncOutArgs[i].Arguments[j];
                    if (arg == pParam)
                    {
                        return true;
                    }
                    if (arg < 0)
                    {
                        return false;
                    }
                }
            }
            i++;
        }
        return false;
    }
}
