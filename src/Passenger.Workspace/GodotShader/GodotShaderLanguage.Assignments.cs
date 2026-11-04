namespace Passenger.Workspace.GodotShader;

/// <summary>Where Godot's shading language lets a value be written (shader_language.cpp, MIT): no constant, uniform or function,
/// no swizzle with a part twice, a varying in its stage's function only.</summary>
public sealed partial class GodotShaderLanguage
{
    private bool ValidateVaryingAssign(Varying pVarying, ref string rMessage)
    {
        if (currentFunction != "vertex" && currentFunction != "fragment")
        {
            rMessage = Format(("Varying may not be assigned in the '%s' function."), currentFunction);
            return false;
        }
        switch (pVarying.Stage)
        {
            case VaryingStage.Unknown: // first assign
                if (currentFunction == varyingFunctionNames.Vertex)
                {
                    if (pVarying.Type < DataType.Int)
                    {
                        rMessage = Format(("Varying with '%s' data type may only be assigned in the '%s' function."), GetDatatypeName(pVarying.Type), "fragment");
                        return false;
                    }
                    pVarying.Stage = VaryingStage.Vertex;
                }
                else if (currentFunction == varyingFunctionNames.Fragment)
                {
                    pVarying.Stage = VaryingStage.Fragment;
                }
                break;
            case VaryingStage.Vertex:
                if (currentFunction == varyingFunctionNames.Fragment)
                {
                    rMessage = Format(("Varyings which assigned in '%s' function may not be reassigned in '%s' or '%s'."), "vertex", "fragment", "light");
                    return false;
                }
                break;
            case VaryingStage.Fragment:
                if (currentFunction == varyingFunctionNames.Vertex)
                {
                    rMessage = Format(("Varyings which assigned in '%s' function may not be reassigned in '%s' or '%s'."), "fragment", "vertex", "light");
                    return false;
                }
                break;
            default:
                break;
        }
        return true;
    }

    private bool CheckNodeConstness(Node pNode)
    {
        switch (pNode.Type)
        {
            case NodeType.Operator:
                {
                    OperatorNode opNode = ((OperatorNode)pNode);
                    for (int i = opNode.Op == Operator.Call ? 1 : 0; i < opNode.Arguments.Count; i++)
                    {
                        if (!CheckNodeConstness(opNode.Arguments[i]))
                        {
                            return false;
                        }
                    }
                }
                break;

            case NodeType.Constant:
                break;
            case NodeType.Variable:
                {
                    VariableNode varNode = ((VariableNode)pNode);
                    if (!varNode.IsConst)
                    {
                        return false;
                    }
                }
                break;
            case NodeType.Array:
                {
                    ArrayNode arrNode = ((ArrayNode)pNode);
                    if (!arrNode.IsConst)
                    {
                        return false;
                    }
                }
                break;
            default:
                return false;
        }
        return true;
    }

    private bool ValidateAssign(Node pNode, FunctionInfo pFunctionInfo, ref string rMessage)
    {
        if (pNode.Type == NodeType.Operator)
        {
            OperatorNode op = ((OperatorNode)pNode);

            if (op.Op == Operator.Index)
            {
                return ValidateAssign(op.Arguments[0], pFunctionInfo, ref rMessage);

            }
            else if (IsOperatorAssign(op.Op))
            {
                //chained assignment
                return ValidateAssign(op.Arguments[1], pFunctionInfo, ref rMessage);

            }
            else if (op.Op == Operator.Call)
            {
                rMessage = ("Assignment to function.");
                return false;
            }

        }
        else if (pNode.Type == NodeType.Member)
        {
            MemberNode member = ((MemberNode)pNode);

            if (member.HasSwizzlingDuplicates)
            {
                rMessage = ("Swizzling assignment contains duplicates.");
                return false;
            }

            return ValidateAssign(member.Owner!, pFunctionInfo, ref rMessage);

        }
        else if (pNode.Type == NodeType.Variable)
        {
            VariableNode variable = ((VariableNode)pNode);

            if (shader.Uniforms.ContainsKey(variable.Name))
            {
                rMessage = ("Assignment to uniform.");
                return false;
            }

            if (shader.Constants.ContainsKey(variable.Name) || variable.IsConst)
            {
                rMessage = ("Constants cannot be modified.");
                return false;
            }

            if (shader.Varyings.ContainsKey(variable.Name))
            {
                return ValidateVaryingAssign(shader.Varyings[variable.Name], ref rMessage);
            }

            if (!(pFunctionInfo.BuiltIns.ContainsKey(variable.Name) && pFunctionInfo.BuiltIns[variable.Name].Constant))
            {
                return true;
            }
        }
        else if (pNode.Type == NodeType.Array)
        {
            ArrayNode arr = ((ArrayNode)pNode);

            if (shader.Constants.ContainsKey(arr.Name) || arr.IsConst)
            {
                rMessage = ("Constants cannot be modified.");
                return false;
            }

            return true;
        }

        rMessage = "Assignment to constant expression.";
        return false;
    }
}
