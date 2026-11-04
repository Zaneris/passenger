namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's reading of a shader's top level (shader_language.cpp, MIT): its type, render and stencil modes, uniforms,
/// varyings, constants, structs and functions, each as Godot checks it.</summary>
public sealed partial class GodotShaderLanguage
{
    private Err ParseShader(Dictionary<string, FunctionInfo> pFunctions, List<ModeInfo> pRenderModes, List<ModeInfo> pStencilModes, HashSet<string> pShaderTypes)
    {
        Token tk;
        TkPos prevPos;

        if (!isShaderInc)
        {
            tk = GetToken();

            if (tk.Type != Tk.ShaderType)
            {
                SetError(Format(("Expected '%s' at the beginning of shader. Valid types are: %s."), "shader_type", GetShaderTypeList(pShaderTypes)));
                return Err.ParseError;
            }

            GetCompletableIdentifier(null, CompletionType.ShaderType, out shaderTypeIdentifier);
            if (shaderTypeIdentifier == "")
            {
                SetError(Format(("Expected an identifier after '%s', indicating the type of shader. Valid types are: %s."), "shader_type", GetShaderTypeList(pShaderTypes)));
                return Err.ParseError;
            }
            if (!pShaderTypes.Contains(shaderTypeIdentifier))
            {
                SetError(Format(("Invalid shader type. Valid types are: %s"), GetShaderTypeList(pShaderTypes)));
                return Err.ParseError;
            }
            prevPos = GetTkPos();
            tk = GetToken();

            if (tk.Type != Tk.Semicolon)
            {
                SetTkPos(prevPos);
                SetExpectedAfterError(";", "shader_type " + (shaderTypeIdentifier));
                return Err.ParseError;
            }
        }

        tk = GetToken();

        int textureUniforms = 0;
        int textureBinding = 0;
        int uniforms = 0;
        int instanceIndex = 0;
        int propIndex = 0;
        ulong uniformBufferSize = 0;
        ulong maxUniformBufferSize = 65536;
        int uniformBufferExceededLine = -1;
        bool checkDeviceLimitWarnings = checkWarnings && HasWarning(GodotShaderWarningFlags.DeviceLimitExceeded);
        UniformScope uniformScope = UniformScope.Local;

        stages = pFunctions;

        isDiscardSupported = shaderTypeIdentifier == "canvas_item" || shaderTypeIdentifier == "spatial" || shaderTypeIdentifier == "";
        isSupportedFragOnlyFuncs = isDiscardSupported || shaderTypeIdentifier == "sky" || shaderTypeIdentifier == "";

        FunctionInfo constants = pFunctions.ContainsKey("constants") ? pFunctions["constants"] : new FunctionInfo();

        Dictionary<string, string> definedRenderModes = new();
        Dictionary<string, string> definedStencilModes = new();

        while (tk.Type != Tk.Eof)
        {
            switch (tk.Type)
            {
                case Tk.RenderMode:
                    {
                        while (true)
                        {
                            if (ParseShaderMode(false, pRenderModes, definedRenderModes) != Err.Ok)
                            {
                                return Err.ParseError;
                            }

                            tk = GetToken();

                            if (tk.Type == Tk.Comma)
                            {
                                // All good, do nothing.
                            }
                            else if (tk.Type == Tk.Semicolon)
                            {
                                break; // Done.
                            }
                            else
                            {
                                SetError(Format(("Unexpected token: '%s'."), GetTokenText(tk)));
                                return Err.ParseError;
                            }
                        }
                    }
                    break;
                case Tk.StencilMode:
                    {
                        while (true)
                        {
                            TkPos pos = GetTkPos();
                            tk = GetToken();

                            if (tk.IsIntegerConstant)
                            {
                                int referenceValue = (int)tk.Constant;

                                if (shader.StencilReference != -1)
                                {
                                    SetError(Format(("Duplicated stencil mode reference value: '%s'."), referenceValue));
                                    return Err.ParseError;
                                }

                                if (referenceValue < 0)
                                {
                                    SetError(Format(("Stencil mode reference value cannot be negative: '%s'."), referenceValue));
                                    return Err.ParseError;
                                }

                                if (referenceValue > 255)
                                {
                                    SetError(Format(("Stencil mode reference value cannot be greater than 255: '%s'."), referenceValue));
                                    return Err.ParseError;
                                }

                                shader.StencilReference = referenceValue;
                            }
                            else
                            {
                                SetTkPos(pos);

                                if (ParseShaderMode(true, pStencilModes, definedStencilModes) != Err.Ok)
                                {
                                    return Err.ParseError;
                                }
                            }

                            tk = GetToken();

                            if (tk.Type == Tk.Comma)
                            {
                                //all good, do nothing
                            }
                            else if (tk.Type == Tk.Semicolon)
                            {
                                break; //done
                            }
                            else
                            {
                                SetError(Format(("Unexpected token: '%s'."), GetTokenText(tk)));
                                return Err.ParseError;
                            }
                        }
                    }
                    break;
                case Tk.Struct:
                    {
                        ShaderStructDecl st = new();
                        DataType type;
                        tk = GetToken();
                        if (tk.Type == Tk.Identifier)
                        {
                            st.Name = tk.Text;
                            if (shader.Functions.ContainsKey(st.Name) || shader.Constants.ContainsKey(st.Name) || shader.Structs.ContainsKey(st.Name))
                            {
                                SetRedefinitionError((st.Name));
                                return Err.ParseError;
                            }
                            tk = GetToken();
                            if (tk.Type != Tk.CurlyBracketOpen)
                            {
                                SetExpectedError("{");
                                return Err.ParseError;
                            }
                        }
                        else
                        {
                            SetError(("Expected a struct identifier."));
                            return Err.ParseError;
                        }

                        StructNode stNode = new StructNode();
                        st.ShaderStruct = stNode;

                        int memberCount = 0;
                        HashSet<string> memberNames = [];

                        while (true)
                        { // variables list

                            tk = GetToken();
                            if (tk.Type == Tk.CurlyBracketClose)
                            {
                                break;
                            }
                            string structName = "";
                            bool structDt = false;
                            DataPrecision precision = DataPrecision.Default;

                            if (tk.Type == Tk.Struct)
                            {
                                SetError(("Nested structs are not allowed."));
                                return Err.ParseError;
                            }

                            if (IsTokenPrecision(tk.Type))
                            {
                                precision = GetTokenPrecision(tk.Type);
                                tk = GetToken();
                            }

                            if (shader.Structs.ContainsKey(tk.Text))
                            {
                                structName = tk.Text;
                                if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedStruct) && usedStructs.ContainsKey(structName))
                                {
                                    usedStructs[structName].Used = true;
                                }
                                structDt = true;
                            }

                            if (!IsTokenDatatype(tk.Type) && !structDt)
                            {
                                SetError(("Expected data type."));
                                return Err.ParseError;
                            }
                            else
                            {
                                type = structDt ? DataType.Struct : GetTokenDatatype(tk.Type);

                                if (precision != DataPrecision.Default && ValidatePrecision(type, precision) != Err.Ok)
                                {
                                    return Err.ParseError;
                                }

                                if (type == DataType.Void || IsSamplerType(type))
                                {
                                    SetError(Format(("A '%s' data type is not allowed here."), GetDatatypeName(type)));
                                    return Err.ParseError;
                                }

                                bool first = true;
                                bool fixedArraySize = false;
                                int arraySize = 0;

                                do
                                {
                                    tk = GetToken();

                                    if (first)
                                    {
                                        first = false;

                                        if (tk.Type != Tk.Identifier && tk.Type != Tk.BracketOpen)
                                        {
                                            SetError(("Expected an identifier or '['."));
                                            return Err.ParseError;
                                        }

                                        if (tk.Type == Tk.BracketOpen)
                                        {
                                            if (ParseArraySize(null, constants, true, ref arraySize) != Err.Ok)
                                            {
                                                return Err.ParseError;
                                            }
                                            fixedArraySize = true;
                                            tk = GetToken();
                                        }
                                    }

                                    if (tk.Type != Tk.Identifier)
                                    {
                                        SetError(("Expected an identifier."));
                                        return Err.ParseError;
                                    }

                                    MemberNode member = new MemberNode();
                                    member.Precision = precision;
                                    member.Datatype = type;
                                    member.StructName = structName;
                                    member.Name = tk.Text;
                                    member.ArraySize = arraySize;

                                    if (memberNames.Contains(member.Name))
                                    {
                                        SetRedefinitionError((member.Name));
                                        return Err.ParseError;
                                    }
                                    memberNames.Add(member.Name);
                                    tk = GetToken();

                                    if (tk.Type == Tk.BracketOpen)
                                    {
                                        int sizeRead = member.ArraySize;
                                        if (ParseArraySize(null, constants, true, ref sizeRead) != Err.Ok)
                                        {
                                            return Err.ParseError;
                                        }
                                        member.ArraySize = sizeRead;
                                        tk = GetToken();
                                    }

                                    if (!fixedArraySize)
                                    {
                                        arraySize = 0;
                                    }

                                    if (tk.Type != Tk.Semicolon && tk.Type != Tk.Comma)
                                    {
                                        SetExpectedError(",", ";");
                                        return Err.ParseError;
                                    }

                                    stNode.Members.Add(member);
                                    memberCount++;
                                } while (tk.Type == Tk.Comma); // another member
                            }
                        }
                        if (memberCount == 0)
                        {
                            SetError(("Empty structs are not allowed."));
                            return Err.ParseError;
                        }

                        tk = GetToken();
                        if (tk.Type != Tk.Semicolon)
                        {
                            SetExpectedError(";");
                            return Err.ParseError;
                        }

                        shader.Structs[st.Name] = st;
                        shader.Vstructs.Add(st); // struct's order is important!
                        if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedStruct))
                        {
                            usedStructs[st.Name] = new Usage(tkLine);
                        }
                    }
                    break;
                case Tk.Global:
                    {
                        tk = GetToken();
                        if (tk.Type != Tk.Uniform)
                        {
                            SetExpectedAfterError("uniform", "global");
                            return Err.ParseError;
                        }
                        uniformScope = UniformScope.Global;
                    }
                    goto case Tk.Instance;
                case Tk.Instance:
                    {
                        if (tk.Type == Tk.Instance)
                        {
                            if (shaderTypeIdentifier != "" && (shaderTypeIdentifier) != "spatial" && (shaderTypeIdentifier) != "canvas_item")
                            {
                                SetError(Format(("Uniform instances are not yet implemented for '%s' shaders."), shaderTypeIdentifier));
                                return Err.ParseError;
                            }
                            if (uniformScope == UniformScope.Local)
                            {
                                tk = GetToken();
                                if (tk.Type != Tk.Uniform)
                                {
                                    SetExpectedAfterError("uniform", "instance");
                                    return Err.ParseError;
                                }
                                uniformScope = UniformScope.Instance;
                            }
                        }
                    }
                    goto case Tk.Uniform;
                case Tk.Uniform:
                case Tk.Varying:
                    {
                        bool isUniform = tk.Type == Tk.Uniform;
                        if (!isUniform)
                        {
                            if (shaderTypeIdentifier == "particles" || shaderTypeIdentifier == "sky" || shaderTypeIdentifier == "fog")
                            {
                                SetError(Format(("Varyings cannot be used in '%s' shaders."), shaderTypeIdentifier));
                                return Err.ParseError;
                            }
                        }
                        DataPrecision precision = DataPrecision.Default;
                        DataInterpolation interpolation = DataInterpolation.Default;
                        DataType type;
                        string name = "";
                        int arraySize = 0;

                        tk = GetToken();
                        bool tempError = false;

                        if (IsTokenInterpolation(tk.Type))
                        {
                            if (isUniform)
                            {
                                SetError(("Interpolation qualifiers are not supported for uniforms."));
                                tempError = true;
                            }
                            interpolation = GetTokenInterpolation(tk.Type);
                            tk = GetToken();
                            if (tempError)
                            {
                                return Err.ParseError;
                            }
                        }

                        if (IsTokenPrecision(tk.Type))
                        {
                            precision = GetTokenPrecision(tk.Type);
                            tk = GetToken();
                        }

                        if (shader.Structs.ContainsKey(tk.Text))
                        {
                            if (isUniform)
                            {
                                SetError(Format(("The '%s' data type is not supported for uniforms."), "struct"));
                                return Err.ParseError;
                            }
                            else
                            {
                                SetError(Format(("The '%s' data type is not allowed here."), "struct"));
                                return Err.ParseError;
                            }
                        }

                        if (!IsTokenDatatype(tk.Type))
                        {
                            SetError(("Expected data type."));
                            return Err.ParseError;
                        }

                        type = GetTokenDatatype(tk.Type);

                        if (precision != DataPrecision.Default && ValidatePrecision(type, precision) != Err.Ok)
                        {
                            return Err.ParseError;
                        }

                        if (type == DataType.Void)
                        {
                            SetError(Format(("The '%s' data type is not allowed here."), "void"));
                            return Err.ParseError;
                        }

                        if (!isUniform && interpolation != DataInterpolation.Default && type < DataType.Int)
                        {
                            SetError(Format(("Interpolation modifier '%s' cannot be used with boolean types."), GetInterpolationName(interpolation)));
                            return Err.ParseError;
                        }

                        if (!isUniform && type > DataType.Mat4)
                        {
                            SetError(("Invalid data type for varying."));
                            return Err.ParseError;
                        }

                        tk = GetToken();

                        if (tk.Type != Tk.Identifier && tk.Type != Tk.BracketOpen)
                        {
                            SetError(("Expected an identifier or '['."));
                            return Err.ParseError;
                        }

                        if (tk.Type == Tk.BracketOpen)
                        {
                            if (ParseArraySize(null, constants, true, ref arraySize) != Err.Ok)
                            {
                                return Err.ParseError;
                            }
                            tk = GetToken();
                        }

                        if (tk.Type != Tk.Identifier)
                        {
                            SetError(("Expected an identifier."));
                            return Err.ParseError;
                        }

                        prevPos = GetTkPos();
                        name = tk.Text;

                        if (FindIdentifier(null, false, constants, name))
                        {
                            SetRedefinitionError((name));
                            return Err.ParseError;
                        }

                        if (HasBuiltin(pFunctions, name))
                        {
                            SetRedefinitionError((name));
                            return Err.ParseError;
                        }

                        if (isUniform)
                        {
                            if (uniformScope == UniformScope.Global && compileInfo.GlobalShaderUniformType is not null)
                            { // Type checking for global uniforms is not allowed outside the editor.
                              //validate global uniform
                                DataType gvtype = compileInfo.GlobalShaderUniformType(name);
                                if (gvtype == DataType.Max)
                                {
                                    SetError(Format(("Global uniform '%s' does not exist. Create it in Project Settings."), (name)));
                                    return Err.ParseError;
                                }

                                if (type != gvtype)
                                {
                                    SetError(Format(("Global uniform '%s' must be of type '%s'."), (name), GetDatatypeName(gvtype)));
                                    return Err.ParseError;
                                }
                            }
                            Uniform uniform = new();

                            uniform.Type = type;
                            uniform.Scope = uniformScope;
                            uniform.Precision = precision;
                            uniform.ArraySize = arraySize;
                            uniform.Group = currentUniformGroupName;

                            tk = GetToken();
                            if (tk.Type == Tk.BracketOpen)
                            {
                                int sizeRead = uniform.ArraySize;
                                if (ParseArraySize(null, constants, true, ref sizeRead) != Err.Ok)
                                {
                                    return Err.ParseError;
                                }
                                uniform.ArraySize = sizeRead;
                                tk = GetToken();
                            }

                            if (IsSamplerType(type))
                            {
                                if (uniformScope == UniformScope.Instance)
                                {
                                    SetError(Format(("The '%s' qualifier is not supported for sampler types."), "SCOPE_INSTANCE"));
                                    return Err.ParseError;
                                }
                                uniform.TextureOrder = textureUniforms++;
                                uniform.TextureBinding = textureBinding;
                                if (uniform.ArraySize > 0)
                                {
                                    textureBinding += uniform.ArraySize;
                                }
                                else
                                {
                                    ++textureBinding;
                                }
                                uniform.Order = -1;
                                uniform.PropOrder = propIndex++;
                            }
                            else
                            {
                                if (uniformScope == UniformScope.Instance && (type == DataType.Mat2 || type == DataType.Mat3 || type == DataType.Mat4))
                                {
                                    SetError(Format(("The '%s' qualifier is not supported for matrix types."), "SCOPE_INSTANCE"));
                                    return Err.ParseError;
                                }
                                uniform.TextureOrder = -1;
                                if (uniformScope != UniformScope.Instance)
                                {
                                    uniform.Order = uniforms++;
                                    uniform.PropOrder = propIndex++;
                                    if (checkDeviceLimitWarnings)
                                    {
                                        if (uniform.ArraySize > 0)
                                        {
                                            int size = (int)GetDatatypeSize(uniform.Type) * uniform.ArraySize;
                                            int m = (16 * uniform.ArraySize);
                                            if ((size % m) != 0)
                                            {
                                                size += m - (size % m);
                                            }
                                            uniformBufferSize += (ulong)size;
                                        }
                                        else
                                        {
                                            uniformBufferSize += GetDatatypeSize(uniform.Type);
                                        }

                                        if (uniformBufferExceededLine == -1 && uniformBufferSize > maxUniformBufferSize)
                                        {
                                            uniformBufferExceededLine = tkLine;
                                        }
                                    }
                                }
                            }

                            if (uniform.ArraySize > 0)
                            {
                                if (uniformScope == UniformScope.Global)
                                {
                                    SetError(Format(("The '%s' qualifier is not supported for uniform arrays."), "SCOPE_GLOBAL"));
                                    return Err.ParseError;
                                }
                                if (uniformScope == UniformScope.Instance)
                                {
                                    SetError(Format(("The '%s' qualifier is not supported for uniform arrays."), "SCOPE_INSTANCE"));
                                    return Err.ParseError;
                                }
                            }

                            int customInstanceIndex = -1;

                            if (tk.Type == Tk.Colon)
                            {
                                completionType = CompletionType.Hint;
                                completionBase = type;
                                completionBaseArray = uniform.ArraySize > 0;

                                //hint
                                do
                                {
                                    tk = GetToken();
                                    completionLine = tk.Line;

                                    if (!IsTokenHint(tk.Type))
                                    {
                                        SetError(("Expected valid type hint after ':'."));
                                        return Err.ParseError;
                                    }

                                    if (uniform.ArraySize > 0)
                                    {
                                        Tk[] supportedHints =
                                        [
                                            Tk.HintSourceColor, Tk.HintColorConversionDisabled, Tk.RepeatDisable, Tk.RepeatEnable,
                        Tk.FilterLinear, Tk.FilterLinearMipmap, Tk.FilterLinearMipmapAnisotropic,
                        Tk.FilterNearest, Tk.FilterNearestMipmap, Tk.FilterNearestMipmapAnisotropic,
                        Tk.HintNoStorage, Tk.HintNoEditor,
                    ];
                                        if (!supportedHints.Contains(tk.Type))
                                        {
                                            SetError(("This hint is not supported for uniform arrays."));
                                            return Err.ParseError;
                                        }
                                    }

                                    Hint newHint = Hint.None;
                                    TextureFilter newFilter = TextureFilter.Default;
                                    TextureRepeat newRepeat = TextureRepeat.Default;
                                    PropertyUsage unsetPropertyUsage = PropertyUsage.None;

                                    switch (tk.Type)
                                    {
                                        case Tk.HintSourceColor:
                                            {
                                                if (type != DataType.Vec3 && type != DataType.Vec4 && !IsSamplerType(type))
                                                {
                                                    SetError(Format(("Source color hint is for '%s', '%s' or sampler types only."), "vec3", "vec4"));
                                                    return Err.ParseError;
                                                }

                                                if (IsSamplerType(type))
                                                {
                                                    if (uniform.UseColor)
                                                    {
                                                        SetError(Format(("Duplicated hint: '%s'."), "source_color"));
                                                        return Err.ParseError;
                                                    }
                                                    uniform.UseColor = true;
                                                }
                                                else
                                                {
                                                    newHint = Hint.SourceColor;
                                                }
                                            }
                                            break;
                                        case Tk.HintColorConversionDisabled:
                                            {
                                                if (type != DataType.Vec3 && type != DataType.Vec4)
                                                {
                                                    SetError(Format(("Source color conversion disabled hint is for '%s', '%s'."), "vec3", "vec4"));
                                                    return Err.ParseError;
                                                }
                                                if (uniform.Hint != Hint.SourceColor)
                                                {
                                                    SetError(Format(("Hint '%s' should be preceded by '%s'."), "color_conversion_disabled", "source_color"));
                                                    return Err.ParseError;
                                                }
                                                uniform.Hint = Hint.None;
                                                newHint = Hint.ColorConversionDisabled;
                                            }
                                            break;
                                        case Tk.HintDefaultBlackTexture:
                                            {
                                                newHint = Hint.DefaultBlack;
                                            }
                                            break;
                                        case Tk.HintDefaultWhiteTexture:
                                            {
                                                newHint = Hint.DefaultWhite;
                                            }
                                            break;
                                        case Tk.HintDefaultTransparentTexture:
                                            {
                                                newHint = Hint.DefaultTransparent;
                                            }
                                            break;
                                        case Tk.HintNormalTexture:
                                            {
                                                newHint = Hint.Normal;
                                            }
                                            break;
                                        case Tk.HintRoughnessNormalTexture:
                                            {
                                                newHint = Hint.RoughnessNormal;
                                            }
                                            break;
                                        case Tk.HintRoughnessR:
                                            {
                                                newHint = Hint.RoughnessR;
                                            }
                                            break;
                                        case Tk.HintRoughnessG:
                                            {
                                                newHint = Hint.RoughnessG;
                                            }
                                            break;
                                        case Tk.HintRoughnessB:
                                            {
                                                newHint = Hint.RoughnessB;
                                            }
                                            break;
                                        case Tk.HintRoughnessA:
                                            {
                                                newHint = Hint.RoughnessA;
                                            }
                                            break;
                                        case Tk.HintRoughnessGray:
                                            {
                                                newHint = Hint.RoughnessGray;
                                            }
                                            break;
                                        case Tk.HintAnisotropyTexture:
                                            {
                                                newHint = Hint.Anisotropy;
                                            }
                                            break;
                                        case Tk.HintRange:
                                            {
                                                if (type != DataType.Float && type != DataType.Int)
                                                {
                                                    SetError(Format(("Range hint is for '%s' and '%s' only."), "float", "int"));
                                                    return Err.ParseError;
                                                }

                                                tk = GetToken();
                                                if (tk.Type != Tk.ParenthesisOpen)
                                                {
                                                    SetExpectedAfterError("(", "hint_range");
                                                    return Err.ParseError;
                                                }

                                                if (!ParseNumericConstantExpression(constants, ref uniform.HintRange[0]))
                                                {
                                                    SetError(("Expected a valid numeric expression."));
                                                    return Err.ParseError;
                                                }

                                                tk = GetToken();

                                                if (tk.Type != Tk.Comma)
                                                {
                                                    SetExpectedError(",");
                                                    return Err.ParseError;
                                                }

                                                if (!ParseNumericConstantExpression(constants, ref uniform.HintRange[1]))
                                                {
                                                    SetError(("Expected a valid numeric expression after ','."));
                                                    return Err.ParseError;
                                                }

                                                tk = GetToken();

                                                if (tk.Type == Tk.Comma)
                                                {
                                                    if (!ParseNumericConstantExpression(constants, ref uniform.HintRange[2]))
                                                    {
                                                        SetError(("Expected a valid numeric expression after ','."));
                                                        return Err.ParseError;
                                                    }
                                                    tk = GetToken();
                                                }
                                                else
                                                {
                                                    if (type == DataType.Int)
                                                    {
                                                        uniform.HintRange[2] = 1;
                                                    }
                                                    else
                                                    {
                                                        uniform.HintRange[2] = 0.001f;
                                                    }
                                                }

                                                if (tk.Type != Tk.ParenthesisClose)
                                                {
                                                    SetExpectedError(")");
                                                    return Err.ParseError;
                                                }

                                                newHint = Hint.Range;
                                            }
                                            break;
                                        case Tk.HintEnum:
                                            {
                                                if (type != DataType.Int)
                                                {
                                                    SetError(Format(("Enum hint is for '%s' only."), "int"));
                                                    return Err.ParseError;
                                                }

                                                tk = GetToken();
                                                if (tk.Type != Tk.ParenthesisOpen)
                                                {
                                                    SetExpectedAfterError("(", "hint_enum");
                                                    return Err.ParseError;
                                                }

                                                while (true)
                                                {
                                                    tk = GetToken();

                                                    if (tk.Type != Tk.StringConstant)
                                                    {
                                                        SetError(("Expected a string constant."));
                                                        return Err.ParseError;
                                                    }

                                                    uniform.HintEnumNames.Add(tk.Text);

                                                    tk = GetToken();

                                                    if (tk.Type == Tk.ParenthesisClose)
                                                    {
                                                        break;
                                                    }
                                                    else if (tk.Type != Tk.Comma)
                                                    {
                                                        SetError(("Expected ',' or ')' after string constant."));
                                                        return Err.ParseError;
                                                    }
                                                }

                                                newHint = Hint.Enum;
                                            }
                                            break;
                                        case Tk.HintNoStorage:
                                            {
                                                unsetPropertyUsage = PropertyUsage.Storage;
                                            }
                                            break;
                                        case Tk.HintNoEditor:
                                            {
                                                unsetPropertyUsage = PropertyUsage.Editor;
                                            }
                                            break;
                                        case Tk.HintInstanceIndex:
                                            {
                                                if (customInstanceIndex != -1)
                                                {
                                                    SetError(Format(("Can only specify '%s' once."), "instance_index"));
                                                    return Err.ParseError;
                                                }

                                                tk = GetToken();
                                                if (tk.Type != Tk.ParenthesisOpen)
                                                {
                                                    SetExpectedAfterError("(", "instance_index");
                                                    return Err.ParseError;
                                                }

                                                tk = GetToken();

                                                if (tk.Type == Tk.OpSub)
                                                {
                                                    SetError(("The instance index can't be negative."));
                                                    return Err.ParseError;
                                                }

                                                if (!tk.IsIntegerConstant)
                                                {
                                                    SetError(("Expected an integer constant."));
                                                    return Err.ParseError;
                                                }

                                                customInstanceIndex = (int)tk.Constant;

                                                if (customInstanceIndex >= MaxInstanceUniformIndices)
                                                {
                                                    SetError(Format(("Allowed instance uniform indices must be within [0..%d] range."), MaxInstanceUniformIndices - 1));
                                                    return Err.ParseError;
                                                }

                                                tk = GetToken();

                                                if (tk.Type != Tk.ParenthesisClose)
                                                {
                                                    SetExpectedError(")");
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.HintScreenTexture:
                                            {
                                                newHint = Hint.ScreenTexture;
                                                --textureUniforms;
                                                --textureBinding;
                                                if (shaderTypeIdentifier != "" && !((shaderTypeIdentifier) == "spatial" || (shaderTypeIdentifier) == "canvas_item"))
                                                {
                                                    SetError(Format(("'hint_screen_texture' is not supported in '%s' shaders."), shaderTypeIdentifier));
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.HintNormalRoughnessTexture:
                                            {
                                                newHint = Hint.NormalRoughnessTexture;
                                                --textureUniforms;
                                                --textureBinding;
                                                if (compileInfo.RenderingMethod != "forward_plus")
                                                {
                                                    SetError(("'hint_normal_roughness_texture' is only available when using the Forward+ renderer."));
                                                    return Err.ParseError;
                                                }
                                                if (shaderTypeIdentifier != "" && (shaderTypeIdentifier) != "spatial")
                                                {
                                                    SetError(Format(("'hint_normal_roughness_texture' is not supported in '%s' shaders."), shaderTypeIdentifier));
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.HintDepthTexture:
                                            {
                                                newHint = Hint.DepthTexture;
                                                --textureUniforms;
                                                --textureBinding;
                                                if (shaderTypeIdentifier != "" && (shaderTypeIdentifier) != "spatial")
                                                {
                                                    SetError(Format(("'hint_depth_texture' is not supported in '%s' shaders."), shaderTypeIdentifier));
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.HintBlitSource0:
                                            {
                                                newHint = Hint.BlitSource0;
                                                --textureUniforms;
                                                --textureBinding;
                                                if (shaderTypeIdentifier != "" && (shaderTypeIdentifier) != "texture_blit")
                                                {
                                                    SetError(Format(("'hint_blit_source' is not supported in '%s' shaders."), shaderTypeIdentifier));
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.HintBlitSource1:
                                            {
                                                newHint = Hint.BlitSource1;
                                                --textureUniforms;
                                                --textureBinding;
                                                if (shaderTypeIdentifier != "" && (shaderTypeIdentifier) != "texture_blit")
                                                {
                                                    SetError(Format(("'hint_blit_source' is not supported in '%s' shaders."), shaderTypeIdentifier));
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.HintBlitSource2:
                                            {
                                                newHint = Hint.BlitSource2;
                                                --textureUniforms;
                                                --textureBinding;
                                                if (shaderTypeIdentifier != "" && (shaderTypeIdentifier) != "texture_blit")
                                                {
                                                    SetError(Format(("'hint_blit_source' is not supported in '%s' shaders."), shaderTypeIdentifier));
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.HintBlitSource3:
                                            {
                                                newHint = Hint.BlitSource3;
                                                --textureUniforms;
                                                --textureBinding;
                                                if (shaderTypeIdentifier != "" && (shaderTypeIdentifier) != "texture_blit")
                                                {
                                                    SetError(Format(("'hint_blit_source' is not supported in '%s' shaders."), shaderTypeIdentifier));
                                                    return Err.ParseError;
                                                }
                                            }
                                            break;
                                        case Tk.FilterNearest:
                                            {
                                                newFilter = TextureFilter.Nearest;
                                            }
                                            break;
                                        case Tk.FilterLinear:
                                            {
                                                newFilter = TextureFilter.Linear;
                                            }
                                            break;
                                        case Tk.FilterNearestMipmap:
                                            {
                                                newFilter = TextureFilter.NearestMipmap;
                                            }
                                            break;
                                        case Tk.FilterLinearMipmap:
                                            {
                                                newFilter = TextureFilter.LinearMipmap;
                                            }
                                            break;
                                        case Tk.FilterNearestMipmapAnisotropic:
                                            {
                                                newFilter = TextureFilter.NearestMipmapAnisotropic;
                                            }
                                            break;
                                        case Tk.FilterLinearMipmapAnisotropic:
                                            {
                                                newFilter = TextureFilter.LinearMipmapAnisotropic;
                                            }
                                            break;
                                        case Tk.RepeatDisable:
                                            {
                                                newRepeat = TextureRepeat.Disable;
                                            }
                                            break;
                                        case Tk.RepeatEnable:
                                            {
                                                newRepeat = TextureRepeat.Enable;
                                            }
                                            break;

                                        default:
                                            break;
                                    }

                                    bool isSamplerHint = newHint != Hint.None && newHint != Hint.SourceColor && newHint != Hint.ColorConversionDisabled && newHint != Hint.Range && newHint != Hint.Enum;
                                    if (((newFilter != TextureFilter.Default || newRepeat != TextureRepeat.Default) || isSamplerHint) && !IsSamplerType(type))
                                    {
                                        SetError(("This hint is only for sampler types."));
                                        return Err.ParseError;
                                    }

                                    if (newHint != Hint.None)
                                    {
                                        if (uniform.Hint != Hint.None)
                                        {
                                            if (uniform.Hint == newHint)
                                            {
                                                SetError(Format(("Duplicated hint: '%s'."), GetUniformHintName(newHint)));
                                            }
                                            else
                                            {
                                                SetError(Format(("Redefinition of hint: '%s'. The hint has already been set to '%s'."), GetUniformHintName(newHint), GetUniformHintName(uniform.Hint)));
                                            }
                                            return Err.ParseError;
                                        }
                                        else
                                        {
                                            uniform.Hint = newHint;
                                            currentUniformHint = newHint;
                                        }
                                    }

                                    if (unsetPropertyUsage != PropertyUsage.None)
                                    {
                                        if ((uniform.PropertyUsage & unsetPropertyUsage) == 0)
                                        {
                                            SetError(Format(("Duplicated property usage flag '%s'."), GetUnsetPropertyUsageName(unsetPropertyUsage)));
                                            return Err.ParseError;
                                        }
                                        else
                                        {
                                            uniform.PropertyUsage &= ~unsetPropertyUsage;
                                            currentPropertyUsage = uniform.PropertyUsage;
                                        }
                                    }

                                    if (newFilter != TextureFilter.Default)
                                    {
                                        if (uniform.Filter != TextureFilter.Default)
                                        {
                                            if (uniform.Filter == newFilter)
                                            {
                                                SetError(Format(("Duplicated filter mode: '%s'."), GetTextureFilterName(newFilter)));
                                            }
                                            else
                                            {
                                                SetError(Format(("Redefinition of filter mode: '%s'. The filter mode has already been set to '%s'."), GetTextureFilterName(newFilter), GetTextureFilterName(uniform.Filter)));
                                            }
                                            return Err.ParseError;
                                        }
                                        else
                                        {
                                            uniform.Filter = newFilter;
                                            currentUniformFilter = newFilter;
                                        }
                                    }

                                    if (newRepeat != TextureRepeat.Default)
                                    {
                                        if (uniform.Repeat != TextureRepeat.Default)
                                        {
                                            if (uniform.Repeat == newRepeat)
                                            {
                                                SetError(Format(("Duplicated repeat mode: '%s'."), GetTextureRepeatName(newRepeat)));
                                            }
                                            else
                                            {
                                                SetError(Format(("Redefinition of repeat mode: '%s'. The repeat mode has already been set to '%s'."), GetTextureRepeatName(newRepeat), GetTextureRepeatName(uniform.Repeat)));
                                            }
                                            return Err.ParseError;
                                        }
                                        else
                                        {
                                            uniform.Repeat = newRepeat;
                                            currentUniformRepeat = newRepeat;
                                        }
                                    }

                                    tk = GetToken();

                                } while (tk.Type == Tk.Comma);
                            }

                            if (uniformScope == UniformScope.Instance)
                            {
                                if (customInstanceIndex >= 0)
                                {
                                    uniform.InstanceIndex = customInstanceIndex;
                                }
                                else
                                {
                                    uniform.InstanceIndex = instanceIndex++;
                                    if (instanceIndex > MaxInstanceUniformIndices)
                                    {
                                        SetError(Format(("Too many '%s' uniforms in shader, maximum supported is %d."), "instance", MaxInstanceUniformIndices));
                                        return Err.ParseError;
                                    }
                                }
                            }

                            //reset scope for next uniform

                            if (tk.Type == Tk.OpAssign)
                            {
                                if (uniform.ArraySize > 0)
                                {
                                    SetError(("Setting default values to uniform arrays is not supported."));
                                    return Err.ParseError;
                                }

                                Node? expr = ParseAndReduceExpression(null, constants);
                                if (expr == null)
                                {
                                    return Err.ParseError;
                                }
                                if (expr.Type != NodeType.Constant)
                                {
                                    SetError(("Expected constant expression after '='."));
                                    return Err.ParseError;
                                }

                                ConstantNode cn = ((ConstantNode)expr);

                                uniform.DefaultValue = [.. new Scalar[cn.Values.Count]];

                                if (!ConvertConstant(cn, uniform.Type, uniform.DefaultValue))
                                {
                                    SetError(Format(("Can't convert constant to '%s'."), GetDatatypeName(uniform.Type)));
                                    return Err.ParseError;
                                }
                                tk = GetToken();
                            }

                            shader.Uniforms[name] = uniform;
                            if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedUniform))
                            {
                                usedUniforms[name] = new Usage(tkLine);
                            }

                            //reset scope for next uniform
                            uniformScope = UniformScope.Local;

                            if (tk.Type != Tk.Semicolon)
                            {
                                SetExpectedError(";");
                                return Err.ParseError;
                            }

                            completionType = CompletionType.None;

                            currentUniformHint = Hint.None;
                            currentUniformFilter = TextureFilter.Default;
                            currentUniformRepeat = TextureRepeat.Default;
                            currentPropertyUsage = PropertyUsage.Default;
                        }
                        else
                        { // varying
                            Varying varying = new();
                            varying.Type = type;
                            varying.Precision = precision;
                            varying.Interpolation = interpolation;
                            varying.Tkpos = prevPos;
                            varying.ArraySize = arraySize;

                            tk = GetToken();
                            if (tk.Type != Tk.Semicolon && tk.Type != Tk.BracketOpen)
                            {
                                if (arraySize == 0)
                                {
                                    SetExpectedError(";", "[");
                                }
                                else
                                {
                                    SetExpectedError(";");
                                }
                                return Err.ParseError;
                            }

                            if (tk.Type == Tk.BracketOpen)
                            {
                                int sizeRead = varying.ArraySize;
                                if (ParseArraySize(null, constants, true, ref sizeRead) != Err.Ok)
                                {
                                    return Err.ParseError;
                                }
                                varying.ArraySize = sizeRead;
                                tk = GetToken();
                            }

                            shader.Varyings[name] = varying;
                            if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedVarying))
                            {
                                usedVaryings[name] = new Usage(tkLine);
                            }
                        }

                    }
                    break;

                case Tk.UniformGroup:
                    {
                        tk = GetToken();
                        if (tk.Type == Tk.Identifier)
                        {
                            currentUniformGroupName = tk.Text;

                            tk = GetToken();
                            while (tk.Type == Tk.Period)
                            {
                                tk = GetToken();

                                if (tk.Type == Tk.Identifier)
                                {
                                    currentUniformGroupName += "/" + tk.Text;
                                }
                                else
                                {
                                    SetError(("Expected an uniform subgroup identifier."));
                                    return Err.ParseError;
                                }

                                tk = GetToken();
                            }

                            if (tk.Type != Tk.Semicolon)
                            {
                                SetExpectedError(";", ".");
                                return Err.ParseError;
                            }
                        }
                        else
                        {
                            if (tk.Type != Tk.Semicolon)
                            {
                                if (currentUniformGroupName.Length == 0)
                                {
                                    SetError(("Expected an uniform group identifier."));
                                }
                                else
                                {
                                    SetError(("Expected an uniform group identifier or `;`."));
                                }
                                return Err.ParseError;
                            }
                            else if (currentUniformGroupName.Length == 0)
                            {
                                SetError(("Group needs to be opened before."));
                                return Err.ParseError;
                            }
                            else
                            {
                                currentUniformGroupName = "";
                            }
                        }
                    }
                    break;
                case Tk.ShaderType:
                    {
                        SetError(("Shader type is already defined."));
                        return Err.ParseError;
                    }
                default:
                    {
                        //function or constant variable

                        bool isConstant = false;
                        bool isStruct = false;
                        string structName = "";
                        DataPrecision precision = DataPrecision.Default;
                        DataType type;
                        string name = "";
                        int arraySize = 0;

                        if (tk.Type == Tk.Const)
                        {
                            isConstant = true;
                            tk = GetToken();
                        }

                        if (IsTokenPrecision(tk.Type))
                        {
                            precision = GetTokenPrecision(tk.Type);
                            tk = GetToken();
                        }

                        if (shader.Structs.ContainsKey(tk.Text))
                        {
                            isStruct = true;
                            structName = tk.Text;
                        }
                        else
                        {
                            if (!IsTokenDatatype(tk.Type))
                            {
                                SetError(("Expected constant, function, uniform or varying."));
                                return Err.ParseError;
                            }

                            if (!IsTokenVariableDatatype(tk.Type))
                            {
                                if (isConstant)
                                {
                                    SetError(("Invalid constant type (samplers are not allowed)."));
                                }
                                else
                                {
                                    SetError(("Invalid function type (samplers are not allowed)."));
                                }
                                return Err.ParseError;
                            }
                        }

                        if (isStruct)
                        {
                            type = DataType.Struct;
                        }
                        else
                        {
                            type = GetTokenDatatype(tk.Type);
                        }

                        if (precision != DataPrecision.Default && ValidatePrecision(type, precision) != Err.Ok)
                        {
                            return Err.ParseError;
                        }

                        prevPos = GetTkPos();
                        tk = GetToken();


                        bool unknownSize = false;
                        bool fixedArraySize = false;

                        if (tk.Type == Tk.BracketOpen)
                        {
                            if (ParseArraySize(null, constants, !isConstant, ref arraySize, ref unknownSize, out _) != Err.Ok)
                            {
                                return Err.ParseError;
                            }
                            fixedArraySize = true;
                            prevPos = GetTkPos();
                        }

                        SetTkPos(prevPos);

                        GetCompletableIdentifier(null, CompletionType.MainFunction, out name);

                        if (name == "")
                        {
                            if (isConstant)
                            {
                                SetError(("Expected an identifier or '[' after type."));
                            }
                            else
                            {
                                SetError(("Expected a function name after type."));
                            }
                            return Err.ParseError;
                        }

                        IdentifierFound found = new();
                        if (shader.Structs.ContainsKey(name) || (FindIdentifier(null, false, constants, name, found) && found.Type != IdentifierType.Function) || HasBuiltin(pFunctions, name, !isConstant))
                        {
                            SetRedefinitionError((name));
                            return Err.ParseError;
                        }

                        tk = GetToken();
                        if (tk.Type != Tk.ParenthesisOpen)
                        {
                            if (type == DataType.Void)
                            {
                                SetError(("Expected '(' after function identifier."));
                                return Err.ParseError;
                            }

                            //variable
                            while (true)
                            {
                                ShaderConstant constant = new();
                                constant.Name = name;
                                constant.Type = isStruct ? DataType.Struct : type;
                                constant.StructName = structName;
                                constant.Precision = precision;
                                constant.Initializer = null;
                                constant.ArraySize = arraySize;
                                isConstDecl = true;

                                if (tk.Type == Tk.BracketOpen)
                                {
                                    int sizeRead = constant.ArraySize;
                                    if (ParseArraySize(null, constants, false, ref sizeRead, ref unknownSize, out _) != Err.Ok)
                                    {
                                        return Err.ParseError;
                                    }
                                    constant.ArraySize = sizeRead;
                                    tk = GetToken();
                                }

                                if (tk.Type == Tk.OpAssign)
                                {
                                    if (!isConstant)
                                    {
                                        SetError(Format(("Global non-constant variables are not supported. Expected '%s' keyword before constant definition."), "const"));
                                        return Err.ParseError;
                                    }

                                    if (constant.ArraySize > 0 || unknownSize)
                                    {
                                        bool fullDef = false;

                                        Declaration decl = new();
                                        decl.Name = name;
                                        decl.Size = (uint)(constant.ArraySize);

                                        tk = GetToken();

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

                                            string structName2 = "";
                                            DataType type2;

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

                                            int arraySize2 = 0;
                                            tk = GetToken();

                                            if (tk.Type == Tk.BracketOpen)
                                            {
                                                bool isUnknownSize = false;
                                                if (ParseArraySize(null, constants, false, ref arraySize2, ref isUnknownSize, out _) != Err.Ok)
                                                {
                                                    return Err.ParseError;
                                                }
                                                if (isUnknownSize)
                                                {
                                                    arraySize2 = constant.ArraySize;
                                                }
                                                tk = GetToken();
                                            }
                                            else
                                            {
                                                SetExpectedError("[");
                                                return Err.ParseError;
                                            }

                                            if (constant.Precision != precision2 || constant.Type != type2 || structName != structName2 || constant.ArraySize != arraySize2)
                                            {
                                                string from = "";
                                                if (type2 == DataType.Struct)
                                                {
                                                    from += structName2;
                                                }
                                                else
                                                {
                                                    if (precision2 != DataPrecision.Default)
                                                    {
                                                        from += GetPrecisionName(precision2);
                                                        from += " ";
                                                    }
                                                    from += GetDatatypeName(type2);
                                                }
                                                from += "[";
                                                from += Itos(arraySize2);
                                                from += "]'";

                                                string to = "";
                                                if (type == DataType.Struct)
                                                {
                                                    to += structName;
                                                }
                                                else
                                                {
                                                    if (precision != DataPrecision.Default)
                                                    {
                                                        to += GetPrecisionName(precision);
                                                        to += " ";
                                                    }
                                                    to += GetDatatypeName(type);
                                                }
                                                to += "[";
                                                to += Itos(constant.ArraySize);
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
                                                Node? n = ParseAndReduceExpression(null, constants);
                                                if (n == null)
                                                {
                                                    return Err.ParseError;
                                                }

                                                if (n.Type == NodeType.Operator && ((OperatorNode)n).Op == Operator.Call)
                                                {
                                                    SetError(("Expected constant expression."));
                                                    return Err.ParseError;
                                                }

                                                if (!CompareDatatypes(constant.Type, structName, 0, n.GetDatatype(), n.GetDatatypeName(), 0))
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
                                                constant.ArraySize = decl.Initializer.Count;
                                            }
                                            else if (decl.Initializer.Count != constant.ArraySize)
                                            {
                                                SetError(Format(("Array size mismatch. Expected %d elements (found %d)."), constant.ArraySize, decl.Initializer.Count));
                                                return Err.ParseError;
                                            }
                                        }
                                        else
                                        {
                                            SetExpectedError("(");
                                            return Err.ParseError;
                                        }

                                        arraySize = constant.ArraySize;

                                        ConstantNode expr = new ConstantNode();

                                        expr.Datatype = constant.Type;

                                        expr.StructName = constant.StructName;

                                        expr.ArraySize = constant.ArraySize;

                                        expr.ArrayDeclarations.Add(decl);

                                        constant.Initializer = ((ConstantNode)expr);
                                    }
                                    else
                                    {

                                        //variable created with assignment! must parse an expression
                                        Node? expr = ParseAndReduceExpression(null, constants);
                                        if (expr == null)
                                        {
                                            return Err.ParseError;
                                        }
                                        if (expr.Type == NodeType.Operator && ((OperatorNode)expr).Op == Operator.Call)
                                        {
                                            OperatorNode op = ((OperatorNode)expr);
                                            for (int i = 1; i < op.Arguments.Count; i++)
                                            {
                                                if (!CheckNodeConstness(op.Arguments[i]))
                                                {
                                                    SetError(Format(("Expected constant expression for argument %d of function call after '='."), i - 1));
                                                    return Err.ParseError;
                                                }
                                            }
                                        }

                                        constant.Initializer = expr;

                                        if (!CompareDatatypes(type, structName, 0, expr.GetDatatype(), expr.GetDatatypeName(), expr.GetArraySize()))
                                        {
                                            return Err.ParseError;
                                        }
                                    }
                                    tk = GetToken();
                                }
                                else
                                {
                                    if (constant.ArraySize > 0 || unknownSize)
                                    {
                                        SetError(("Expected array initialization."));
                                        return Err.ParseError;
                                    }
                                    else
                                    {
                                        SetError(("Expected initialization of constant."));
                                        return Err.ParseError;
                                    }
                                }

                                shader.Constants[name] = constant;
                                shader.Vconstants.Add(constant);
                                if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedConstant))
                                {
                                    usedConstants[name] = new Usage(tkLine);
                                }

                                if (tk.Type == Tk.Comma)
                                {
                                    tk = GetToken();
                                    if (tk.Type != Tk.Identifier)
                                    {
                                        SetError(("Expected an identifier after type."));
                                        return Err.ParseError;
                                    }

                                    name = tk.Text;
                                    if (FindIdentifier(null, false, constants, name))
                                    {
                                        SetRedefinitionError((name));
                                        return Err.ParseError;
                                    }

                                    if (HasBuiltin(pFunctions, name))
                                    {
                                        SetRedefinitionError((name));
                                        return Err.ParseError;
                                    }

                                    tk = GetToken();

                                    if (!fixedArraySize)
                                    {
                                        arraySize = 0;
                                    }
                                    unknownSize = false;

                                }
                                else if (tk.Type == Tk.Semicolon)
                                {
                                    isConstDecl = false;
                                    break;
                                }
                                else
                                {
                                    SetExpectedError(",", ";");
                                    return Err.ParseError;
                                }
                            }

                            break;
                        }

                        if (isConstant)
                        {
                            SetError(Format(("'%s' qualifier cannot be used with a function return type."), "const"));
                            return Err.ParseError;
                        }

                        FunctionInfo builtins = new();
                        if (pFunctions.ContainsKey(name))
                        {
                            builtins = pFunctions[name];
                        }

                        if (pFunctions.ContainsKey("global"))
                        { // Adds global variables: 'TIME'
                            foreach (KeyValuePair<string, BuiltInInfo> E in pFunctions["global"].BuiltIns)
                            {
                                builtins.BuiltIns[E.Key] = E.Value;
                            }
                        }

                        if (pFunctions.ContainsKey("constants"))
                        { // Adds global constants: 'PI', 'TAU', 'E'
                            foreach (KeyValuePair<string, BuiltInInfo> E in pFunctions["constants"].BuiltIns)
                            {
                                builtins.BuiltIns[E.Key] = E.Value;
                            }
                        }

                        for (int i = 0; i < shader.Vfunctions.Count; i++)
                        {
                            if (!shader.Vfunctions[i].Callable && shader.Vfunctions[i].Name == name)
                            {
                                SetRedefinitionError((name));
                                return Err.ParseError;
                            }
                        }

                        ShaderFunction function = new();

                        function.Callable = !pFunctions.ContainsKey(name);
                        function.Name = name;
                        function.Rname = name;

                        FunctionNode funcNode = new FunctionNode();
                        function.Function = funcNode;

                        funcNode.Name = name;
                        funcNode.Rname = name;
                        funcNode.ReturnType = type;
                        funcNode.ReturnStructName = structName;
                        funcNode.ReturnPrecision = precision;
                        funcNode.ReturnArraySize = arraySize;

                        if (pFunctions.ContainsKey(name))
                        {
                            funcNode.CanDiscard = pFunctions[name].CanDiscard;
                        }
                        else
                        {
                            funcNode.CanDiscard = isDiscardSupported; // Allow use it for custom functions (in supported shader types).
                        }

                        if (!functionOverloadCount.ContainsKey(name))
                        {
                            functionOverloadCount[name] = 0;
                        }
                        else
                        {
                            functionOverloadCount[name]++;
                        }

                        funcNode.Body = new BlockNode();
                        funcNode.Body.ParentFunction = funcNode;

                        tk = GetToken();

                        while (true)
                        {
                            if (tk.Type == Tk.ParenthesisClose)
                            {
                                break;
                            }


                            bool paramIsConst = false;
                            if (tk.Type == Tk.Const)
                            {
                                paramIsConst = true;
                                tk = GetToken();

                            }

                            ArgumentQualifier paramQualifier = ArgumentQualifier.In;
                            if (IsTokenArgQual(tk.Type))
                            {
                                bool error = false;
                                switch (tk.Type)
                                {
                                    case Tk.ArgIn:
                                        {
                                            paramQualifier = ArgumentQualifier.In;
                                        }
                                        break;
                                    case Tk.ArgOut:
                                        {
                                            if (paramIsConst)
                                            {
                                                SetError(Format(("The '%s' qualifier cannot be used within a function parameter declared with '%s'."), "out", "const"));
                                                error = true;
                                            }
                                            paramQualifier = ArgumentQualifier.Out;
                                        }
                                        break;
                                    case Tk.ArgInout:
                                        {
                                            if (paramIsConst)
                                            {
                                                SetError(Format(("The '%s' qualifier cannot be used within a function parameter declared with '%s'."), "inout", "const"));
                                                error = true;
                                            }
                                            paramQualifier = ArgumentQualifier.Inout;
                                        }
                                        break;
                                    default:
                                        error = true;
                                        break;
                                }
                                tk = GetToken();

                                if (error)
                                {
                                    return Err.ParseError;
                                }
                            }

                            DataType paramType;
                            string paramName = "";
                            string paramStructName = "";
                            DataPrecision paramPrecision = DataPrecision.Default;
                            int argArraySize = 0;

                            if (IsTokenPrecision(tk.Type))
                            {
                                paramPrecision = GetTokenPrecision(tk.Type);
                                tk = GetToken();

                            }

                            isStruct = false;

                            if (shader.Structs.ContainsKey(tk.Text))
                            {
                                isStruct = true;
                                paramStructName = tk.Text;
                                if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedStruct) && usedStructs.ContainsKey(paramStructName))
                                {
                                    usedStructs[paramStructName].Used = true;
                                }
                            }

                            if (!isStruct && !IsTokenDatatype(tk.Type))
                            {
                                SetError(("Expected a valid data type for argument."));
                                return Err.ParseError;
                            }

                            if (paramQualifier == ArgumentQualifier.Out || paramQualifier == ArgumentQualifier.Inout)
                            {
                                if (IsSamplerType(GetTokenDatatype(tk.Type)))
                                {
                                    SetError(("Opaque types cannot be output parameters."));
                                    return Err.ParseError;
                                }
                            }

                            if (isStruct)
                            {
                                paramType = DataType.Struct;
                            }
                            else
                            {
                                paramType = GetTokenDatatype(tk.Type);
                                if (paramType == DataType.Void)
                                {
                                    SetError(("Void type not allowed as argument."));
                                    return Err.ParseError;
                                }
                            }

                            if (paramPrecision != DataPrecision.Default && ValidatePrecision(paramType, paramPrecision) != Err.Ok)
                            {
                                return Err.ParseError;
                            }
                            tk = GetToken();

                            if (tk.Type == Tk.BracketOpen)
                            {
                                if (ParseArraySize(null, constants, true, ref argArraySize) != Err.Ok)
                                {
                                    return Err.ParseError;
                                }
                                tk = GetToken();
                            }
                            if (tk.Type != Tk.Identifier)
                            {
                                SetError(("Expected an identifier for argument name."));
                                return Err.ParseError;
                            }

                            paramName = tk.Text;

                            IdentifierFound paramFound = new();
                            if (FindIdentifier(funcNode.Body, false, builtins, paramName, paramFound))
                            {
                                if (paramFound.Type != IdentifierType.Function)
                                {
                                    SetRedefinitionError((paramName));
                                    return Err.ParseError;
                                }
                            }

                            if (HasBuiltin(pFunctions, paramName))
                            {
                                SetRedefinitionError((paramName));
                                return Err.ParseError;
                            }

                            FunctionArgument arg = new();
                            arg.Type = paramType;
                            arg.Name = paramName;
                            arg.StructName = paramStructName;
                            arg.Precision = paramPrecision;
                            arg.Qualifier = paramQualifier;
                            arg.TexArgumentCheck = false;
                            arg.TexBuiltinCheck = false;
                            arg.TexArgumentFilter = TextureFilter.Default;
                            arg.TexArgumentRepeat = TextureRepeat.Default;
                            arg.IsConst = paramIsConst;

                            tk = GetToken();
                            if (tk.Type == Tk.BracketOpen)
                            {
                                if (ParseArraySize(null, constants, true, ref argArraySize) != Err.Ok)
                                {
                                    return Err.ParseError;
                                }
                                tk = GetToken();
                            }

                            arg.ArraySize = argArraySize;
                            funcNode.Arguments.Add(arg);

                            if (tk.Type == Tk.Comma)
                            {
                                tk = GetToken();
                                //do none and go on
                            }
                            else if (tk.Type != Tk.ParenthesisClose)
                            {
                                SetExpectedError(",", ")");
                                return Err.ParseError;
                            }
                        }

                        // Searches for function index and check for the exact duplicate in overloads.
                        int functionIndex = 0;
                        for (int i = 0; i < shader.Vfunctions.Count; i++)
                        {
                            if (!shader.Vfunctions[i].Callable || shader.Vfunctions[i].Rname != name)
                            {
                                continue;
                            }

                            functionIndex++;

                            if (shader.Vfunctions[i].Function.Arguments.Count != funcNode.Arguments.Count)
                            {
                                continue;
                            }

                            bool isSame = true;

                            for (int j = 0; j < shader.Vfunctions[i].Function.Arguments.Count; j++)
                            {
                                FunctionArgument a = funcNode.Arguments[j];
                                FunctionArgument b = shader.Vfunctions[i].Function.Arguments[j];

                                if (a.Type == b.Type && a.ArraySize == b.ArraySize)
                                {
                                    if (a.Type == DataType.Struct)
                                    {
                                        isSame = a.StructName == b.StructName;
                                    }
                                }
                                else
                                {
                                    isSame = false;
                                }

                                if (!isSame)
                                {
                                    break;
                                }
                            }

                            if (isSame)
                            {
                                SetRedefinitionError((name));
                                return Err.ParseError;
                            }
                        }

                        // Creates a fake name for function overload, which will be replaced by the real name by the compiler.
                        string name2 = name;
                        if (functionIndex > 0)
                        {
                            name2 = Format("%s@%s", name, Itos(functionIndex + 1));

                            function.Name = name2;
                            funcNode.Name = name2;
                        }

                        shader.Functions[name2] = function;
                        shader.Vfunctions.Add(function);

                        CallInfo callInfo = new();
                        callInfo.Name = name2;
                        callsInfo[name2] = callInfo;

                        if (checkWarnings && HasWarning(GodotShaderWarningFlags.UnusedFunction) && !pFunctions.ContainsKey(name))
                        {
                            usedFunctions[name2] = new Usage(tkLine);
                        }

                        if (pFunctions.ContainsKey(name))
                        {
                            //if one of the core functions, make sure they are of the correct form
                            if (funcNode.Arguments.Count > 0)
                            {
                                SetError(Format(("Function '%s' expects no arguments."), (name)));
                                return Err.ParseError;
                            }
                            if (funcNode.ReturnType != DataType.Void)
                            {
                                SetError(Format(("Function '%s' must be of '%s' return type."), (name), "void"));
                                return Err.ParseError;
                            }
                        }

                        //all good let's parse inside the function!
                        tk = GetToken();
                        if (tk.Type != Tk.CurlyBracketOpen)
                        {
                            SetError(("Expected a '{' to begin function."));
                            return Err.ParseError;
                        }

                        currentFunction = name2;

                        if (ParseBlock(funcNode.Body, builtins) != Err.Ok)
                        {
                            return Err.ParseError;
                        }
                        if (funcNode.ReturnType != DataType.Void)
                        {
                            BlockNode block = funcNode.Body;
                            if (FindLastFlowOpInBlock(block, FlowOperation.Return) != Err.Ok)
                            {
                                SetError(Format(("Expected at least one '%s' statement in a non-void function."), "return"));
                                return Err.ParseError;
                            }
                        }
                        currentFunction = "";
                    }
                    break;
            }

            tk = GetToken();
        }
        uint varyingIndex = baseVaryingIndex;
        // Godot's renderers say 31 but on old OpenGL-only phones, its headless one too.
        uint maxVaryings = 31;

        foreach (KeyValuePair<string, Varying> kv in shader.Varyings)
        {
            if (kv.Value.Stage != VaryingStage.Fragment && (kv.Value.Type > DataType.Bvec4 && kv.Value.Type < DataType.Float) && kv.Value.Interpolation != DataInterpolation.Flat)
            {
                SetTkPos(kv.Value.Tkpos);
                SetError(Format(("Varying with integer data type must be declared with `%s` interpolation qualifier."), "flat"));
                return Err.ParseError;
            }

            if (varyingIndex + kv.Value.GetSize() > maxVaryings)
            {
                SetTkPos(kv.Value.Tkpos);
                SetError(Format(("Too many varyings used in shader (%d used, maximum supported is %d)."), varyingIndex + kv.Value.GetSize(), maxVaryings));
                return Err.ParseError;
            }

            varyingIndex += kv.Value.GetSize();
        }

        if (checkDeviceLimitWarnings && uniformBufferExceededLine != -1)
        {
            AddWarning(GodotShaderWarningCode.DeviceLimitExceeded, uniformBufferExceededLine, "uniform buffer", [(int)uniformBufferSize, (int)maxUniformBufferSize]);
        }
        return Err.Ok;
    }

    private Err FindLastFlowOpInOp(ControlFlowNode pFlow, FlowOperation pOp)
    {
        bool found = false;

        for (int i = pFlow.Blocks.Count - 1; i >= 0; i--)
        {
            if (pFlow.Blocks[i].Type == NodeType.Block)
            {
                BlockNode lastBlock = ((BlockNode)pFlow.Blocks[i]);
                if (FindLastFlowOpInBlock(lastBlock, pOp) == Err.Ok)
                {
                    found = true;
                    break;
                }
            }
        }
        if (found)
        {
            return Err.Ok;
        }
        return Err.Failed;
    }

    private Err FindLastFlowOpInBlock(BlockNode pBlock, FlowOperation pOp)
    {
        bool found = false;

        for (int e = pBlock.Statements.Count - 1; e >= 0; e--)
        {
            Node statement = pBlock.Statements[e];
            if (statement.Type == NodeType.ControlFlow)
            {
                ControlFlowNode flow = (ControlFlowNode)statement;
                if (flow.FlowOp == pOp)
                {
                    found = true;
                    break;
                }
                else
                {
                    if (FindLastFlowOpInOp(flow, pOp) == Err.Ok)
                    {
                        found = true;
                        break;
                    }
                }
            }
            else if (statement.Type == NodeType.Block)
            {
                BlockNode block = (BlockNode)statement;
                if (FindLastFlowOpInBlock(block, pOp) == Err.Ok)
                {
                    found = true;
                    break;
                }
            }
        }

        if (found)
        {
            return Err.Ok;
        }
        return Err.Failed;
    }

    private Err ParseShaderMode(bool pIsStencil, List<ModeInfo> pModes, Dictionary<string, string> rDefinedModes)
    {
        string mode = "";
        GetCompletableIdentifier(null, pIsStencil ? CompletionType.StencilMode : CompletionType.RenderMode, out mode);

        if (mode == "")
        {
            if (pIsStencil)
            {
                SetError(("Expected an identifier for stencil mode."));
            }
            else
            {
                SetError(("Expected an identifier for render mode."));
            }
            return Err.ParseError;
        }

        string smode = (mode);

        List<string> currentModes = pIsStencil ? shader.StencilModes : shader.RenderModes;

        if (currentModes.Contains(mode))
        {
            if (pIsStencil)
            {
                SetError(Format(("Duplicated stencil mode: '%s'."), smode));
            }
            else
            {
                SetError(Format(("Duplicated render mode: '%s'."), smode));
            }
            return Err.ParseError;
        }

        bool found = false;

        if (isShaderInc)
        {
            foreach ((List<ModeInfo> Render, List<ModeInfo> Stencil) typeModes in compileInfo.AllModes)
            {
                List<ModeInfo> modes = pIsStencil ? typeModes.Stencil : typeModes.Render;

                foreach (ModeInfo info in modes)
                {
                    string name = (info.Name);

                    if (smode.StartsWith(name, StringComparison.Ordinal))
                    {
                        if (info.Options.Count > 0)
                        {
                            if (info.Options.Contains(Substr(smode, name.Length + 1)))
                            {
                                found = true;

                                if (rDefinedModes.ContainsKey(name))
                                {
                                    if (pIsStencil)
                                    {
                                        SetError(Format(("Redefinition of stencil mode: '%s'. The '%s' mode has already been set to '%s'."), smode, name, rDefinedModes[name]));
                                    }
                                    else
                                    {
                                        SetError(Format(("Redefinition of render mode: '%s'. The '%s' mode has already been set to '%s'."), smode, name, rDefinedModes[name]));
                                    }
                                    return Err.ParseError;
                                }
                                rDefinedModes[name] = smode;
                                break;
                            }
                        }
                        else
                        {
                            found = true;
                            break;
                        }
                    }
                }
            }
        }
        else
        {
            foreach (ModeInfo info in pModes)
            {
                string name = (info.Name);

                if (smode.StartsWith(name, StringComparison.Ordinal))
                {
                    if (info.Options.Count > 0)
                    {
                        if (info.Options.Contains(Substr(smode, name.Length + 1)))
                        {
                            found = true;

                            if (rDefinedModes.ContainsKey(name))
                            {
                                if (pIsStencil)
                                {
                                    SetError(Format(("Redefinition of stencil mode: '%s'. The '%s' mode has already been set to '%s'."), smode, name, rDefinedModes[name]));
                                }
                                else
                                {
                                    SetError(Format(("Redefinition of render mode: '%s'. The '%s' mode has already been set to '%s'."), smode, name, rDefinedModes[name]));
                                }
                                return Err.ParseError;
                            }
                            rDefinedModes[name] = smode;
                            break;
                        }
                    }
                    else
                    {
                        found = true;
                        break;
                    }
                }
            }
        }

        if (!found)
        {
            if (pIsStencil)
            {
                SetError(Format(("Invalid stencil mode: '%s'."), smode));
            }
            else
            {
                SetError(Format(("Invalid render mode: '%s'."), smode));
            }
            return Err.ParseError;
        }

        if (pIsStencil)
        {
            shader.StencilModes.Add(mode);
        }
        else
        {
            shader.RenderModes.Add(mode);
        }

        return Err.Ok;
    }

    /// <summary>Where the code's first word begins, past white space and comments.</summary>
    private static int GetFirstIdentPos(string pCode)
    {
        int idx = 0;

        char GETCHAR(int offset) => idx + offset < pCode.Length ? pCode[idx + offset] : '\0';

        while (true)
        {
            if (GETCHAR(0) == '/' && GETCHAR(1) == '/')
            {
                idx += 2;
                while (true)
                {
                    if (GETCHAR(0) == 0)
                    {
                        return 0;
                    }
                    if (GETCHAR(0) == '\n')
                    {
                        idx++;
                        break; // loop
                    }
                    idx++;
                }
            }
            else if (GETCHAR(0) == '/' && GETCHAR(1) == '*')
            {
                idx += 2;
                while (true)
                {
                    if (GETCHAR(0) == 0)
                    {
                        return 0;
                    }
                    if (GETCHAR(0) == '*' && GETCHAR(1) == '/')
                    {
                        idx += 2;
                        break; // loop
                    }
                    idx++;
                }
            }
            else
            {
                switch (GETCHAR(0))
                {
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                        {
                            idx++;
                        }
                        break; // switch
                    default:
                        return idx;
                }
            }
        }

    }

    public static string GetShaderType(string pCode)
    {
        bool readingType = false;

        string curIdentifier = "";

        for (int i = GetFirstIdentPos(pCode); i < pCode.Length; i++)
        {
            if (pCode[i] == ';')
            {
                break;

            }
            else if (pCode[i] <= 32)
            {
                if (curIdentifier.Length > 0)
                {
                    if (!readingType)
                    {
                        if (curIdentifier != "shader_type")
                        {
                            return "";
                        }

                        readingType = true;
                        curIdentifier = "";
                    }
                    else
                    {
                        return curIdentifier;
                    }
                }
            }
            else
            {
                curIdentifier += pCode[i];
            }
        }

        if (readingType)
        {
            return curIdentifier;
        }

        return "";
    }
}
