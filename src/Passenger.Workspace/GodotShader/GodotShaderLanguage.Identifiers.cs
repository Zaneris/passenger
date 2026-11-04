namespace Passenger.Workspace.GodotShader;

/// <summary>What a name in a shader names, as Godot's shading language finds it (shader_language.cpp, MIT): a built-in, a stage
/// function, a local, an argument, a varying, a uniform, a constant or a function, and the uses that keep its warning away.</summary>
public sealed partial class GodotShaderLanguage
{
    /// <summary>Why an overload of a function the shader declares refused a call: its arguments, the one refused and its name.</summary>
    private struct OverloadErrorInfo
    {
        public string ArgList;
        public int Index;
        public string FuncArgName;
        public string ArgName;
    }

    private void ParseUsedIdentifier(string pIdentifier, IdentifierType pType, string pFunction)
    {
        switch (pType)
        {
            case IdentifierType.Constant:
                if (HasWarning(GodotShaderWarningFlags.UnusedConstant) && usedConstants.ContainsKey(pIdentifier))
                {
                    usedConstants[pIdentifier].Used = true;
                }
                break;
            case IdentifierType.Varying:
                if (HasWarning(GodotShaderWarningFlags.UnusedVarying) && usedVaryings.ContainsKey(pIdentifier))
                {
                    if (shader.Varyings[pIdentifier].Stage == VaryingStage.Unknown)
                    {
                        usedVaryings[pIdentifier].Used = true;
                    }
                }
                break;
            case IdentifierType.Uniform:
                if (HasWarning(GodotShaderWarningFlags.UnusedUniform) && usedUniforms.ContainsKey(pIdentifier))
                {
                    usedUniforms[pIdentifier].Used = true;
                }
                break;
            case IdentifierType.Function:
                if (HasWarning(GodotShaderWarningFlags.UnusedFunction) && usedFunctions.ContainsKey(pIdentifier))
                {
                    usedFunctions[pIdentifier].Used = true;
                }
                break;
            case IdentifierType.LocalVar:
                if (HasWarning(GodotShaderWarningFlags.UnusedLocalVariable) && usedLocalVars.ContainsKey(pFunction) && usedLocalVars[pFunction].ContainsKey(pIdentifier))
                {
                    usedLocalVars[pFunction][pIdentifier].Used = true;
                }
                break;
            default:
                break;
        }
    }

    private bool FindIdentifier(BlockNode? pBlock, bool pAllowReassign, FunctionInfo pFunctionInfo, string pIdentifier, IdentifierFound? found = null)
    {
        if (isShaderInc)
        {
            foreach (Dictionary<string, FunctionInfo> functions in compileInfo.AllFunctions)
            {
                foreach (KeyValuePair<string, FunctionInfo> E in functions)
                {
                    if ((currentFunction == E.Key || E.Key == "global" || E.Key == "constants") && E.Value.BuiltIns.ContainsKey(pIdentifier))
                    {
                        found?.DataType = E.Value.BuiltIns[pIdentifier].Type;
                        found?.IsConst = E.Value.BuiltIns[pIdentifier].Constant;
                        found?.Type = IdentifierType.BuiltinVar;
                        return true;
                    }
                }
            }
        }
        else
        {
            if (pFunctionInfo.BuiltIns.ContainsKey(pIdentifier))
            {
                found?.DataType = pFunctionInfo.BuiltIns[pIdentifier].Type;
                found?.IsConst = pFunctionInfo.BuiltIns[pIdentifier].Constant;
                found?.ConstantValues = pFunctionInfo.BuiltIns[pIdentifier].ValuesList;
                found?.Type = IdentifierType.BuiltinVar;
                return true;
            }
        }

        if (pFunctionInfo.StageFunctions.ContainsKey(pIdentifier))
        {
            found?.DataType = pFunctionInfo.StageFunctions[pIdentifier].ReturnType;
            found?.IsConst = true;
            found?.Type = IdentifierType.Function;
            return true;
        }

        FunctionNode? function = null;

        while (pBlock != null)
        {
            if (pBlock.Variables.ContainsKey(pIdentifier))
            {
                found?.DataType = pBlock.Variables[pIdentifier].Type;
                found?.IsConst = pBlock.Variables[pIdentifier].IsConst;
                found?.ArraySize = pBlock.Variables[pIdentifier].ArraySize;
                found?.StructName = pBlock.Variables[pIdentifier].StructName;
                if (found != null && pBlock.Variables[pIdentifier].Values.Count > 0)
                {
                    found.ConstantValues = [.. pBlock.Variables[pIdentifier].Values];
                }
                found?.Type = IdentifierType.LocalVar;
                return true;
            }

            if (pBlock.ParentFunction != null)
            {
                function = pBlock.ParentFunction;
                break;
            }
            else
            {
                if (pAllowReassign)
                {
                    break;
                }
                if (pBlock.ParentBlock == null)
                {
                    return false;
                }
                pBlock = pBlock.ParentBlock;
            }
        }

        if (function != null)
        {
            for (int i = 0; i < function.Arguments.Count; i++)
            {
                if (function.Arguments[i].Name == pIdentifier)
                {
                    found?.DataType = function.Arguments[i].Type;
                    found?.StructName = function.Arguments[i].StructName;
                    found?.ArraySize = function.Arguments[i].ArraySize;
                    found?.IsConst = function.Arguments[i].IsConst;
                    found?.Type = IdentifierType.FunctionArgument;
                    return true;
                }
            }
        }

        if (shader.Varyings.ContainsKey(pIdentifier))
        {
            found?.DataType = shader.Varyings[pIdentifier].Type;
            found?.ArraySize = shader.Varyings[pIdentifier].ArraySize;
            found?.Type = IdentifierType.Varying;
            return true;
        }

        if (shader.Uniforms.ContainsKey(pIdentifier))
        {
            found?.DataType = shader.Uniforms[pIdentifier].Type;
            found?.ArraySize = shader.Uniforms[pIdentifier].ArraySize;
            found?.Type = IdentifierType.Uniform;
            return true;
        }

        if (shader.Constants.ContainsKey(pIdentifier))
        {
            found?.IsConst = true;
            found?.DataType = shader.Constants[pIdentifier].Type;
            found?.ArraySize = shader.Constants[pIdentifier].ArraySize;
            found?.StructName = shader.Constants[pIdentifier].StructName;
            if (found is { WantsValues: true } && shader.Constants[pIdentifier].Initializer is Node initializer)
            {
                List<Scalar> values = GetNodeValues(pBlock, pFunctionInfo, initializer);
                if (values.Count > 0)
                {
                    found.ConstantValues = values;
                }
            }
            found?.Type = IdentifierType.Constant;
            return true;
        }

        for (int i = 0; i < shader.Vfunctions.Count; i++)
        {
            if (!shader.Vfunctions[i].Callable)
            {
                continue;
            }

            if (shader.Vfunctions[i].Name == pIdentifier)
            {
                found?.DataType = shader.Vfunctions[i].Function.ReturnType;
                found?.ArraySize = shader.Vfunctions[i].Function.ReturnArraySize;
                found?.Type = IdentifierType.Function;
                return true;
            }
        }

        return false;
    }

    private bool GetCompletableIdentifier(BlockNode? pBlock, CompletionType pType, out string identifier)
    {
        identifier = "";

        TkPos pos = new(0, 0);

        Token tk = GetToken();

        if (tk.Type == Tk.Identifier)
        {
            identifier = tk.Text;
            pos = GetTkPos();
            tk = GetToken();
        }

        if (tk.Type == Tk.Cursor)
        {
            completionType = pType;
            completionLine = tkLine;
            completionBlock = pBlock;

            pos = GetTkPos();
            tk = GetToken();

            if (tk.Type == Tk.Identifier)
            {
                identifier = identifier + tk.Text;
            }
            else
            {
                SetTkPos(pos);
            }
            return true;
        }
        else if (identifier != "")
        {
            SetTkPos(pos);
        }

        return false;
    }

    private static bool HasBuiltin(Dictionary<string, FunctionInfo> pFunctions, string pName, bool pCheckGlobalFuncs = false)
    {
        if (pCheckGlobalFuncs && globalFuncSet.Contains(pName))
        {
            return true;
        }

        foreach (KeyValuePair<string, FunctionInfo> E in pFunctions)
        {
            if (E.Value.BuiltIns.ContainsKey(pName))
            {
                return true;
            }
        }

        return false;
    }
}
