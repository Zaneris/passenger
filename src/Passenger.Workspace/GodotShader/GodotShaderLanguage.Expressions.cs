namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's reading of an expression (shader_language.cpp, MIT): operands, calls, constructors, members, swizzles and
/// indexing, then its operators by precedence, then constants folded; and array sizes and constructors.</summary>
public sealed partial class GodotShaderLanguage
{
    private Err ParseArraySize(BlockNode? pBlock, FunctionInfo pFunctionInfo, bool pForbidUnknownSize, ref int rArraySize)
    {
        bool unknownSize = false;
        return ParseArraySize(pBlock, pFunctionInfo, pForbidUnknownSize, ref rArraySize, ref unknownSize, out _);
    }

    private Err ParseArraySize(BlockNode? pBlock, FunctionInfo pFunctionInfo, bool pForbidUnknownSize, ref int rArraySize, ref bool rUnknownSize, out Node? rSizeExpression)
    {
        rSizeExpression = null;
        bool error = false;
        if (rArraySize > 0)
        {
            error = true;
        }
        if (rUnknownSize)
        {
            error = true;
        }
        if (error)
        {
            SetError(Format(("Array size is already defined.")));
            return Err.ParseError;
        }

        TkPos pos = GetTkPos();
        Token tk = GetToken();

        if (tk.Type == Tk.BracketClose)
        {
            if (pForbidUnknownSize)
            {
                SetError(Format(("Unknown array size is forbidden in that context.")));
                return Err.ParseError;
            }
            rUnknownSize = true;
        }
        else
        {
            SetTkPos(pos);

            int arraySize = 0;
            Node? expr = ParseAndReduceExpression(pBlock, pFunctionInfo);

            if (expr != null)
            {
                List<Scalar> values = GetNodeValues(pBlock, pFunctionInfo, expr);

                if (values.Count > 0)
                {
                    switch (expr.GetDatatype())
                    {
                        case DataType.Int:
                            {
                                arraySize = values[0].Sint;
                            }
                            break;
                        case DataType.Uint:
                            {
                                arraySize = (int)values[0].Uint;
                            }
                            break;
                        default:
                            {
                            }
                            break;
                    }
                }

                rSizeExpression = expr;
            }

            if (arraySize <= 0)
            {
                SetError(("Expected a positive integer constant."));
                return Err.ParseError;
            }

            tk = GetToken();
            if (tk.Type != Tk.BracketClose)
            {
                SetExpectedError("]");
                return Err.ParseError;
            }

            rArraySize = arraySize;
        }
        return Err.Ok;
    }

    private Node? ParseArrayConstructor(BlockNode? pBlock, FunctionInfo pFunctionInfo)
    {
        DataType type = DataType.Void;
        string structName = "";
        int arraySize = 0;
        bool autoSize = false;
        bool undefinedSize = false;
        Token tk = GetToken();

        if (tk.Type == Tk.CurlyBracketOpen)
        {
            autoSize = true;
        }
        else
        {
            if (shader.Structs.ContainsKey(tk.Text))
            {
                type = DataType.Struct;
                structName = tk.Text;
            }
            else
            {
                if (!IsTokenVariableDatatype(tk.Type))
                {
                    SetError(("Invalid data type for the array."));
                    return null;
                }
                type = GetTokenDatatype(tk.Type);
            }
            tk = GetToken();
            if (tk.Type == Tk.BracketOpen)
            {
                Err error = ParseArraySize(pBlock, pFunctionInfo, false, ref arraySize, ref undefinedSize, out _);
                if (error != Err.Ok)
                {
                    return null;
                }
                tk = GetToken();
            }
            else
            {
                SetExpectedError("[");
                return null;
            }
        }

        ArrayConstructNode an = new ArrayConstructNode();

        if (tk.Type == Tk.ParenthesisOpen || autoSize)
        { // initialization
            int idx = 0;
            while (true)
            {
                Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);
                if (n == null)
                {
                    return null;
                }

                // define type by using the first member
                if (autoSize && idx == 0)
                {
                    type = n.GetDatatype();
                    if (type == DataType.Struct)
                    {
                        structName = n.GetDatatypeName();
                    }
                }
                else
                {
                    if (!CompareDatatypes(type, structName, 0, n.GetDatatype(), n.GetDatatypeName(), 0))
                    {
                        return null;
                    }
                }

                tk = GetToken();
                if (tk.Type == Tk.Comma)
                {
                    an.Initializer.Add(n);
                }
                else if (!autoSize && tk.Type == Tk.ParenthesisClose)
                {
                    an.Initializer.Add(n);
                    break;
                }
                else if (autoSize && tk.Type == Tk.CurlyBracketClose)
                {
                    an.Initializer.Add(n);
                    break;
                }
                else
                {
                    if (autoSize)
                    {
                        SetExpectedError("}", ",");
                    }
                    else
                    {
                        SetExpectedError(")", ",");
                    }
                    return null;
                }
                idx++;
            }
            if (!autoSize && !undefinedSize && an.Initializer.Count != arraySize)
            {
                SetError(Format(("Array size mismatch. Expected %d elements (found %d)."), arraySize, an.Initializer.Count));
                return null;
            }
        }
        else
        {
            SetError(("Expected array initialization."));
            return null;
        }

        an.Datatype = type;
        an.StructName = structName;
        return an;
    }

    private Node? ParseArrayConstructor(BlockNode? pBlock, FunctionInfo pFunctionInfo, DataType pType, string pStructName, int pArraySize)
    {
        DataType type = DataType.Void;
        string structName = "";
        int arraySize = 0;
        bool autoSize = false;
        TkPos prevPos = GetTkPos();
        Token tk = GetToken();

        if (tk.Type == Tk.CurlyBracketOpen)
        {
            autoSize = true;
        }
        else
        {
            if (shader.Structs.ContainsKey(tk.Text))
            {
                type = DataType.Struct;
                structName = tk.Text;
            }
            else
            {
                if (!IsTokenVariableDatatype(tk.Type))
                {
                    SetTkPos(prevPos);

                    Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);

                    if (n == null)
                    {
                        SetError(("Invalid data type for the array."));
                        return null;
                    }

                    if (!CompareDatatypes(pType, pStructName, pArraySize, n.GetDatatype(), n.GetDatatypeName(), n.GetArraySize()))
                    {
                        return null;
                    }
                    return n;
                }
                type = GetTokenDatatype(tk.Type);
            }
            tk = GetToken();
            if (tk.Type == Tk.BracketOpen)
            {
                bool isUnknownSize = false;
                Err error = ParseArraySize(pBlock, pFunctionInfo, false, ref arraySize, ref isUnknownSize, out _);
                if (error != Err.Ok)
                {
                    return null;
                }
                if (isUnknownSize)
                {
                    arraySize = pArraySize;
                }
                tk = GetToken();
            }
            else
            {
                SetExpectedError("[");
                return null;
            }

            if (type != pType || structName != pStructName || arraySize != pArraySize)
            {
                string from = "";
                if (type == DataType.Struct)
                {
                    from += structName;
                }
                else
                {
                    from += GetDatatypeName(type);
                }
                from += "[";
                from += Itos(arraySize);
                from += "]'";

                string to = "";
                if (type == DataType.Struct)
                {
                    to += pStructName;
                }
                else
                {
                    to += GetDatatypeName(pType);
                }
                to += "[";
                to += Itos(pArraySize);
                to += "]'";

                SetError(Format(("Cannot convert from '%s' to '%s'."), from, to));
                return null;
            }
        }

        ArrayConstructNode an = new ArrayConstructNode();
        an.Datatype = pType;
        an.StructName = pStructName;

        if (tk.Type == Tk.ParenthesisOpen || autoSize)
        { // initialization
            while (true)
            {
                Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);
                if (n == null)
                {
                    return null;
                }

                if (!CompareDatatypes(pType, pStructName, 0, n.GetDatatype(), n.GetDatatypeName(), n.GetArraySize()))
                {
                    return null;
                }

                tk = GetToken();
                if (tk.Type == Tk.Comma)
                {
                    an.Initializer.Add(n);
                }
                else if (!autoSize && tk.Type == Tk.ParenthesisClose)
                {
                    an.Initializer.Add(n);
                    break;
                }
                else if (autoSize && tk.Type == Tk.CurlyBracketClose)
                {
                    an.Initializer.Add(n);
                    break;
                }
                else
                {
                    if (autoSize)
                    {
                        SetExpectedError("}", ",");
                    }
                    else
                    {
                        SetExpectedError(")", ",");
                    }
                    return null;
                }
            }
            if (an.Initializer.Count != pArraySize)
            {
                SetError(Format(("Array size mismatch. Expected %d elements (found %d)."), pArraySize, an.Initializer.Count));
                return null;
            }
        }
        else
        {
            SetError(("Expected array initialization."));
            return null;
        }

        return an;
    }

    private Node? ParseExpression(BlockNode? pBlock, FunctionInfo pFunctionInfo, ExpressionInfo? pPreviousExpressionInfo = null)
    {
        List<Expression> expression = [];

        //List<Tk> operators;
        bool checkPositionWrite = checkWarnings && HasWarning(GodotShaderWarningFlags.MagicPositionWrite);
        checkPositionWrite = checkPositionWrite && (shaderTypeIdentifier) == "spatial" && currentFunction == "vertex";

        while (true)
        {
            Node? expr = null;
            TkPos prepos = GetTkPos();
            Token tk = GetToken();
            TkPos pos = GetTkPos();

            bool isConst = false;

            if (tk.Type == Tk.ParenthesisOpen)
            {
                //handle subexpression

                expr = ParseAndReduceExpression(pBlock, pFunctionInfo);
                if (expr == null)
                {
                    return null;
                }

                tk = GetToken();

                if (tk.Type != Tk.ParenthesisClose)
                {
                    SetError(("Expected ')' in expression."));
                    return null;
                }

            }
            else if (tk.Type == Tk.FloatConstant)
            {
                ConstantNode constant = new ConstantNode();
                Scalar v = default;
                v.Real = (float)tk.Constant;
                constant.Values.Add(v);
                constant.Datatype = DataType.Float;
                expr = constant;

            }
            else if (tk.Type == Tk.IntConstant)
            {
                ConstantNode constant = new ConstantNode();
                Scalar v = default;
                v.Sint = (int)tk.Constant;
                constant.Values.Add(v);
                constant.Datatype = DataType.Int;
                expr = constant;

            }
            else if (tk.Type == Tk.UintConstant)
            {
                ConstantNode constant = new ConstantNode();
                Scalar v = default;
                v.Uint = (uint)tk.Constant;
                constant.Values.Add(v);
                constant.Datatype = DataType.Uint;
                expr = constant;

            }
            else if (tk.Type == Tk.True)
            {
                //handle true constant
                ConstantNode constant = new ConstantNode();
                Scalar v = default;
                v.Boolean = true;
                constant.Values.Add(v);
                constant.Datatype = DataType.Bool;
                expr = constant;

            }
            else if (tk.Type == Tk.False)
            {
                //handle false constant
                ConstantNode constant = new ConstantNode();
                Scalar v = default;
                v.Boolean = false;
                constant.Values.Add(v);
                constant.Datatype = DataType.Bool;
                expr = constant;

            }
            else if (tk.Type == Tk.TypeVoid)
            {
                //make sure void is not used in expression
                SetError(("Void value not allowed in expression."));
                return null;
            }
            else if (IsTokenNonvoidDatatype(tk.Type) || tk.Type == Tk.CurlyBracketOpen)
            {
                if (tk.Type == Tk.CurlyBracketOpen)
                {
                    //array constructor

                    SetTkPos(prepos);
                    expr = ParseArrayConstructor(pBlock, pFunctionInfo);
                }
                else
                {
                    DataType datatype;
                    DataPrecision precision = DataPrecision.Default;

                    if (IsTokenPrecision(tk.Type))
                    {
                        precision = GetTokenPrecision(tk.Type);
                        tk = GetToken();
                    }

                    datatype = GetTokenDatatype(tk.Type);
                    if (precision != DataPrecision.Default && ValidatePrecision(datatype, precision) != Err.Ok)
                    {
                        return null;
                    }

                    tk = GetToken();

                    if (tk.Type == Tk.BracketOpen)
                    {
                        //array constructor

                        SetTkPos(prepos);
                        expr = ParseArrayConstructor(pBlock, pFunctionInfo);
                    }
                    else
                    {
                        if (tk.Type != Tk.ParenthesisOpen)
                        {
                            SetError(("Expected '(' after the type name."));
                            return null;
                        }
                        //basic type constructor

                        OperatorNode func = new OperatorNode();
                        func.Op = Operator.Construct;

                        if (precision != DataPrecision.Default)
                        {
                            func.ReturnPrecisionCache = precision;
                        }

                        VariableNode funcname = new VariableNode();
                        funcname.Name = GetDatatypeName(datatype);
                        func.Arguments.Add(funcname);

                        int carg = -1;

                        bool ok = ParseFunctionArguments(pBlock, pFunctionInfo, func, ref carg);

                        if (carg >= 0)
                        {
                            completionType = CompletionType.CallArguments;
                            completionLine = tkLine;
                            completionBlock = pBlock;
                            completionFunction = funcname.Name;
                            completionArgument = carg;
                        }

                        if (!ok)
                        {
                            return null;
                        }

                        bool customConstructor = false;
                        if (!ValidateFunctionCall(pBlock, pFunctionInfo, func, ref customConstructor))
                        {
                            SetError(Format(("No matching constructor found for: '%s'."), (funcname.Name)));
                            return null;
                        }

                        expr = ReduceExpression(pBlock, func);
                    }
                }
            }
            else if (tk.Type == Tk.Identifier)
            {
                SetTkPos(prepos);

                string identifier = "";

                StructNode? pstruct = null;
                bool structInit = false;

                GetCompletableIdentifier(pBlock, CompletionType.Identifier, out identifier);

                if (shader.Structs.ContainsKey(identifier))
                {
                    pstruct = shader.Structs[identifier].ShaderStruct;
                    structInit = true;
                }

                tk = GetToken();
                if (tk.Type == Tk.ParenthesisOpen)
                {
                    if (structInit && pstruct != null)
                    { //a struct constructor

                        string name = identifier;

                        OperatorNode func = new OperatorNode();
                        func.Op = Operator.Struct;
                        func.StructName = name;
                        func.ReturnCache = DataType.Struct;
                        VariableNode funcname = new VariableNode();
                        funcname.Name = name;
                        func.Arguments.Add(funcname);

                        for (int m = 0; m < pstruct.Members.Count; m++)
                        {
                            MemberNode member = pstruct.Members[m];
                            Node? nexpr;

                            if (member.ArraySize != 0)
                            {
                                nexpr = ParseArrayConstructor(pBlock, pFunctionInfo, member.GetDatatype(), member.StructName, member.ArraySize);
                                if (nexpr == null)
                                {
                                    return null;
                                }
                            }
                            else
                            {
                                nexpr = ParseAndReduceExpression(pBlock, pFunctionInfo);
                                if (nexpr == null)
                                {
                                    return null;
                                }
                                if (!CompareDatatypesInNodes(member, nexpr))
                                {
                                    return null;
                                }
                            }

                            if (m + 1 < pstruct.Members.Count)
                            {
                                tk = GetToken();
                                if (tk.Type != Tk.Comma)
                                {
                                    SetExpectedError(",");
                                    return null;
                                }
                            }
                            func.Arguments.Add(nexpr);
                        }
                        tk = GetToken();
                        if (tk.Type != Tk.ParenthesisClose)
                        {
                            SetExpectedError(")");
                            return null;
                        }

                        expr = func;

                    }
                    else
                    { //a function call

                        // Non-builtin function call is forbidden for constant declaration.
                        if (isConstDecl)
                        {
                            if (shader.Functions.ContainsKey(identifier))
                            {
                                SetError(("Expected constant expression."));
                                return null;
                            }
                        }

                        string rname = identifier;
                        string name = identifier;

                        OperatorNode func = new OperatorNode();
                        func.Op = Operator.Call;

                        VariableNode funcname = new VariableNode();
                        funcname.Name = name;
                        funcname.Rname = name;

                        func.Arguments.Add(funcname);

                        int carg = -1;

                        bool ok = ParseFunctionArguments(pBlock, pFunctionInfo, func, ref carg);

                        // Check if block has a variable with the same name as function to prevent shader crash.
                        BlockNode? bnode = pBlock;
                        while (bnode != null)
                        {
                            if (bnode.Variables.ContainsKey(name))
                            {
                                SetError(("Expected a function name."));
                                return null;
                            }
                            bnode = bnode.ParentBlock;
                        }

                        long argCount = func.Arguments.Count;
                        long argCount2 = func.Arguments.Count - 1;

                        // Test if function was parsed first.
                        int functionIndex = -1;
                        for (int i = 0, maxValidArgs = 0; i < shader.Vfunctions.Count; i++)
                        {
                            if (!shader.Vfunctions[i].Callable || shader.Vfunctions[i].Rname != rname || argCount2 != shader.Vfunctions[i].Function.Arguments.Count)
                            {
                                continue;
                            }

                            bool found = true;
                            int validArgs = 0;

                            // Search for correct overload.
                            for (int j = 1; j < argCount; j++)
                            {
                                FunctionArgument a = shader.Vfunctions[i].Function.Arguments[j - 1];
                                Node? b = func.Arguments[j];

                                if (a.Type == b.GetDatatype() && a.ArraySize == b.GetArraySize())
                                {
                                    if (a.Type == DataType.Struct)
                                    {
                                        if (a.StructName != b.GetDatatypeName())
                                        {
                                            found = false;
                                            break;
                                        }
                                        else
                                        {
                                            validArgs++;
                                        }
                                    }
                                    else
                                    {
                                        validArgs++;
                                    }
                                }
                                else
                                {
                                    if (functionOverloadCount[rname] == 0 && GetScalarType(a.Type) == a.Type && b.Type == NodeType.Constant && a.ArraySize == 0 && ConvertConstant(((ConstantNode)b), a.Type))
                                    {
                                        // Implicit cast if no overloads.
                                        continue;
                                    }
                                    found = false;
                                    break;
                                }
                            }

                            // Using the best match index for completion hint if the function not found.
                            if (validArgs > maxValidArgs)
                            {
                                name = shader.Vfunctions[i].Name;
                                funcname.Name = name;
                                maxValidArgs = validArgs;
                            }

                            if (!found)
                            {
                                continue;
                            }

                            // Add to current function as dependency.
                            for (int j = 0; j < shader.Vfunctions.Count; j++)
                            {
                                if (shader.Vfunctions[j].Name == currentFunction)
                                {
                                    shader.Vfunctions[j].UsesFunction.Add(name);
                                    break;
                                }
                            }

                            name = shader.Vfunctions[i].Name;
                            funcname.Name = name;

                            // See if texture arguments must connect.
                            functionIndex = i;
                            break;
                        }

                        if (carg >= 0)
                        {
                            completionType = CompletionType.CallArguments;
                            completionLine = tkLine;
                            completionBlock = pBlock;
                            completionFunction = funcname.Name;
                            completionArgument = carg;
                        }

                        if (!ok)
                        {
                            return null;
                        }

                        if (name != currentFunction)
                        { // Recursion is not allowed.
                          // Register call.
                            if (callsInfo.ContainsKey(name))
                            {
                                callsInfo[currentFunction].Calls.Add(callsInfo[name]);
                            }

                            bool isBuiltin = false;

                            if (isSupportedFragOnlyFuncs)
                            {
                                foreach (KeyValuePair<string, FunctionInfo> E in stages)
                                {
                                    if (E.Value.StageFunctions.ContainsKey(name))
                                    {
                                        // Register usage of the restricted stage function.
                                        callsInfo[currentFunction].UsesRestrictedItems.Add((name, new CallItem(CallItemType.Builtin, GetTkPos())));
                                        isBuiltin = true;
                                        break;
                                    }
                                }
                            }

                            if (!isBuiltin)
                            {
                                int idx = 0;
                                while (fragOnlyFuncDefs[idx].Name != null)
                                {
                                    if (fragOnlyFuncDefs[idx].Name == name)
                                    {
                                        // If a built-in function not found for the current shader type, then it shouldn't be parsed further.
                                        if (!isSupportedFragOnlyFuncs)
                                        {
                                            SetError(Format(("Built-in function '%s' is not supported for the '%s' shader type."), name, shaderTypeIdentifier));
                                            return null;
                                        }
                                        // Register usage of the restricted function.
                                        callsInfo[currentFunction].UsesRestrictedItems.Add((name, new CallItem(CallItemType.Builtin, GetTkPos())));
                                        isBuiltin = true;
                                        break;
                                    }
                                    idx++;
                                }
                            }

                            // Recursively checks for the restricted function call.
                            if (isSupportedFragOnlyFuncs && currentFunction == "vertex" && stages.ContainsKey(currentFunction) && !ValidateRestrictedFunc(name, callsInfo[currentFunction], isBuiltin))
                            {
                                return null;
                            }
                        }

                        bool isCustomFunc = false;
                        if (!ValidateFunctionCall(pBlock, pFunctionInfo, func, ref isCustomFunc))
                        {
                            SetError(Format(("No matching function found for: '%s'."), (funcname.Rname)));
                            return null;
                        }
                        completionClass = SubClassTag.Global; // reset sub-class
                        if (functionIndex >= 0)
                        {
                            // Each sampler argument learns the filter and repeat it is called with.

                            FunctionNode? callFunction = shader.Vfunctions[functionIndex].Function;
                            if (callFunction != null)
                            {
                                func.ReturnCache = callFunction.GetDatatype();
                                func.StructName = callFunction.GetDatatypeName();
                                func.ReturnArraySize = callFunction.GetArraySize();

                                //get current base function
                                FunctionNode? baseFunction = null;
                                {
                                    BlockNode? b = pBlock;

                                    while (b != null)
                                    {
                                        if (b.ParentFunction != null)
                                        {
                                            baseFunction = b.ParentFunction;
                                            break;
                                        }
                                        else
                                        {
                                            b = b.ParentBlock;
                                        }
                                    }
                                }

                                if (baseFunction == null)
                                {
                                    return null;
                                } // Bug, wtf.

                                for (int i = 0; i < callFunction.Arguments.Count; i++)
                                {
                                    int argidx = i + 1;
                                    if (argidx < argCount)
                                    {
                                        bool error = false;
                                        Node n = func.Arguments[argidx];
                                        ArgumentQualifier argQual = callFunction.Arguments[i].Qualifier;
                                        bool isOutArg = argQual != ArgumentQualifier.In;

                                        if (n.Type == NodeType.Variable || n.Type == NodeType.Array)
                                        {
                                            string varname = "";

                                            if (n.Type == NodeType.Variable)
                                            {
                                                VariableNode vn = ((VariableNode)n);
                                                varname = vn.Name;
                                            }
                                            else
                                            { // DataType.Array
                                                ArrayNode an = ((ArrayNode)n);
                                                varname = an.Name;
                                            }

                                            if (shader.Varyings.ContainsKey(varname))
                                            {
                                                switch (shader.Varyings[varname].Stage)
                                                {
                                                    case VaryingStage.Unknown:
                                                        if (isOutArg)
                                                        {
                                                            error = true;
                                                        }
                                                        break;
                                                    case VaryingStage.Vertex:
                                                        if (isOutArg && currentFunction != varyingFunctionNames.Vertex)
                                                        { // inout/out
                                                            error = true;
                                                        }
                                                        break;
                                                    case VaryingStage.Fragment:
                                                        if (!isOutArg)
                                                        {
                                                            if (currentFunction != varyingFunctionNames.Fragment && currentFunction != varyingFunctionNames.Light)
                                                            {
                                                                error = true;
                                                            }
                                                        }
                                                        else if (currentFunction != varyingFunctionNames.Fragment)
                                                        { // inout/out
                                                            error = true;
                                                        }
                                                        break;
                                                    default:
                                                        break;
                                                }

                                                if (error)
                                                {
                                                    SetError(Format(("Varying '%s' cannot be passed for the '%s' parameter in that context."), varname, GetQualifierStr(argQual)));
                                                    return null;
                                                }
                                            }
                                        }

                                        bool isConstArg = callFunction.Arguments[i].IsConst;

                                        if (isConstArg || isOutArg)
                                        {
                                            string varname = "";

                                            if (n.Type == NodeType.Constant || n.Type == NodeType.Operator || n.Type == NodeType.ArrayConstruct)
                                            {
                                                if (!isConstArg)
                                                {
                                                    error = true;
                                                }
                                            }
                                            else if (n.Type == NodeType.Array)
                                            {
                                                ArrayNode an = ((ArrayNode)n);
                                                if (!isConstArg && (an.CallExpression != null || an.IsConst))
                                                {
                                                    error = true;
                                                }
                                                varname = an.Name;
                                            }
                                            else if (n.Type == NodeType.Variable)
                                            {
                                                VariableNode vn = ((VariableNode)n);
                                                if (vn.IsConst && !isConstArg)
                                                {
                                                    error = true;
                                                }
                                                varname = vn.Name;
                                            }
                                            else if (n.Type == NodeType.Member)
                                            {
                                                MemberNode mn = ((MemberNode)n);
                                                if (mn.BasetypeConst && isOutArg)
                                                {
                                                    error = true;
                                                }
                                            }
                                            if (!error && varname != "")
                                            {
                                                if (shader.Constants.ContainsKey(varname))
                                                {
                                                    error = true;
                                                }
                                                else if (shader.Uniforms.ContainsKey(varname))
                                                {
                                                    error = true;
                                                }
                                                else if (pFunctionInfo.BuiltIns.ContainsKey(varname))
                                                {
                                                    BuiltInInfo info = pFunctionInfo.BuiltIns[varname];
                                                    if (info.Constant)
                                                    {
                                                        error = true;
                                                    }
                                                }
                                            }

                                            if (error)
                                            {
                                                SetError(Format(("A constant value cannot be passed for the '%s' parameter."), GetQualifierStr(argQual)));
                                                return null;
                                            }
                                        }
                                        if (IsSamplerType(callFunction.Arguments[i].Type))
                                        {
                                            // Let's see where our argument comes from.
                                            string varname = "";
                                            if (n.Type == NodeType.Variable)
                                            {
                                                VariableNode vn = ((VariableNode)n);
                                                varname = vn.Name;
                                            }
                                            else if (n.Type == NodeType.Array)
                                            {
                                                ArrayNode an = ((ArrayNode)n);
                                                varname = an.Name;
                                            }

                                            if (shader.Uniforms.ContainsKey(varname))
                                            {
                                                //being sampler, this either comes from a uniform
                                                Uniform u = shader.Uniforms[varname];
                                                if (u.Type != callFunction.Arguments[i].Type)
                                                {
                                                    continue;
                                                } //this should have been validated previously

                                                if (compileInfo.XrEnabled && isCustomFunc)
                                                {
                                                    Hint hint = u.Hint;

                                                    if (hint == Hint.DepthTexture || hint == Hint.ScreenTexture || hint == Hint.NormalRoughnessTexture)
                                                    {
                                                        SetError(Format(("Unable to pass a multiview texture sampler as a parameter to a custom function. Consider sampling it in the main function and then passing the vector result to the custom function."), GetUniformHintName(hint)));
                                                        return null;
                                                    }
                                                }

                                                //propagate
                                                if (!PropagateFunctionCallSamplerUniformSettings(name, i, u.Filter, u.Repeat, u.Hint))
                                                {
                                                    return null;
                                                }
                                            }
                                            else if (pFunctionInfo.BuiltIns.ContainsKey(varname))
                                            {
                                                if (isCustomFunc && varname == "RADIANCE")
                                                {
                                                    SetError(("Unable to pass RADIANCE texture sampler as a parameter to a custom function. Consider sampling it in the main function and then passing the vector result to the custom function."));
                                                    return null;
                                                }

                                                //a built-in
                                                if (!PropagateFunctionCallSamplerBuiltinReference(name, i, varname))
                                                {
                                                    return null;
                                                }
                                            }
                                            else
                                            {
                                                //or this comes from an argument, but nothing else can be a sampler
                                                bool found = false;
                                                for (int j = 0; j < baseFunction.Arguments.Count; j++)
                                                {
                                                    if (baseFunction.Arguments[j].Name == varname)
                                                    {
                                                        if (!baseFunction.Arguments[j].TexArgumentConnect.ContainsKey(callFunction.Name))
                                                        {
                                                            baseFunction.Arguments[j].TexArgumentConnect[callFunction.Name] = [];
                                                        }
                                                        baseFunction.Arguments[j].TexArgumentConnect[callFunction.Name].Add(i);
                                                        found = true;
                                                        break;
                                                    }
                                                }
                                                if (!found)
                                                {
                                                    continue;
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                        expr = func;
                        if (checkWarnings && isCustomFunc)
                        {
                            string funcName = "";

                            if (pBlock != null && pBlock.ParentFunction != null)
                            {
                                funcName = pBlock.ParentFunction.Name;
                            }

                            ParseUsedIdentifier(name, IdentifierType.Function, funcName);
                        }
                    }
                }
                else
                {
                    //an identifier

                    lastName = identifier;
                    lastType = IdentifierType.Max;
                    SetTkPos(pos);

                    DataType dataType = DataType.Max;
                    IdentifierType identType = IdentifierType.Max;
                    int arraySize = 0;
                    string structName = "";
                    bool isLocal = false;

                    if (pBlock != null && pBlock.BlockTag != SubClassTag.Global)
                    {
                        int idx = 0;
                        bool found = false;

                        while (builtinFuncDefs[idx].Name != null)
                        {
                            if (builtinFuncDefs[idx].Tag == pBlock.BlockTag && builtinFuncDefs[idx].Name == identifier)
                            {
                                found = true;
                                break;
                            }
                            idx++;
                        }
                        if (!found)
                        {
                            SetError(Format(("Unknown identifier in expression: '%s'."), (identifier)));
                            return null;
                        }
                    }
                    else
                    {
                        IdentifierFound found = new();
                        bool known = FindIdentifier(pBlock, false, pFunctionInfo, identifier, found);
                        if (known)
                        {
                            dataType = found.DataType;
                            identType = found.Type;
                            isConst = found.IsConst;
                            arraySize = found.ArraySize;
                            structName = found.StructName;
                        }
                        if (!known)
                        {
                            if (identifier == "SCREEN_TEXTURE" || identifier == "DEPTH_TEXTURE" || identifier == "NORMAL_ROUGHNESS_TEXTURE")
                            {
                                string name = (identifier);
                                string nameLower = name.ToLower();
                                SetError(Format(("%s has been removed in favor of using hint_%s with a uniform.\nTo continue with minimal code changes add 'uniform sampler2D %s : hint_%s, filter_linear_mipmap;' near the top of your shader."), name, nameLower, name, nameLower));
                                return null;
                            }
                            SetError(Format(("Unknown identifier in expression: '%s'."), (identifier)));
                            return null;
                        }
                        if (isConstDecl && !isConst)
                        {
                            SetError(("Expected constant expression."));
                            return null;
                        }
                        if (identType == IdentifierType.Function)
                        {
                            SetError(Format(("Can't use function as identifier: '%s'."), (identifier)));
                            return null;
                        }
                        if (checkWarnings)
                        {
                            string funcName = "";
                            BlockNode? b = pBlock;

                            while (b != null)
                            {
                                if (b.ParentFunction != null)
                                {
                                    funcName = b.ParentFunction.Name;
                                    break;
                                }
                                else
                                {
                                    b = b.ParentBlock;
                                }
                            }

                            ParseUsedIdentifier(identifier, identType, funcName);
                        }
                        if (identType == IdentifierType.Varying)
                        {
                            TkPos prevPos = GetTkPos();
                            Token nextToken = GetToken();

                            // An array of varyings.
                            if (nextToken.Type == Tk.BracketOpen)
                            {
                                GetToken(); // Pass constant.
                                GetToken(); // Pass Tk.BracketClose.
                                nextToken = GetToken();
                            }
                            SetTkPos(prevPos);

                            Varying variable = shader.Varyings[identifier];
                            callsInfo[currentFunction].UsesRestrictedItems.Add((identifier, new CallItem(CallItemType.Varying, prevPos)));

                            string error = "";
                            if (IsTokenOperatorAssign(nextToken.Type))
                            {
                                if (!ValidateVaryingAssign(shader.Varyings[identifier], ref error))
                                {
                                    SetError(error);
                                    return null;
                                }
                            }
                            else
                            {
                                if (variable.Stage == VaryingStage.Unknown && variable.Type < DataType.Int)
                                {
                                    if (currentFunction == varyingFunctionNames.Vertex)
                                    {
                                        SetError(Format(("Varying with '%s' data type may only be used in the '%s' function."), GetDatatypeName(variable.Type), "fragment"));
                                    }
                                    else
                                    {
                                        SetError(Format(("Varying '%s' must be assigned in the '%s' function first."), identifier, "fragment"));
                                    }
                                    return null;
                                }
                            }
                        }
                        if (checkPositionWrite && identType == IdentifierType.BuiltinVar)
                        {
                            if ((identifier) == "POSITION")
                            {
                                // Check if the user wrote "POSITION = vec4(VERTEX," and warn if they did.
                                TkPos prevPos = GetTkPos();
                                if (GetToken().Type == Tk.OpAssign &&
                                        GetToken().Type == Tk.TypeVec4 &&
                                        GetToken().Type == Tk.ParenthesisOpen &&
                                        GetToken().Text == "VERTEX" &&
                                        GetToken().Type == Tk.Comma)
                                {
                                    AddLineWarning(GodotShaderWarningCode.MagicPositionWrite);
                                }

                                // Reset the position so compiling can continue as normal.
                                SetTkPos(prevPos);
                            }
                        }
                        if (isConst)
                        {
                            lastType = IdentifierType.Constant;
                        }
                        else
                        {
                            lastType = identType;
                        }

                        isLocal = identType == IdentifierType.LocalVar || identType == IdentifierType.FunctionArgument;
                    }

                    Node? indexExpression = null;
                    Node? callExpression = null;
                    Node? assignExpression = null;

                    if (arraySize > 0)
                    {
                        prepos = GetTkPos();
                        tk = GetToken();

                        if (tk.Type == Tk.OpAssign)
                        {
                            if (isConst)
                            {
                                SetError(("Constants cannot be modified."));
                                return null;
                            }
                            assignExpression = ParseArrayConstructor(pBlock, pFunctionInfo, dataType, structName, arraySize);
                            if (assignExpression == null)
                            {
                                return null;
                            }
                        }
                        else if (tk.Type == Tk.Period)
                        {
                            completionClass = SubClassTag.Array;
                            if (pBlock != null)
                            {
                                pBlock.BlockTag = SubClassTag.Array;
                            }
                            callExpression = ParseAndReduceExpression(pBlock, pFunctionInfo);
                            if (pBlock != null)
                            {
                                pBlock.BlockTag = SubClassTag.Global;
                            }
                            if (callExpression == null)
                            {
                                return null;
                            }
                        }
                        else if (tk.Type == Tk.BracketOpen)
                        { // indexing
                            indexExpression = ParseAndReduceExpression(pBlock, pFunctionInfo);
                            if (indexExpression == null)
                            {
                                return null;
                            }

                            if (indexExpression.GetArraySize() != 0 || (indexExpression.GetDatatype() != DataType.Int && indexExpression.GetDatatype() != DataType.Uint))
                            {
                                SetError(("Only integer expressions are allowed for indexing."));
                                return null;
                            }

                            if (indexExpression.Type == NodeType.Constant)
                            {
                                ConstantNode cnode = ((ConstantNode)indexExpression);
                                if (cnode != null)
                                {
                                    if (cnode.Values.Count > 0)
                                    {
                                        int value = cnode.Values[0].Sint;
                                        if (value < 0 || value >= arraySize)
                                        {
                                            SetError(Format(("Index [%d] out of range [%d..%d]."), value, 0, arraySize - 1));
                                            return null;
                                        }
                                    }
                                }
                            }

                            tk = GetToken();
                            if (tk.Type != Tk.BracketClose)
                            {
                                SetExpectedError("]");
                                return null;
                            }
                        }
                        else
                        {
                            SetTkPos(prepos);
                        }

                        ArrayNode arrname = new ArrayNode();
                        arrname.Name = identifier;
                        arrname.DatatypeCache = dataType;
                        arrname.StructName = structName;
                        arrname.IndexExpression = indexExpression;
                        arrname.CallExpression = callExpression;
                        arrname.AssignExpression = assignExpression;
                        arrname.IsConst = isConst;
                        arrname.ArraySize = arraySize;
                        arrname.IsLocal = isLocal;
                        expr = arrname;
                    }
                    else
                    {
                        VariableNode varname = new VariableNode();
                        varname.Name = identifier;
                        varname.DatatypeCache = dataType;
                        varname.IsConst = isConst;
                        varname.StructName = structName;
                        varname.IsLocal = isLocal;
                        expr = varname;
                    }
                }
            }
            else if (tk.Type == Tk.OpAdd)
            {
                continue; //this one does nothing
            }
            else if (tk.Type == Tk.OpSub || tk.Type == Tk.OpNot || tk.Type == Tk.OpBitInvert || tk.Type == Tk.OpIncrement || tk.Type == Tk.OpDecrement)
            {
                Expression unary = new();
                unary.IsOp = true;

                switch (tk.Type)
                {
                    case Tk.OpSub:
                        unary.Op = Operator.Negate;
                        break;
                    case Tk.OpNot:
                        unary.Op = Operator.Not;
                        break;
                    case Tk.OpBitInvert:
                        unary.Op = Operator.BitInvert;
                        break;
                    case Tk.OpIncrement:
                        unary.Op = Operator.Increment;
                        break;
                    case Tk.OpDecrement:
                        unary.Op = Operator.Decrement;
                        break;
                    default:
                        return null;
                }

                expression.Add(unary);
                continue;
            }
            else
            {
                bool valid = false;
                if (pBlock != null && pBlock.BlockType == BlockType.ForExpression && tk.Type == Tk.ParenthesisClose)
                {
                    valid = true;
                    SetTkPos(prepos);

                    OperatorNode func = new OperatorNode();
                    func.Op = Operator.Empty;
                    expr = func;
                }
                if (!valid)
                {
                    if (tk.Type != Tk.Semicolon)
                    {
                        SetError(Format(("Expected expression, found: '%s'."), GetTokenText(tk)));
                        return null;
                    }
                    else
                    {
                        if (pBlock == null || (pBlock.BlockType != BlockType.ForInit && pBlock.BlockType != BlockType.ForCondition))
                        {
                            if (checkWarnings && HasWarning(GodotShaderWarningFlags.FormattingError))
                            {
                                AddLineWarning(GodotShaderWarningCode.FormattingError, ("Empty statement. Remove ';' to fix this warning."));
                            }
                        }
                        SetTkPos(prepos);

                        OperatorNode func = new OperatorNode();
                        func.Op = Operator.Empty;
                        expr = func;
                    }
                }
            }

            if (expr == null)
            {
                return null;
            }

            /* Err.Ok now see what's NEXT to the operator.. */

            while (true)
            {
                TkPos pos2 = GetTkPos();
                tk = GetToken();

                if (tk.Type == Tk.Cursor)
                {
                    //do nothing
                }
                else if (tk.Type == Tk.Period)
                {

                    DataType dt = expr.GetDatatype();
                    string st = expr.GetDatatypeName();

                    if (!expr.IsIndexed() && expr.GetArraySize() > 0)
                    {
                        completionClass = SubClassTag.Array;
                        if (pBlock != null)
                        {
                            pBlock.BlockTag = SubClassTag.Array;
                        }
                        Node? callExpression = ParseAndReduceExpression(pBlock, pFunctionInfo);
                        if (pBlock != null)
                        {
                            pBlock.BlockTag = SubClassTag.Global;
                        }
                        if (callExpression == null)
                        {
                            return null;
                        }
                        expr = callExpression;
                        break;
                    }

                    string identifier = "";
                    if (GetCompletableIdentifier(pBlock, dt == DataType.Struct ? CompletionType.Struct : CompletionType.Index, out identifier))
                    {
                        if (dt == DataType.Struct)
                        {
                            completionStruct = st;
                        }
                        else
                        {
                            completionBase = dt;
                        }
                    }

                    if (identifier == "")
                    {
                        SetError(("Expected an identifier as a member."));
                        return null;
                    }
                    string ident = identifier;

                    bool ok = true;
                    bool repeated = false;
                    DataType memberType = DataType.Void;
                    string memberStructName = "";
                    int arraySize = 0;

                    SortedSet<char> positionSymbols = [];
                    SortedSet<char> colorSymbols = [];
                    SortedSet<char> textureSymbols = [];

                    bool mixError = false;

                    switch (dt)
                    {
                        case DataType.Struct:
                            {
                                ok = false;
                                string memberName = ident;
                                if (shader.Structs.ContainsKey(st))
                                {
                                    StructNode? n = shader.Structs[st].ShaderStruct;
                                    foreach (MemberNode E in n.Members)
                                    {
                                        if ((E.Name) == memberName)
                                        {
                                            memberType = E.Datatype;
                                            arraySize = E.ArraySize;
                                            if (memberType == DataType.Struct)
                                            {
                                                memberStructName = E.StructName;
                                            }
                                            ok = true;
                                            break;
                                        }
                                    }
                                }

                            }
                            break;
                        case DataType.Bvec2:
                        case DataType.Ivec2:
                        case DataType.Uvec2:
                        case DataType.Vec2:
                            {
                                int l = ident.Length;
                                if (l == 1)
                                {
                                    memberType = (dt - 1);
                                }
                                else if (l == 2)
                                {
                                    memberType = dt;
                                }
                                else if (l == 3)
                                {
                                    memberType = (dt + 1);
                                }
                                else if (l == 4)
                                {
                                    memberType = (dt + 2);
                                }
                                else
                                {
                                    ok = false;
                                    break;
                                }

                                string c = ident;
                                for (int i = 0; i < l; i++)
                                {
                                    switch (c[i])
                                    {
                                        case 'r':
                                        case 'g':
                                            if (positionSymbols.Count > 0 || textureSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!colorSymbols.Contains(c[i]))
                                            {
                                                colorSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        case 'x':
                                        case 'y':
                                            if (colorSymbols.Count > 0 || textureSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!positionSymbols.Contains(c[i]))
                                            {
                                                positionSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        case 's':
                                        case 't':
                                            if (colorSymbols.Count > 0 || positionSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!textureSymbols.Contains(c[i]))
                                            {
                                                textureSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        default:
                                            ok = false;
                                            break;
                                    }
                                }

                            }
                            break;
                        case DataType.Bvec3:
                        case DataType.Ivec3:
                        case DataType.Uvec3:
                        case DataType.Vec3:
                            {
                                int l = ident.Length;
                                if (l == 1)
                                {
                                    memberType = (dt - 2);
                                }
                                else if (l == 2)
                                {
                                    memberType = (dt - 1);
                                }
                                else if (l == 3)
                                {
                                    memberType = dt;
                                }
                                else if (l == 4)
                                {
                                    memberType = (dt + 1);
                                }
                                else
                                {
                                    ok = false;
                                    break;
                                }

                                string c = ident;
                                for (int i = 0; i < l; i++)
                                {
                                    switch (c[i])
                                    {
                                        case 'r':
                                        case 'g':
                                        case 'b':
                                            if (positionSymbols.Count > 0 || textureSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!colorSymbols.Contains(c[i]))
                                            {
                                                colorSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        case 'x':
                                        case 'y':
                                        case 'z':
                                            if (colorSymbols.Count > 0 || textureSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!positionSymbols.Contains(c[i]))
                                            {
                                                positionSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        case 's':
                                        case 't':
                                        case 'p':
                                            if (colorSymbols.Count > 0 || positionSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!textureSymbols.Contains(c[i]))
                                            {
                                                textureSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        default:
                                            ok = false;
                                            break;
                                    }
                                }

                            }
                            break;
                        case DataType.Bvec4:
                        case DataType.Ivec4:
                        case DataType.Uvec4:
                        case DataType.Vec4:
                            {
                                int l = ident.Length;
                                if (l == 1)
                                {
                                    memberType = (dt - 3);
                                }
                                else if (l == 2)
                                {
                                    memberType = (dt - 2);
                                }
                                else if (l == 3)
                                {
                                    memberType = (dt - 1);
                                }
                                else if (l == 4)
                                {
                                    memberType = dt;
                                }
                                else
                                {
                                    ok = false;
                                    break;
                                }

                                string c = ident;
                                for (int i = 0; i < l; i++)
                                {
                                    switch (c[i])
                                    {
                                        case 'r':
                                        case 'g':
                                        case 'b':
                                        case 'a':
                                            if (positionSymbols.Count > 0 || textureSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!colorSymbols.Contains(c[i]))
                                            {
                                                colorSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        case 'x':
                                        case 'y':
                                        case 'z':
                                        case 'w':
                                            if (colorSymbols.Count > 0 || textureSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!positionSymbols.Contains(c[i]))
                                            {
                                                positionSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        case 's':
                                        case 't':
                                        case 'p':
                                        case 'q':
                                            if (colorSymbols.Count > 0 || positionSymbols.Count > 0)
                                            {
                                                mixError = true;
                                                break;
                                            }
                                            if (!textureSymbols.Contains(c[i]))
                                            {
                                                textureSymbols.Add(c[i]);
                                            }
                                            else
                                            {
                                                repeated = true;
                                            }
                                            break;
                                        default:
                                            ok = false;
                                            break;
                                    }
                                }

                            }
                            break;

                        default:
                            {
                                ok = false;
                            }
                            break;
                    }

                    if (mixError)
                    {
                        SetError(Format(("Cannot combine symbols from different sets in expression '.%s'."), ident));
                        return null;
                    }

                    if (!ok)
                    {
                        SetError(Format(("Invalid member for '%s' expression: '.%s'."), (dt == DataType.Struct ? st : GetDatatypeName(dt)), ident));
                        return null;
                    }

                    MemberNode mn = new MemberNode { Owner = expr };
                    mn.Basetype = dt;
                    mn.BasetypeConst = isConst;
                    mn.Datatype = memberType;
                    mn.BaseStructName = st;
                    mn.StructName = memberStructName;
                    mn.ArraySize = arraySize;
                    mn.Name = ident;
                    mn.HasSwizzlingDuplicates = repeated;

                    if (arraySize > 0)
                    {
                        TkPos prevPos = GetTkPos();
                        tk = GetToken();
                        if (tk.Type == Tk.OpAssign)
                        {
                            if (lastType == IdentifierType.Constant)
                            {
                                SetError(("Constants cannot be modified."));
                                return null;
                            }
                            Node? assignExpression = ParseArrayConstructor(pBlock, pFunctionInfo, memberType, memberStructName, arraySize);
                            if (assignExpression == null)
                            {
                                return null;
                            }
                            mn.AssignExpression = assignExpression;
                        }
                        else if (tk.Type == Tk.Period)
                        {
                            completionClass = SubClassTag.Array;
                            if (pBlock != null)
                            {
                                pBlock.BlockTag = SubClassTag.Array;
                            }
                            mn.CallExpression = ParseAndReduceExpression(pBlock, pFunctionInfo);
                            if (pBlock != null)
                            {
                                pBlock.BlockTag = SubClassTag.Global;
                            }
                            if (mn.CallExpression == null)
                            {
                                return null;
                            }
                        }
                        else if (tk.Type == Tk.BracketOpen)
                        {
                            Node? indexExpression = ParseAndReduceExpression(pBlock, pFunctionInfo);
                            if (indexExpression == null)
                            {
                                return null;
                            }

                            if (indexExpression.GetArraySize() != 0 || (indexExpression.GetDatatype() != DataType.Int && indexExpression.GetDatatype() != DataType.Uint))
                            {
                                SetError(("Only integer expressions are allowed for indexing."));
                                return null;
                            }

                            if (indexExpression.Type == NodeType.Constant)
                            {
                                ConstantNode cnode = ((ConstantNode)indexExpression);
                                if (cnode != null)
                                {
                                    if (cnode.Values.Count > 0)
                                    {
                                        int value = cnode.Values[0].Sint;
                                        if (value < 0 || value >= arraySize)
                                        {
                                            SetError(Format(("Index [%d] out of range [%d..%d]."), value, 0, arraySize - 1));
                                            return null;
                                        }
                                    }
                                }
                            }

                            tk = GetToken();
                            if (tk.Type != Tk.BracketClose)
                            {
                                SetExpectedError("]");
                                return null;
                            }
                            mn.IndexExpression = indexExpression;
                        }
                        else
                        {
                            SetTkPos(prevPos);
                        }
                    }
                    expr = mn;


                    // A member (period) has priority over any operator, its expression made in place.
                }
                else if (tk.Type == Tk.BracketOpen)
                {
                    Node? index = ParseAndReduceExpression(pBlock, pFunctionInfo);
                    if (index == null)
                    {
                        return null;
                    }

                    if (index.GetArraySize() != 0 || (index.GetDatatype() != DataType.Int && index.GetDatatype() != DataType.Uint))
                    {
                        SetError(("Only integer expressions are allowed for indexing."));
                        return null;
                    }

                    DataType memberType = DataType.Void;
                    string memberStructName = "";

                    if (expr.GetArraySize() > 0)
                    {
                        if (index.Type == NodeType.Constant)
                        {
                            uint indexConstant = ((ConstantNode)index).Values[0].Uint;
                            if (indexConstant >= (uint)expr.GetArraySize())
                            {
                                SetError(Format(("Index [%d] out of range [%d..%d]."), indexConstant, 0, expr.GetArraySize() - 1));
                                return null;
                            }
                        }
                        memberType = expr.GetDatatype();
                        if (memberType == DataType.Struct)
                        {
                            memberStructName = expr.GetDatatypeName();
                        }
                    }
                    else
                    {
                        switch (expr.GetDatatype())
                        {
                            case DataType.Bvec2:
                            case DataType.Vec2:
                            case DataType.Ivec2:
                            case DataType.Uvec2:
                            case DataType.Mat2:
                                if (index.Type == NodeType.Constant)
                                {
                                    uint indexConstant = ((ConstantNode)index).Values[0].Uint;
                                    if (indexConstant >= 2)
                                    {
                                        SetError(Format(("Index [%d] out of range [%d..%d]."), indexConstant, 0, 1));
                                        return null;
                                    }
                                }

                                switch (expr.GetDatatype())
                                {
                                    case DataType.Bvec2:
                                        memberType = DataType.Bool;
                                        break;
                                    case DataType.Vec2:
                                        memberType = DataType.Float;
                                        break;
                                    case DataType.Ivec2:
                                        memberType = DataType.Int;
                                        break;
                                    case DataType.Uvec2:
                                        memberType = DataType.Uint;
                                        break;
                                    case DataType.Mat2:
                                        memberType = DataType.Vec2;
                                        break;
                                    default:
                                        break;
                                }

                                break;
                            case DataType.Bvec3:
                            case DataType.Vec3:
                            case DataType.Ivec3:
                            case DataType.Uvec3:
                            case DataType.Mat3:
                                if (index.Type == NodeType.Constant)
                                {
                                    uint indexConstant = ((ConstantNode)index).Values[0].Uint;
                                    if (indexConstant >= 3)
                                    {
                                        SetError(Format(("Index [%d] out of range [%d..%d]."), indexConstant, 0, 2));
                                        return null;
                                    }
                                }

                                switch (expr.GetDatatype())
                                {
                                    case DataType.Bvec3:
                                        memberType = DataType.Bool;
                                        break;
                                    case DataType.Vec3:
                                        memberType = DataType.Float;
                                        break;
                                    case DataType.Ivec3:
                                        memberType = DataType.Int;
                                        break;
                                    case DataType.Uvec3:
                                        memberType = DataType.Uint;
                                        break;
                                    case DataType.Mat3:
                                        memberType = DataType.Vec3;
                                        break;
                                    default:
                                        break;
                                }
                                break;
                            case DataType.Bvec4:
                            case DataType.Vec4:
                            case DataType.Ivec4:
                            case DataType.Uvec4:
                            case DataType.Mat4:
                                if (index.Type == NodeType.Constant)
                                {
                                    uint indexConstant = ((ConstantNode)index).Values[0].Uint;
                                    if (indexConstant >= 4)
                                    {
                                        SetError(Format(("Index [%d] out of range [%d..%d]."), indexConstant, 0, 3));
                                        return null;
                                    }
                                }

                                switch (expr.GetDatatype())
                                {
                                    case DataType.Bvec4:
                                        memberType = DataType.Bool;
                                        break;
                                    case DataType.Vec4:
                                        memberType = DataType.Float;
                                        break;
                                    case DataType.Ivec4:
                                        memberType = DataType.Int;
                                        break;
                                    case DataType.Uvec4:
                                        memberType = DataType.Uint;
                                        break;
                                    case DataType.Mat4:
                                        memberType = DataType.Vec4;
                                        break;
                                    default:
                                        break;
                                }
                                break;
                            default:
                                {
                                    SetError(Format(("An object of type '%s' can't be indexed."), (expr.GetDatatype() == DataType.Struct ? expr.GetDatatypeName() : GetDatatypeName(expr.GetDatatype()))));
                                    return null;
                                }
                        }
                    }
                    OperatorNode op = new OperatorNode();
                    op.Op = Operator.Index;
                    op.ReturnCache = memberType;
                    op.StructName = memberStructName;
                    op.Arguments.Add(expr);
                    op.Arguments.Add(index);
                    expr = op;

                    tk = GetToken();
                    if (tk.Type != Tk.BracketClose)
                    {
                        SetExpectedError("]");
                        return null;
                    }

                }
                else if (tk.Type == Tk.OpIncrement || tk.Type == Tk.OpDecrement)
                {
                    OperatorNode op = new OperatorNode();
                    op.Op = tk.Type == Tk.OpDecrement ? Operator.PostDecrement : Operator.PostIncrement;
                    op.Arguments.Add(expr);

                    if (!ValidateOperator(pBlock, pFunctionInfo, op))
                    {
                        SetError(("Invalid base type for increment/decrement operator."));
                        return null;
                    }

                    string error = "";
                    if (!ValidateAssign(expr, pFunctionInfo, ref error))
                    {
                        SetError(error);
                        return null;
                    }
                    expr = op;
                }
                else
                {
                    SetTkPos(pos2);
                    break;
                }
            }

            Expression e = new();
            e.IsOp = false;
            e.Node = expr;
            expression.Add(e);

            pos = GetTkPos();
            tk = GetToken();

            if (pPreviousExpressionInfo != null && tk.Type == pPreviousExpressionInfo.TtBreak && !pPreviousExpressionInfo.IsLastExpr)
            {
                break;
            }

            if (IsTokenOperator(tk.Type))
            {
                Expression o = new();
                o.IsOp = true;

                switch (tk.Type)
                {
                    case Tk.OpEqual:
                        o.Op = Operator.Equal;
                        break;
                    case Tk.OpNotEqual:
                        o.Op = Operator.NotEqual;
                        break;
                    case Tk.OpLess:
                        o.Op = Operator.Less;
                        break;
                    case Tk.OpLessEqual:
                        o.Op = Operator.LessEqual;
                        break;
                    case Tk.OpGreater:
                        o.Op = Operator.Greater;
                        break;
                    case Tk.OpGreaterEqual:
                        o.Op = Operator.GreaterEqual;
                        break;
                    case Tk.OpAnd:
                        o.Op = Operator.And;
                        break;
                    case Tk.OpOr:
                        o.Op = Operator.Or;
                        break;
                    case Tk.OpAdd:
                        o.Op = Operator.Add;
                        break;
                    case Tk.OpSub:
                        o.Op = Operator.Sub;
                        break;
                    case Tk.OpMul:
                        o.Op = Operator.Mul;
                        break;
                    case Tk.OpDiv:
                        o.Op = Operator.Div;
                        break;
                    case Tk.OpMod:
                        o.Op = Operator.Mod;
                        break;
                    case Tk.OpShiftLeft:
                        o.Op = Operator.ShiftLeft;
                        break;
                    case Tk.OpShiftRight:
                        o.Op = Operator.ShiftRight;
                        break;
                    case Tk.OpAssign:
                        o.Op = Operator.Assign;
                        break;
                    case Tk.OpAssignAdd:
                        o.Op = Operator.AssignAdd;
                        break;
                    case Tk.OpAssignSub:
                        o.Op = Operator.AssignSub;
                        break;
                    case Tk.OpAssignMul:
                        o.Op = Operator.AssignMul;
                        break;
                    case Tk.OpAssignDiv:
                        o.Op = Operator.AssignDiv;
                        break;
                    case Tk.OpAssignMod:
                        o.Op = Operator.AssignMod;
                        break;
                    case Tk.OpAssignShiftLeft:
                        o.Op = Operator.AssignShiftLeft;
                        break;
                    case Tk.OpAssignShiftRight:
                        o.Op = Operator.AssignShiftRight;
                        break;
                    case Tk.OpAssignBitAnd:
                        o.Op = Operator.AssignBitAnd;
                        break;
                    case Tk.OpAssignBitOr:
                        o.Op = Operator.AssignBitOr;
                        break;
                    case Tk.OpAssignBitXor:
                        o.Op = Operator.AssignBitXor;
                        break;
                    case Tk.OpBitAnd:
                        o.Op = Operator.BitAnd;
                        break;
                    case Tk.OpBitOr:
                        o.Op = Operator.BitOr;
                        break;
                    case Tk.OpBitXor:
                        o.Op = Operator.BitXor;
                        break;
                    case Tk.Question:
                        o.Op = Operator.SelectIf;
                        break;
                    case Tk.Colon:
                        o.Op = Operator.SelectElse;
                        break;
                    default:
                        {
                            SetError(Format(("Invalid token for the operator: '%s'."), GetTokenText(tk)));
                            return null;
                        }
                }

                expression.Add(o);

                if (o.Op == Operator.SelectIf)
                {
                    ExpressionInfo info = new();
                    info.Expression = expression;
                    info.TtBreak = Tk.Colon;

                    expr = ParseAndReduceExpression(pBlock, pFunctionInfo, info);
                    if (expr == null)
                    {
                        return null;
                    }

                    expression.Add(new Expression { IsOp = true, Op = Operator.SelectElse });

                    if (pPreviousExpressionInfo != null)
                    {
                        info.IsLastExpr = pPreviousExpressionInfo.IsLastExpr;
                    }
                    else
                    {
                        info.IsLastExpr = true;
                    }

                    expr = ParseAndReduceExpression(pBlock, pFunctionInfo, info);
                    if (expr == null)
                    {
                        return null;
                    }

                    break;
                }
            }
            else
            {
                SetTkPos(pos); //something else, so rollback and end
                break;
            }
        }

        /* Reduce the set set of expressions and place them in an operator tree, respecting precedence */

        while (expression.Count > 1)
        {
            int nextOp = -1;
            int minPriority = 0xFFFFF;
            bool isUnary = false;
            bool isTernary = false;

            for (int i = 0; i < expression.Count; i++)
            {
                if (!expression[i].IsOp)
                {
                    continue;
                }

                bool unary = false;
                bool ternary = false;
                Operator op = expression[i].Op;

                int priority;
                switch (op)
                {
                    case Operator.Equal:
                        priority = 8;
                        break;
                    case Operator.NotEqual:
                        priority = 8;
                        break;
                    case Operator.Less:
                        priority = 7;
                        break;
                    case Operator.LessEqual:
                        priority = 7;
                        break;
                    case Operator.Greater:
                        priority = 7;
                        break;
                    case Operator.GreaterEqual:
                        priority = 7;
                        break;
                    case Operator.And:
                        priority = 12;
                        break;
                    case Operator.Or:
                        priority = 14;
                        break;
                    case Operator.Not:
                        priority = 3;
                        unary = true;
                        break;
                    case Operator.Negate:
                        priority = 3;
                        unary = true;
                        break;
                    case Operator.Add:
                        priority = 5;
                        break;
                    case Operator.Sub:
                        priority = 5;
                        break;
                    case Operator.Mul:
                        priority = 4;
                        break;
                    case Operator.Div:
                        priority = 4;
                        break;
                    case Operator.Mod:
                        priority = 4;
                        break;
                    case Operator.ShiftLeft:
                        priority = 6;
                        break;
                    case Operator.ShiftRight:
                        priority = 6;
                        break;
                    case Operator.Assign:
                        priority = 16;
                        break;
                    case Operator.AssignAdd:
                        priority = 16;
                        break;
                    case Operator.AssignSub:
                        priority = 16;
                        break;
                    case Operator.AssignMul:
                        priority = 16;
                        break;
                    case Operator.AssignDiv:
                        priority = 16;
                        break;
                    case Operator.AssignMod:
                        priority = 16;
                        break;
                    case Operator.AssignShiftLeft:
                        priority = 16;
                        break;
                    case Operator.AssignShiftRight:
                        priority = 16;
                        break;
                    case Operator.AssignBitAnd:
                        priority = 16;
                        break;
                    case Operator.AssignBitOr:
                        priority = 16;
                        break;
                    case Operator.AssignBitXor:
                        priority = 16;
                        break;
                    case Operator.BitAnd:
                        priority = 9;
                        break;
                    case Operator.BitOr:
                        priority = 11;
                        break;
                    case Operator.BitXor:
                        priority = 10;
                        break;
                    case Operator.BitInvert:
                        priority = 3;
                        unary = true;
                        break;
                    case Operator.Increment:
                        priority = 3;
                        unary = true;
                        break;
                    case Operator.Decrement:
                        priority = 3;
                        unary = true;
                        break;
                    case Operator.SelectIf:
                        priority = 15;
                        ternary = true;
                        break;
                    case Operator.SelectElse:
                        priority = 15;
                        ternary = true;
                        break;

                    default:
                        return null; //unexpected operator
                }

                if (checkWarnings && HasWarning(GodotShaderWarningFlags.FloatComparison) && (op == Operator.Equal || op == Operator.NotEqual) &&
                        (!expression[i - 1].IsOp && !expression[i + 1].IsOp) &&
                        (expression[i - 1].Node!.GetDatatype() == DataType.Float && expression[i + 1].Node!.GetDatatype() == DataType.Float))
                {
                    AddLineWarning(GodotShaderWarningCode.FloatComparison);
                }

                if (priority < minPriority)
                {
                    // < reads left to right (the default), <= right to left.
                    nextOp = i;
                    minPriority = priority;
                    isUnary = unary;
                    isTernary = ternary;
                }
            }

            if (nextOp == -1)
            {
                return null;
            }

            // Err.Ok! create operator..
            if (isUnary)
            {
                int exprPos = nextOp;
                while (expression[exprPos].IsOp)
                {
                    exprPos++;
                    if (exprPos == expression.Count)
                    {
                        //can happen..
                        SetError(("Unexpected end of expression."));
                        return null;
                    }
                }

                //consecutively do unary operators
                for (int i = exprPos - 1; i >= nextOp; i--)
                {
                    OperatorNode op = new OperatorNode();
                    op.Op = expression[i].Op;

                    string error = "";
                    if ((op.Op == Operator.Increment || op.Op == Operator.Decrement) && !ValidateAssign(expression[i + 1].Node!, pFunctionInfo, ref error))
                    {
                        SetError(error);
                        return null;
                    }
                    op.Arguments.Add(expression[i + 1].Node!);

                    expression[i].IsOp = false;
                    expression[i].Node = op;

                    if (!ValidateOperator(pBlock, pFunctionInfo, op))
                    {
                        if (errorSet)
                        {
                            return null;
                        }

                        string at = "";
                        for (int j = 0; j < op.Arguments.Count; j++)
                        {
                            if (j > 0)
                            {
                                at += ", ";
                            }
                            at += GetDatatypeName(op.Arguments[j].GetDatatype());
                            if (!op.Arguments[j].IsIndexed() && op.Arguments[j].GetArraySize() > 0)
                            {
                                at += "[";
                                at += Itos(op.Arguments[j].GetArraySize());
                                at += "]";
                            }
                        }
                        SetError(Format(("Invalid arguments to unary operator '%s': %s."), GetOperatorText(op.Op), at));
                        return null;
                    }
                    expression[i].Node = ReduceExpression(pBlock, expression[i].Node!);
                    expression.RemoveAt(i + 1);
                }

            }
            else if (isTernary)
            {
                if (nextOp < 1 || nextOp >= (expression.Count - 1))
                {
                    SetParsingError();
                    return null;
                }

                if (nextOp + 2 >= expression.Count || !expression[nextOp + 2].IsOp || expression[nextOp + 2].Op != Operator.SelectElse)
                {
                    SetError(("Missing matching ':' for select operator."));
                    return null;
                }

                OperatorNode op = new OperatorNode();
                op.Op = expression[nextOp].Op;
                op.Arguments.Add(expression[nextOp - 1].Node!);
                op.Arguments.Add(expression[nextOp + 1].Node!);
                op.Arguments.Add(expression[nextOp + 3].Node!);

                expression[nextOp - 1].IsOp = false;
                expression[nextOp - 1].Node = op;
                if (!ValidateOperator(pBlock, pFunctionInfo, op, true))
                {
                    if (errorSet)
                    {
                        return null;
                    }

                    string at = "";
                    for (int i = 0; i < op.Arguments.Count; i++)
                    {
                        if (i > 0)
                        {
                            at += ", ";
                        }
                        DataType dt = op.Arguments[i].GetDatatype();
                        if (dt == DataType.Struct)
                        {
                            at += op.Arguments[i].GetDatatypeName();
                        }
                        else
                        {
                            at += GetDatatypeName(dt);
                        }
                        if (!op.Arguments[i].IsIndexed() && op.Arguments[i].GetArraySize() > 0)
                        {
                            at += "[";
                            at += Itos(op.Arguments[i].GetArraySize());
                            at += "]";
                        }
                    }
                    SetError(Format(("Invalid argument to ternary operator: '%s'."), at));
                    return null;
                }

                for (int i = 0; i < 4; i++)
                {
                    expression.RemoveAt(nextOp);
                }

            }
            else
            {
                if (nextOp < 1 || nextOp >= (expression.Count - 1))
                {
                    SetParsingError();
                    return null;
                }

                OperatorNode op = new OperatorNode();
                op.Op = expression[nextOp].Op;

                if (expression[nextOp - 1].IsOp)
                {
                    SetParsingError();
                    return null;
                }

                if (IsOperatorAssign(op.Op))
                {
                    if (pBlock != null && expression[nextOp - 1].Node!.Type == NodeType.Variable)
                    {
                        VariableNode vn = ((VariableNode)expression[nextOp - 1].Node!);
                        pBlock.UseOpEval = vn.IsConst;
                    }

                    string assignMessage = "";
                    if (!ValidateAssign(expression[nextOp - 1].Node!, pFunctionInfo, ref assignMessage))
                    {
                        SetError(assignMessage);
                        return null;
                    }
                }

                if (expression[nextOp + 1].IsOp)
                {
                    // Valid here, but no binary operator can be followed by a unary one, which goes first by precedence.

                    SetParsingError();
                }

                op.Arguments.Add(expression[nextOp - 1].Node!); //expression goes as left
                op.Arguments.Add(expression[nextOp + 1].Node!); //next expression goes as right
                expression[nextOp - 1].Node = op;

                //replace all 3 nodes by this operator and make it an expression

                if (!ValidateOperator(pBlock, pFunctionInfo, op))
                {
                    if (errorSet)
                    {
                        return null;
                    }

                    string at = "";
                    for (int i = 0; i < op.Arguments.Count; i++)
                    {
                        if (i > 0)
                        {
                            at += ", ";
                        }
                        if (op.Arguments[i].GetDatatype() == DataType.Struct)
                        {
                            at += op.Arguments[i].GetDatatypeName();
                        }
                        else
                        {
                            at += GetDatatypeName(op.Arguments[i].GetDatatype());
                        }
                        if (!op.Arguments[i].IsIndexed() && op.Arguments[i].GetArraySize() > 0)
                        {
                            at += "[";
                            at += Itos(op.Arguments[i].GetArraySize());
                            at += "]";
                        }
                    }
                    SetError(Format(("Invalid arguments to operator '%s': '%s'."), GetOperatorText(op.Op), at));
                    return null;
                }

                expression.RemoveAt(nextOp);
                expression.RemoveAt(nextOp);
            }
        }

        if (pBlock != null)
        {
            pBlock.UseOpEval = true;
        }

        if (pPreviousExpressionInfo != null)
        {
            pPreviousExpressionInfo.Expression.Add(expression[0]);
        }

        return expression[0].Node!;
    }

    private Node ReduceExpression(BlockNode? pBlock, Node pNode)
    {
        if (pNode.Type != NodeType.Operator)
        {
            return pNode;
        }

        //for now only reduce simple constructors
        OperatorNode op = ((OperatorNode)pNode);

        if (op.Op == Operator.Construct)
        {
            if (op.Arguments[0].Type != NodeType.Variable)
            {
                return pNode;
            }

            DataType type = op.GetDatatype();
            DataType baseType = GetScalarType(type);
            int cardinality = GetCardinality(type);

            List<Scalar> values = [];

            for (int i = 1; i < op.Arguments.Count; i++)
            {
                op.Arguments[i] = ReduceExpression(pBlock, op.Arguments[i]);
                if (op.Arguments[i].Type == NodeType.Constant)
                {
                    ConstantNode argument = ((ConstantNode)op.Arguments[i]);

                    if (GetScalarType(argument.Datatype) == baseType)
                    {
                        for (int j = 0; j < argument.Values.Count; j++)
                        {
                            values.Add(argument.Values[j]);
                        }
                    }
                    else if (GetScalarType(argument.Datatype) == argument.Datatype)
                    {
                        List<Scalar> v = [default];
                        if (!ConvertConstant(argument, baseType, v))
                        {
                            return pNode;
                        }
                        values.Add(v[0]);
                    }
                    else
                    {
                        return pNode;
                    }

                }
                else
                {
                    return pNode;
                }
            }

            if (values.Count == 1)
            {
                if (type >= DataType.Mat2 && type <= DataType.Mat4)
                {
                    Scalar value = values[0];
                    Scalar zero = default;
                    zero.Real = 0.0f;
                    int size = 2 + (type - DataType.Mat2);

                    values.Clear();
                    for (int i = 0; i < size; i++)
                    {
                        for (int j = 0; j < size; j++)
                        {
                            values.Add(i == j ? value : zero);
                        }
                    }
                }
                else
                {
                    Scalar value = values[0];
                    for (int i = 1; i < cardinality; i++)
                    {
                        values.Add(value);
                    }
                }
            }
            else if (values.Count != cardinality)
            {
                // "Failed to reduce expression, values and cardinality mismatch."
                return pNode;
            }

            ConstantNode cn = new ConstantNode();
            cn.Datatype = op.GetDatatype();
            cn.Values = values;
            return cn;
        }
        else if (op.Op == Operator.Negate)
        {
            op.Arguments[0] = ReduceExpression(pBlock, op.Arguments[0]);
            if (op.Arguments[0].Type == NodeType.Constant)
            {
                ConstantNode cn = ((ConstantNode)op.Arguments[0]);

                DataType baseType = GetScalarType(cn.Datatype);

                List<Scalar> values = [];

                for (int i = 0; i < cn.Values.Count; i++)
                {
                    Scalar nv = default;
                    switch (baseType)
                    {
                        case DataType.Bool:
                            {
                                nv.Boolean = !cn.Values[i].Boolean;
                            }
                            break;
                        case DataType.Int:
                            {
                                nv.Sint = -cn.Values[i].Sint;
                            }
                            break;
                        case DataType.Uint:
                            {
                                // Intentionally wrap the unsigned int value, because GLSL does.
                                nv.Uint = 0 - cn.Values[i].Uint;
                            }
                            break;
                        case DataType.Float:
                            {
                                nv.Real = -cn.Values[i].Real;
                            }
                            break;
                        default:
                            {
                            }
                            break;
                    }

                    values.Add(nv);
                }

                cn.Values = values;
                return cn;
            }
        }

        return pNode;
    }

    private Node? ParseAndReduceExpression(BlockNode? pBlock, FunctionInfo pFunctionInfo, ExpressionInfo? pPreviousExpressionInfo = null)
    {
        Node? expr = ParseExpression(pBlock, pFunctionInfo, pPreviousExpressionInfo);
        if (expr == null)
        { //errored
            return null;
        }

        expr = ReduceExpression(pBlock, expr);

        return expr;
    }
}
