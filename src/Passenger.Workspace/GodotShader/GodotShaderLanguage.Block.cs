using System.Runtime.CompilerServices;

namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's reading of a block of statements (shader_language.cpp, MIT): declarations, control flow, return, discard,
/// break and continue, and expressions, each as Godot checks it.</summary>
public sealed partial class GodotShaderLanguage
{
    private Err ParseBlock(BlockNode pBlock, FunctionInfo pFunctionInfo, bool pJustOne = false, bool pCanBreak = false, bool pCanContinue = false)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            SetError(TooDeep);
            return Err.ParseError;
        }
        while (true)
        {
            TkPos pos = GetTkPos();
            Token tk = GetToken();

            if (pBlock.BlockType == BlockType.Switch)
            {
                if (tk.Type != Tk.CfCase && tk.Type != Tk.CfDefault && tk.Type != Tk.CurlyBracketClose)
                {
                    SetError(Format(("A switch may only contain '%s' and '%s' blocks."), "case", "default"));
                    return Err.ParseError;
                }
            }

            bool isStruct = shader.Structs.ContainsKey(tk.Text) && !(FindIdentifier(pBlock, false, pFunctionInfo, tk.Text));
            bool isVarInit = false;
            bool isCondition = false;

            if (tk.Type == Tk.CurlyBracketClose)
            { //end of block
                if (pJustOne)
                {
                    SetExpectedError("}");
                    return Err.ParseError;
                }

                return Err.Ok;

            }
            else if (tk.Type == Tk.Const || IsTokenPrecision(tk.Type) || IsTokenNonvoidDatatype(tk.Type) || isStruct)
            {
                isVarInit = true;

                string structName = "";
                if (isStruct)
                {
                    structName = tk.Text;
                    if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedStruct) && usedStructs.ContainsKey(structName))
                    {
                        usedStructs[structName].Used = true;
                    }
                }

                bool isConst = false;

                if (tk.Type == Tk.Const)
                {
                    isConst = true;
                    tk = GetToken();

                    if (!isStruct)
                    {
                        isStruct = shader.Structs.ContainsKey(tk.Text); // check again.
                        structName = tk.Text;
                    }
                }

                DataPrecision precision = DataPrecision.Default;
                if (IsTokenPrecision(tk.Type))
                {
                    precision = GetTokenPrecision(tk.Type);
                    tk = GetToken();

                    if (!isStruct)
                    {
                        isStruct = shader.Structs.ContainsKey(tk.Text); // check again.
                    }
                }


                if (precision != DataPrecision.Default)
                {
                    if (!IsTokenNonvoidDatatype(tk.Type))
                    {
                        SetError(("Expected variable type after precision modifier."));
                        return Err.ParseError;
                    }
                }

                if (!isStruct)
                {
                    if (!IsTokenVariableDatatype(tk.Type))
                    {
                        SetError(("Invalid variable type (samplers are not allowed)."));
                        return Err.ParseError;
                    }
                }

                DataType type = isStruct ? DataType.Struct : GetTokenDatatype(tk.Type);

                if (precision != DataPrecision.Default && ValidatePrecision(type, precision) != Err.Ok)
                {
                    return Err.ParseError;
                }


                int arraySize = 0;
                bool fixedArraySize = false;
                bool first = true;

                VariableDeclarationNode vdnode = new VariableDeclarationNode();
                vdnode.Precision = precision;
                if (isStruct)
                {
                    vdnode.StructName = structName;
                    vdnode.Datatype = DataType.Struct;
                }
                else
                {
                    vdnode.Datatype = type;
                }
            ;
                vdnode.IsConst = isConst;

                do
                {
                    bool unknownSize = false;
                    Declaration decl = new();

                    tk = GetToken();

                    if (first)
                    {
                        first = false;

                        if (tk.Type != Tk.Identifier && tk.Type != Tk.BracketOpen)
                        {
                            SetError(("Expected an identifier or '[' after type."));
                            return Err.ParseError;
                        }

                        if (tk.Type == Tk.BracketOpen)
                        {
                            if (ParseArraySize(pBlock, pFunctionInfo, false, ref arraySize, ref unknownSize, out Node? sizeExpression) != Err.Ok)
                            {
                                return Err.ParseError;
                            }
                            decl.SizeExpression = sizeExpression;
                            decl.Size = (uint)(arraySize);

                            fixedArraySize = true;
                            tk = GetToken();
                        }
                    }

                    if (tk.Type != Tk.Identifier)
                    {
                        SetError(("Expected an identifier."));
                        return Err.ParseError;
                    }

                    string name = tk.Text;
                    IdentifierFound found = new();
                    if (FindIdentifier(pBlock, true, pFunctionInfo, name, found))
                    {
                        if (found.Type != IdentifierType.Function)
                        {
                            SetRedefinitionError((name));
                            return Err.ParseError;
                        }
                    }
                    decl.Name = name;

                    if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedLocalVariable))
                    {
                        FunctionNode? parentFunction = null;
                        {
                            BlockNode? block = pBlock;
                            while (block != null && block.ParentFunction == null)
                            {
                                block = block.ParentBlock;
                            }
                            parentFunction = block?.ParentFunction;
                        }
                        if (parentFunction != null)
                        {
                            string funcName = parentFunction.Name;

                            if (!usedLocalVars.ContainsKey(funcName))
                            {
                                usedLocalVars[funcName] = new Dictionary<string, Usage>();
                            }

                            usedLocalVars[funcName][name] = new Usage(tkLine);
                        }
                    }
                    isConstDecl = isConst;

                    BlockVariable variable = new();
                    variable.Type = type;
                    variable.Precision = precision;
                    variable.Line = tkLine;
                    variable.ArraySize = arraySize;
                    variable.IsConst = isConst;
                    variable.StructName = structName;

                    tk = GetToken();

                    if (tk.Type == Tk.BracketOpen)
                    {
                        int sizeRead = variable.ArraySize;
                        if (ParseArraySize(pBlock, pFunctionInfo, false, ref sizeRead, ref unknownSize, out Node? sizeExpression) != Err.Ok)
                        {
                            return Err.ParseError;
                        }
                        decl.SizeExpression = sizeExpression;
                        variable.ArraySize = sizeRead;

                        decl.Size = (uint)(variable.ArraySize);
                        arraySize = variable.ArraySize;

                        tk = GetToken();
                    }
                    if (variable.ArraySize > 0 || unknownSize)
                    {
                        bool fullDef = false;

                        if (tk.Type == Tk.OpAssign)
                        {
                            TkPos prevPos = GetTkPos();
                            tk = GetToken();

                            if (tk.Type == Tk.Identifier)
                            { // a function call array initialization
                                SetTkPos(prevPos);
                                Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);

                                if (n == null)
                                {
                                    SetError(("Expected array initializer."));
                                    return Err.ParseError;
                                }
                                else
                                {
                                    if (unknownSize)
                                    {
                                        decl.Size = (uint)(n.GetArraySize());
                                        variable.ArraySize = n.GetArraySize();
                                    }

                                    if (!CompareDatatypes(variable.Type, variable.StructName, variable.ArraySize, n.GetDatatype(), n.GetDatatypeName(), n.GetArraySize()))
                                    {
                                        return Err.ParseError;
                                    }

                                    decl.SingleExpression = true;
                                    decl.Initializer.Add(n);
                                }

                                tk = GetToken();
                            }
                            else
                            {
                                if (tk.Type != Tk.CurlyBracketOpen)
                                {
                                    if (unknownSize)
                                    {
                                        SetExpectedError("{");
                                        return Err.ParseError;
                                    }

                                    fullDef = true;

                                    DataPrecision precision2 = DataPrecision.Default;
                                    if (IsTokenPrecision(tk.Type))
                                    {
                                        precision2 = GetTokenPrecision(tk.Type);
                                        tk = GetToken();
                                        if (!IsTokenNonvoidDatatype(tk.Type))
                                        {
                                            SetError(("Expected data type after precision modifier."));
                                            return Err.ParseError;
                                        }
                                    }

                                    DataType type2;
                                    string structName2 = "";

                                    if (shader.Structs.ContainsKey(tk.Text))
                                    {
                                        type2 = DataType.Struct;
                                        structName2 = tk.Text;
                                    }
                                    else
                                    {
                                        if (!IsTokenVariableDatatype(tk.Type))
                                        {
                                            SetError(("Invalid data type for the array."));
                                            return Err.ParseError;
                                        }
                                        type2 = GetTokenDatatype(tk.Type);
                                    }

                                    if (precision2 != DataPrecision.Default && ValidatePrecision(type2, precision2) != Err.Ok)
                                    {
                                        return Err.ParseError;
                                    }

                                    int arraySize2 = 0;

                                    tk = GetToken();
                                    if (tk.Type == Tk.BracketOpen)
                                    {
                                        bool isUnknownSize = false;
                                        if (ParseArraySize(pBlock, pFunctionInfo, false, ref arraySize2, ref isUnknownSize, out _) != Err.Ok)
                                        {
                                            return Err.ParseError;
                                        }
                                        if (isUnknownSize)
                                        {
                                            arraySize2 = variable.ArraySize;
                                        }
                                        tk = GetToken();
                                    }
                                    else
                                    {
                                        SetExpectedError("[");
                                        return Err.ParseError;
                                    }

                                    if (precision != precision2 || type != type2 || structName != structName2 || variable.ArraySize != arraySize2)
                                    {
                                        string from = "";
                                        if (precision2 != DataPrecision.Default)
                                        {
                                            from += GetPrecisionName(precision2);
                                            from += " ";
                                        }
                                        if (type2 == DataType.Struct)
                                        {
                                            from += structName2;
                                        }
                                        else
                                        {
                                            from += GetDatatypeName(type2);
                                        }
                                        from += "[";
                                        from += Itos(arraySize2);
                                        from += "]'";

                                        string to = "";
                                        if (precision != DataPrecision.Default)
                                        {
                                            to += GetPrecisionName(precision);
                                            to += " ";
                                        }
                                        if (type == DataType.Struct)
                                        {
                                            to += structName;
                                        }
                                        else
                                        {
                                            to += GetDatatypeName(type);
                                        }
                                        to += "[";
                                        to += Itos(variable.ArraySize);
                                        to += "]'";

                                        SetError(Format(("Cannot convert from '%s' to '%s'."), from, to));
                                        return Err.ParseError;
                                    }
                                }

                                bool curly = tk.Type == Tk.CurlyBracketOpen;

                                if (unknownSize)
                                {
                                    if (!curly)
                                    {
                                        SetExpectedError("{");
                                        return Err.ParseError;
                                    }
                                }
                                else
                                {
                                    if (fullDef)
                                    {
                                        if (curly)
                                        {
                                            SetExpectedError("(");
                                            return Err.ParseError;
                                        }
                                    }
                                }

                                if (tk.Type == Tk.ParenthesisOpen || curly)
                                { // initialization
                                    while (true)
                                    {
                                        Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);
                                        if (n == null)
                                        {
                                            return Err.ParseError;
                                        }

                                        if (isConst && n.Type == NodeType.Operator && ((OperatorNode)n).Op == Operator.Call)
                                        {
                                            SetError(("Expected a constant expression."));
                                            return Err.ParseError;
                                        }

                                        if (!CompareDatatypes(variable.Type, structName, 0, n.GetDatatype(), n.GetDatatypeName(), 0))
                                        {
                                            return Err.ParseError;
                                        }

                                        tk = GetToken();
                                        if (tk.Type == Tk.Comma)
                                        {
                                            decl.Initializer.Add(n);
                                            continue;
                                        }
                                        else if (!curly && tk.Type == Tk.ParenthesisClose)
                                        {
                                            decl.Initializer.Add(n);
                                            break;
                                        }
                                        else if (curly && tk.Type == Tk.CurlyBracketClose)
                                        {
                                            decl.Initializer.Add(n);
                                            break;
                                        }
                                        else
                                        {
                                            if (curly)
                                            {
                                                SetExpectedError("}", ",");
                                            }
                                            else
                                            {
                                                SetExpectedError(")", ",");
                                            }
                                            return Err.ParseError;
                                        }
                                    }
                                    if (unknownSize)
                                    {
                                        decl.Size = (uint)(decl.Initializer.Count);
                                        variable.ArraySize = decl.Initializer.Count;
                                    }
                                    else if (decl.Initializer.Count != variable.ArraySize)
                                    {
                                        SetError(Format(("Array size mismatch. Expected %d elements (found %d)."), variable.ArraySize, decl.Initializer.Count));
                                        return Err.ParseError;
                                    }
                                    tk = GetToken();
                                }
                                else
                                {
                                    SetExpectedError("(");
                                    return Err.ParseError;
                                }
                            }
                        }
                        else
                        {
                            if (unknownSize)
                            {
                                SetError(("Expected array initialization."));
                                return Err.ParseError;
                            }
                            if (isConst)
                            {
                                SetError(("Expected initialization of constant."));
                                return Err.ParseError;
                            }
                        }

                        arraySize = variable.ArraySize;
                    }
                    else if (tk.Type == Tk.OpAssign)
                    {
                        pBlock.UseOpEval = isConst;

                        // Variable created with assignment! Must parse an expression.
                        Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);
                        if (n == null)
                        {
                            return Err.ParseError;
                        }
                        if (isConst && n.Type == NodeType.Operator && ((OperatorNode)n).Op == Operator.Call)
                        {
                            OperatorNode op = ((OperatorNode)n);
                            for (int i = 1; i < op.Arguments.Count; i++)
                            {
                                if (!CheckNodeConstness(op.Arguments[i]))
                                {
                                    SetError(Format(("Expected constant expression for argument %d of function call after '='."), i - 1));
                                    return Err.ParseError;
                                }
                            }
                        }

                        if (isConst)
                        {
                            variable.Values = n.GetValues();
                        }

                        if (!CompareDatatypes(variable.Type, variable.StructName, variable.ArraySize, n.GetDatatype(), n.GetDatatypeName(), n.GetArraySize()))
                        {
                            return Err.ParseError;
                        }

                        decl.Initializer.Add(n);
                        tk = GetToken();
                    }
                    else
                    {
                        if (isConst)
                        {
                            SetError(("Expected initialization of constant."));
                            return Err.ParseError;
                        }
                    }

                    vdnode.Declarations.Add(decl);
                    pBlock.Variables[name] = variable;
                    isConstDecl = false;

                    if (!fixedArraySize)
                    {
                        arraySize = 0;
                    }

                    if (tk.Type == Tk.Semicolon)
                    {
                        break;
                    }
                    else if (tk.Type != Tk.Comma)
                    {
                        SetExpectedError(",", ";");
                        return Err.ParseError;
                    }
                } while (tk.Type == Tk.Comma); //another variable
                pBlock.Statements.Add(vdnode);
            }
            else if (tk.Type == Tk.CurlyBracketOpen)
            {
                //a sub block, just because..
                BlockNode block = new BlockNode();
                block.ParentBlock = pBlock;
                if (ParseBlock(block, pFunctionInfo, false, pCanBreak, pCanContinue) != Err.Ok)
                {
                    return Err.ParseError;
                }
                pBlock.Statements.Add(block);
            }
            else if (tk.Type == Tk.CfIf)
            {
                //if () {}
                tk = GetToken();
                if (tk.Type != Tk.ParenthesisOpen)
                {
                    SetExpectedAfterError("(", "if");
                    return Err.ParseError;
                }

                ControlFlowNode cf = new ControlFlowNode();
                cf.FlowOp = FlowOperation.If;
                Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);
                if (n == null)
                {
                    return Err.ParseError;
                }

                if (n.GetDatatype() != DataType.Bool)
                {
                    SetError(("Expected a boolean expression."));
                    return Err.ParseError;
                }

                tk = GetToken();
                if (tk.Type != Tk.ParenthesisClose)
                {
                    SetExpectedError(")");
                    return Err.ParseError;
                }

                BlockNode block = new BlockNode();
                block.ParentBlock = pBlock;
                cf.Expressions.Add(n);
                cf.Blocks.Add(block);
                pBlock.Statements.Add(cf);

                if (ParseBlock(block, pFunctionInfo, true, pCanBreak, pCanContinue) != Err.Ok)
                {
                    return Err.ParseError;
                }

                pos = GetTkPos();
                tk = GetToken();
                if (tk.Type == Tk.CfElse)
                {
                    block = new BlockNode();
                    block.ParentBlock = pBlock;
                    cf.Blocks.Add(block);
                    if (ParseBlock(block, pFunctionInfo, true, pCanBreak, pCanContinue) != Err.Ok)
                    {
                        return Err.ParseError;
                    }
                }
                else
                {
                    SetTkPos(pos); //rollback
                }
            }
            else if (tk.Type == Tk.CfSwitch)
            {
                // switch() {}
                tk = GetToken();
                if (tk.Type != Tk.ParenthesisOpen)
                {
                    SetExpectedAfterError("(", "switch");
                    return Err.ParseError;
                }
                ControlFlowNode cf = new ControlFlowNode();
                cf.FlowOp = FlowOperation.Switch;
                Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);
                if (n == null)
                {
                    return Err.ParseError;
                }
                DataType dataType = n.GetDatatype();
                if (dataType != DataType.Int && dataType != DataType.Uint)
                {
                    SetError(("Expected an integer or unsigned integer expression."));
                    return Err.ParseError;
                }
                tk = GetToken();
                if (tk.Type != Tk.ParenthesisClose)
                {
                    SetExpectedError(")");
                    return Err.ParseError;
                }
                tk = GetToken();
                if (tk.Type != Tk.CurlyBracketOpen)
                {
                    SetExpectedAfterError("{", "switch");
                    return Err.ParseError;
                }
                BlockNode switchBlock = new BlockNode();
                switchBlock.BlockType = BlockType.Switch;
                switchBlock.ParentBlock = pBlock;
                switchBlock.ExpectedType = dataType;
                cf.Expressions.Add(n);
                cf.Blocks.Add(switchBlock);
                pBlock.Statements.Add(cf);

                pos = GetTkPos();
                tk = GetToken();
                bool hasDefault = false;
                if (tk.Type == Tk.CfCase || tk.Type == Tk.CfDefault)
                {
                    if (tk.Type == Tk.CfDefault)
                    {
                        hasDefault = true;
                    }
                    SetTkPos(pos);
                }
                else
                {
                    SetExpectedError("case", "default");
                    return Err.ParseError;
                }

                while (true)
                { // Go-through multiple cases.

                    if (ParseBlock(switchBlock, pFunctionInfo, true, true, false) != Err.Ok)
                    {
                        return Err.ParseError;
                    }
                    pos = GetTkPos();
                    tk = GetToken();
                    if (tk.Type == Tk.CfCase)
                    {
                        SetTkPos(pos);
                        continue;
                    }
                    else if (tk.Type == Tk.CfDefault)
                    {
                        if (hasDefault)
                        {
                            SetError(("Default case must be defined only once."));
                            return Err.ParseError;
                        }
                        hasDefault = true;
                        SetTkPos(pos);
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }

            }
            else if (tk.Type == Tk.CfCase)
            {
                // case x : break; | return;

                if (pBlock.BlockType == BlockType.Case)
                {
                    SetTkPos(pos);
                    return Err.Ok;
                }

                if (pBlock.BlockType != BlockType.Switch)
                {
                    SetError(Format(("'%s' must be placed within a '%s' block."), "case", "switch"));
                    return Err.ParseError;
                }

                tk = GetToken();

                int sign = 1;

                if (tk.Type == Tk.OpSub)
                {
                    sign = -1;
                    tk = GetToken();
                }

                Node? n = null;

                if (!tk.IsIntegerConstant)
                {
                    bool correctConstantExpression = false;

                    if (tk.Type == Tk.Identifier)
                    {
                        IdentifierFound identified = new();
                        bool found = FindIdentifier(pBlock, false, pFunctionInfo, tk.Text, identified);
                        DataType dataType = identified.DataType;
                        bool isConst = identified.IsConst;
                        if (!found)
                        {
                            SetError(Format(("Undefined identifier '%s' in a case label."), (tk.Text)));
                            return Err.ParseError;
                        }
                        if (isConst && dataType == pBlock.ExpectedType)
                        {
                            correctConstantExpression = true;
                        }
                    }

                    if (!correctConstantExpression)
                    {
                        if (pBlock.ExpectedType == DataType.Uint)
                        {
                            SetError(("Expected an unsigned integer constant."));
                        }
                        else
                        {
                            SetError(("Expected an integer constant."));
                        }
                        return Err.ParseError;
                    }

                    VariableNode vn = new VariableNode();
                    vn.Name = tk.Text;
                    {
                        IdentifierFound found = new() { WantsValues = true };
                        FindIdentifier(pBlock, false, pFunctionInfo, vn.Name, found);
                        List<Scalar> v = found.ConstantValues ?? [];
                        DataType dataType = found.DataType;
                        if (v.Count == 0)
                        {
                            SetError(Format(CaseLabelUnknown, vn.Name));
                            return Err.ParseError;
                        }
                        if (dataType == DataType.Int)
                        {
                            if (pBlock.Constants.Contains(v[0].Sint))
                            {
                                SetError(Format(("Duplicated case label: %d."), v[0].Sint));
                                return Err.ParseError;
                            }
                            pBlock.Constants.Add(v[0].Sint);
                        }
                        else
                        {
                            if (pBlock.Constants.Contains((int)v[0].Uint))
                            {
                                SetError(Format(("Duplicated case label: %d."), v[0].Uint));
                                return Err.ParseError;
                            }
                            pBlock.Constants.Add((int)v[0].Uint);
                        }
                    }
                    n = vn;
                }
                else
                {
                    ConstantNode cn = new ConstantNode();
                    Scalar v = default;
                    if (pBlock.ExpectedType == DataType.Uint)
                    {
                        if (tk.Type != Tk.UintConstant)
                        {
                            SetError(("Expected an unsigned integer constant."));
                            return Err.ParseError;
                        }
                        v.Uint = (uint)tk.Constant;
                        if (pBlock.Constants.Contains((int)v.Uint))
                        {
                            SetError(Format(("Duplicated case label: %d."), v.Uint));
                            return Err.ParseError;
                        }
                        pBlock.Constants.Add((int)v.Uint);
                        cn.Datatype = DataType.Uint;
                    }
                    else
                    {
                        if (tk.Type != Tk.IntConstant)
                        {
                            SetError(("Expected an integer constant."));
                            return Err.ParseError;
                        }
                        v.Sint = (int)tk.Constant * sign;
                        if (pBlock.Constants.Contains(v.Sint))
                        {
                            SetError(Format(("Duplicated case label: %d."), v.Sint));
                            return Err.ParseError;
                        }
                        pBlock.Constants.Add(v.Sint);
                        cn.Datatype = DataType.Int;
                    }
                    cn.Values.Add(v);
                    n = cn;
                }

                tk = GetToken();

                if (tk.Type != Tk.Colon)
                {
                    SetExpectedError(":");
                    return Err.ParseError;
                }

                ControlFlowNode cf = new ControlFlowNode();
                cf.FlowOp = FlowOperation.Case;

                BlockNode caseBlock = new BlockNode();
                caseBlock.BlockType = BlockType.Case;
                caseBlock.ParentBlock = pBlock;
                cf.Expressions.Add(n);
                cf.Blocks.Add(caseBlock);
                pBlock.Statements.Add(cf);

                if (ParseBlock(caseBlock, pFunctionInfo, false, true, false) != Err.Ok)
                {
                    return Err.ParseError;
                }

                return Err.Ok;

            }
            else if (tk.Type == Tk.CfDefault)
            {
                if (pBlock.BlockType == BlockType.Case)
                {
                    SetTkPos(pos);
                    return Err.Ok;
                }

                if (pBlock.BlockType != BlockType.Switch)
                {
                    SetError(Format(("'%s' must be placed within a '%s' block."), "default", "switch"));
                    return Err.ParseError;
                }

                tk = GetToken();

                if (tk.Type != Tk.Colon)
                {
                    SetExpectedError(":");
                    return Err.ParseError;
                }

                ControlFlowNode cf = new ControlFlowNode();
                cf.FlowOp = FlowOperation.Default;

                BlockNode defaultBlock = new BlockNode();
                defaultBlock.BlockType = BlockType.Default;
                defaultBlock.ParentBlock = pBlock;
                cf.Blocks.Add(defaultBlock);
                pBlock.Statements.Add(cf);

                if (ParseBlock(defaultBlock, pFunctionInfo, false, true, false) != Err.Ok)
                {
                    return Err.ParseError;
                }

                return Err.Ok;

            }
            else if (tk.Type == Tk.CfDo || tk.Type == Tk.CfWhile)
            {
                // do {} while() or while() {}.
                bool isDo = tk.Type == Tk.CfDo;

                BlockNode? doBlock = null;
                if (isDo)
                {
                    doBlock = new BlockNode();
                    doBlock.ParentBlock = pBlock;

                    if (ParseBlock(doBlock, pFunctionInfo, true, true, true) != Err.Ok)
                    {
                        return Err.ParseError;
                    }

                    tk = GetToken();
                    if (tk.Type != Tk.CfWhile)
                    {
                        SetExpectedAfterError("while", "do");
                        return Err.ParseError;
                    }
                }
                tk = GetToken();

                if (tk.Type != Tk.ParenthesisOpen)
                {
                    SetExpectedAfterError("(", "while");
                    return Err.ParseError;
                }

                ControlFlowNode cf = new ControlFlowNode();
                if (isDo)
                {
                    cf.FlowOp = FlowOperation.Do;
                }
                else
                {
                    cf.FlowOp = FlowOperation.While;
                }
                Node? n = ParseAndReduceExpression(pBlock, pFunctionInfo);
                if (n == null)
                {
                    return Err.ParseError;
                }

                tk = GetToken();
                if (tk.Type != Tk.ParenthesisClose)
                {
                    SetExpectedError(")");
                    return Err.ParseError;
                }
                if (!isDo)
                {
                    BlockNode block = new BlockNode();
                    block.ParentBlock = pBlock;
                    cf.Expressions.Add(n);
                    cf.Blocks.Add(block);
                    pBlock.Statements.Add(cf);

                    if (ParseBlock(block, pFunctionInfo, true, true, true) != Err.Ok)
                    {
                        return Err.ParseError;
                    }
                }
                else
                {
                    cf.Expressions.Add(n);
                    cf.Blocks.Add(doBlock!);
                    pBlock.Statements.Add(cf);

                    tk = GetToken();
                    if (tk.Type != Tk.Semicolon)
                    {
                        SetExpectedError(";");
                        return Err.ParseError;
                    }
                }
            }
            else if (tk.Type == Tk.CfFor)
            {
                // for() {}
                tk = GetToken();
                if (tk.Type != Tk.ParenthesisOpen)
                {
                    SetExpectedAfterError("(", "for");
                    return Err.ParseError;
                }

                ControlFlowNode cf = new ControlFlowNode();
                cf.FlowOp = FlowOperation.For;

                BlockNode initBlock = new BlockNode();
                initBlock.BlockType = BlockType.ForInit;
                initBlock.ParentBlock = pBlock;
                initBlock.SingleStatement = true;
                cf.Blocks.Add(initBlock);

                if (ParseBlock(initBlock, pFunctionInfo, true, false, false) != Err.Ok)
                {
                    return Err.ParseError;
                }

                BlockNode conditionBlock = new BlockNode();
                conditionBlock.BlockType = BlockType.ForCondition;
                conditionBlock.ParentBlock = initBlock;
                conditionBlock.SingleStatement = true;
                conditionBlock.UseCommaBetweenStatements = true;
                cf.Blocks.Add(conditionBlock);
                if (ParseBlock(conditionBlock, pFunctionInfo, true, false, false) != Err.Ok)
                {
                    return Err.ParseError;
                }

                BlockNode expressionBlock = new BlockNode();
                expressionBlock.BlockType = BlockType.ForExpression;
                expressionBlock.ParentBlock = initBlock;
                expressionBlock.SingleStatement = true;
                expressionBlock.UseCommaBetweenStatements = true;
                cf.Blocks.Add(expressionBlock);
                if (ParseBlock(expressionBlock, pFunctionInfo, true, false, false) != Err.Ok)
                {
                    return Err.ParseError;
                }

                BlockNode block = new BlockNode();
                block.ParentBlock = initBlock;
                cf.Blocks.Add(block);
                pBlock.Statements.Add(cf);

                if (ParseBlock(block, pFunctionInfo, true, true, true) != Err.Ok)
                {
                    return Err.ParseError;
                }
            }
            else if (tk.Type == Tk.CfReturn)
            {
                //check return type
                BlockNode? b = pBlock;

                while (b != null && b.ParentFunction == null)
                {
                    b = b.ParentBlock;
                }

                if (b?.ParentFunction is not FunctionNode returnFunction)
                {
                    SetParsingError();
                    return Err.Bug;
                }

                if (pFunctionInfo.MainFunction)
                {
                    SetError(Format(("Using '%s' in the '%s' processor function is incorrect."), "return", returnFunction.Name));
                    return Err.ParseError;
                }

                string returnStructName = (returnFunction.ReturnStructName);
                string arraySizeString = "";

                if (returnFunction.ReturnArraySize > 0)
                {
                    arraySizeString = "[" + Itos(returnFunction.ReturnArraySize) + "]";
                }

                ControlFlowNode flow = new ControlFlowNode();
                flow.FlowOp = FlowOperation.Return;

                pos = GetTkPos();
                tk = GetToken();
                if (tk.Type == Tk.Semicolon)
                {
                    //all is good
                    if (returnFunction.ReturnType != DataType.Void)
                    {
                        SetError(Format(("Expected '%s' with an expression of type '%s'."), "return", (returnStructName.Length > 0 ? returnStructName : GetDatatypeName(returnFunction.ReturnType)) + arraySizeString));
                        return Err.ParseError;
                    }
                }
                else
                {
                    if (returnFunction.ReturnType == DataType.Void)
                    {
                        SetError(Format(("'%s' function cannot return a value."), "void"));
                        return Err.ParseError;
                    }

                    SetTkPos(pos); //rollback, wants expression


                    Node? expr = ParseAndReduceExpression(pBlock, pFunctionInfo);
                    if (expr == null)
                    {
                        return Err.ParseError;
                    }

                    if (returnFunction.ReturnType != expr.GetDatatype() || returnFunction.ReturnArraySize != expr.GetArraySize() || returnStructName != expr.GetDatatypeName())
                    {
                        SetError(Format(("Expected return with an expression of type '%s'."), (returnStructName.Length > 0 ? returnStructName : GetDatatypeName(returnFunction.ReturnType)) + arraySizeString));
                        return Err.ParseError;
                    }

                    tk = GetToken();
                    if (tk.Type != Tk.Semicolon)
                    {
                        SetExpectedAfterError(";", "return");
                        return Err.ParseError;
                    }


                    flow.Expressions.Add(expr);
                }

                pBlock.Statements.Add(flow);

                BlockNode? block = pBlock;
                while (block != null)
                {
                    if (block.BlockType == BlockType.Case || block.BlockType == BlockType.Default)
                    {
                        return Err.Ok;
                    }
                    block = block.ParentBlock;
                }
            }
            else if (tk.Type == Tk.CfDiscard)
            {
                if (!isDiscardSupported)
                {
                    SetError(Format(("Use of '%s' is not supported for the '%s' shader type."), "discard", shaderTypeIdentifier));
                    return Err.ParseError;
                }

                //check return type
                BlockNode? b = pBlock;
                while (b != null && b.ParentFunction == null)
                {
                    b = b.ParentBlock;
                }
                if (b?.ParentFunction is not FunctionNode discardFunction)
                {
                    SetParsingError();
                    return Err.Bug;
                }

                if (!discardFunction.CanDiscard)
                {
                    SetError(Format(("'%s' cannot be used within the '%s' processor function."), "discard", discardFunction.Name));
                    return Err.ParseError;
                }

                ControlFlowNode flow = new ControlFlowNode();
                flow.FlowOp = FlowOperation.Discard;

                pos = GetTkPos();
                tk = GetToken();

                callsInfo[discardFunction.Name].UsesRestrictedItems.Add(("discard", new CallItem(CallItemType.Builtin, pos)));

                if (tk.Type != Tk.Semicolon)
                {
                    SetExpectedAfterError(";", "discard");
                    return Err.ParseError;
                }

                pBlock.Statements.Add(flow);
            }
            else if (tk.Type == Tk.CfBreak)
            {
                if (!pCanBreak)
                {
                    SetError(Format(("'%s' is not allowed outside of a loop or '%s' statement."), "break", "switch"));
                    return Err.ParseError;
                }

                ControlFlowNode flow = new ControlFlowNode();
                flow.FlowOp = FlowOperation.Break;

                pos = GetTkPos();
                tk = GetToken();
                if (tk.Type != Tk.Semicolon)
                {
                    SetExpectedAfterError(";", "break");
                    return Err.ParseError;
                }

                pBlock.Statements.Add(flow);

                BlockNode? block = pBlock;
                while (block != null)
                {
                    if (block.BlockType == BlockType.Case || block.BlockType == BlockType.Default)
                    {
                        return Err.Ok;
                    }
                    block = block.ParentBlock;
                }

            }
            else if (tk.Type == Tk.CfContinue)
            {
                if (!pCanContinue)
                {
                    SetError(Format(("'%s' is not allowed outside of a loop."), "continue"));
                    return Err.ParseError;
                }

                ControlFlowNode flow = new ControlFlowNode();
                flow.FlowOp = FlowOperation.Continue;

                pos = GetTkPos();
                tk = GetToken();
                if (tk.Type != Tk.Semicolon)
                {
                    //all is good
                    SetExpectedAfterError(";", "continue");
                    return Err.ParseError;
                }

                pBlock.Statements.Add(flow);

            }
            else
            {
                //nothing else, so expression
                SetTkPos(pos); //rollback
                Node? expr = ParseAndReduceExpression(pBlock, pFunctionInfo);
                if (expr == null)
                {
                    return Err.ParseError;
                }
                isCondition = expr.GetDatatype() == DataType.Bool;

                if (expr.Type == NodeType.Operator)
                {
                    OperatorNode op = ((OperatorNode)expr);
                    if (op.Op == Operator.Empty)
                    {
                        isVarInit = true;
                        isCondition = true;
                    }
                }

                pBlock.Statements.Add(expr);
                tk = GetToken();

                if (pBlock.BlockType == BlockType.ForCondition)
                {
                    if (tk.Type == Tk.Comma)
                    {
                        if (!isCondition)
                        {
                            SetError(("The middle expression is expected to have a boolean data type."));
                            return Err.ParseError;
                        }
                        tk = Peek();
                        if (tk.Type == Tk.Semicolon)
                        {
                            SetError(Format(("Expected expression, found: '%s'."), GetTokenText(tk)));
                            return Err.ParseError;
                        }
                        continue;
                    }
                    if (tk.Type != Tk.Semicolon)
                    {
                        SetExpectedError(",", ";");
                        return Err.ParseError;
                    }
                }
                else if (pBlock.BlockType == BlockType.ForExpression)
                {
                    if (tk.Type == Tk.Comma)
                    {
                        tk = Peek();
                        if (tk.Type == Tk.ParenthesisClose)
                        {
                            SetError(Format(("Expected expression, found: '%s'."), GetTokenText(tk)));
                            return Err.ParseError;
                        }
                        continue;
                    }
                    if (tk.Type != Tk.ParenthesisClose)
                    {
                        SetExpectedError(",", ")");
                        return Err.ParseError;
                    }
                }
                else if (tk.Type != Tk.Semicolon)
                {
                    SetExpectedError(";");
                    return Err.ParseError;
                }
            }

            if (pBlock.BlockType == BlockType.ForInit && !isVarInit)
            {
                SetError(("The left expression is expected to be a variable declaration."));
                return Err.ParseError;
            }
            if (pBlock.BlockType == BlockType.ForCondition && !isCondition)
            {
                SetError(("The middle expression is expected to have a boolean data type."));
                return Err.ParseError;
            }

            if (pJustOne)
            {
                break;
            }
        }

        return Err.Ok;
    }

    private string GetShaderTypeList(HashSet<string> pShaderTypes)
    {
        // Return a list of shader types as an human-readable string
        string validTypes = "";
        foreach (string E in pShaderTypes)
        {
            if (validTypes.Length > 0)
            {
                validTypes += ", ";
            }

            validTypes += "'" + E + "'";
        }

        return validTypes;
    }

    private string GetQualifierStr(ArgumentQualifier pQualifier)
    {
        switch (pQualifier)
        {
            case ArgumentQualifier.In:
                return "in";

            case ArgumentQualifier.Out:
                return "out";
            case ArgumentQualifier.Inout:
                return "inout";
        }
        return "";
    }

    private Err ValidatePrecision(DataType pType, DataPrecision pPrecision)
    {
        switch (pType)
        {
            case DataType.Struct:
                {
                    SetError(("The precision modifier cannot be used on structs."));
                    return Err.Failed;
                }
            case DataType.Bool:
            case DataType.Bvec2:
            case DataType.Bvec3:
            case DataType.Bvec4:
                {
                    SetError(("The precision modifier cannot be used on boolean types."));
                    return Err.Failed;
                }
            default:
                break;
        }
        return Err.Ok;
    }

    private bool ParseNumericConstantExpression(FunctionInfo pFunctionInfo, ref float rConstant)
    {
        Node? expr = ParseAndReduceExpression(null, pFunctionInfo);
        if (expr == null)
        {
            return false;
        }

        List<Scalar> values = [];
        if (expr.Type == NodeType.Variable)
        {
            IdentifierFound found = new() { WantsValues = true };
            if (FindIdentifier(null, false, pFunctionInfo, ((VariableNode)expr).Name, found) && found.ConstantValues != null)
            {
                values = found.ConstantValues;
            }
        }
        else
        {
            values = expr.GetValues();
        }

        if (values.Count == 0)
        {
            return false; // To prevent possible crash.
        }

        switch (expr.GetDatatype())
        {
            case DataType.Float:
                rConstant = values[0].Real;
                break;
            case DataType.Int:
                rConstant = ((float)values[0].Sint);
                break;
            case DataType.Uint:
                rConstant = ((float)values[0].Uint);
                break;
            default:
                return false;
        }

        return true;
    }
}
