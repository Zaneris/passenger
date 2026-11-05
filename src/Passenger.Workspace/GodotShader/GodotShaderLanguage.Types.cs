namespace Passenger.Workspace.GodotShader;

/// <summary>The language's types as Godot's compiler sorts them (shader_language.cpp, MIT): operator tokens, scalar and sampler
/// types, sizes and parts, and constants converted from one type to another.</summary>
public sealed partial class GodotShaderLanguage
{
    private static bool IsTokenOperator(Tk pType)
    {
        return (pType == Tk.OpEqual ||
                pType == Tk.OpNotEqual ||
                pType == Tk.OpLess ||
                pType == Tk.OpLessEqual ||
                pType == Tk.OpGreater ||
                pType == Tk.OpGreaterEqual ||
                pType == Tk.OpAnd ||
                pType == Tk.OpOr ||
                pType == Tk.OpNot ||
                pType == Tk.OpAdd ||
                pType == Tk.OpSub ||
                pType == Tk.OpMul ||
                pType == Tk.OpDiv ||
                pType == Tk.OpMod ||
                pType == Tk.OpShiftLeft ||
                pType == Tk.OpShiftRight ||
                pType == Tk.OpAssign ||
                pType == Tk.OpAssignAdd ||
                pType == Tk.OpAssignSub ||
                pType == Tk.OpAssignMul ||
                pType == Tk.OpAssignDiv ||
                pType == Tk.OpAssignMod ||
                pType == Tk.OpAssignShiftLeft ||
                pType == Tk.OpAssignShiftRight ||
                pType == Tk.OpAssignBitAnd ||
                pType == Tk.OpAssignBitOr ||
                pType == Tk.OpAssignBitXor ||
                pType == Tk.OpBitAnd ||
                pType == Tk.OpBitOr ||
                pType == Tk.OpBitXor ||
                pType == Tk.OpBitInvert ||
                pType == Tk.OpIncrement ||
                pType == Tk.OpDecrement ||
                pType == Tk.Question ||
                pType == Tk.Colon);
    }

    private static bool IsTokenOperatorAssign(Tk pType)
    {
        return (pType == Tk.OpAssign ||
                pType == Tk.OpAssignAdd ||
                pType == Tk.OpAssignSub ||
                pType == Tk.OpAssignMul ||
                pType == Tk.OpAssignDiv ||
                pType == Tk.OpAssignMod ||
                pType == Tk.OpAssignShiftLeft ||
                pType == Tk.OpAssignShiftRight ||
                pType == Tk.OpAssignBitAnd ||
                pType == Tk.OpAssignBitOr ||
                pType == Tk.OpAssignBitXor);
    }

    private static bool IsTokenHint(Tk pType)
    {
        return (int)pType > (int)Tk.StencilMode && (int)pType < (int)Tk.ShaderType;
    }

    /// <summary>Whether an operator's constant value converts to a type, written into <paramref name="pValue"/> when given.</summary>
    private static bool ConvertOperator(OperatorNode pOperator, DataType pToType, List<Scalar>? pValue = null, bool pInConstructor = false) =>
        ConvertValues(pOperator.GetDatatype(), pOperator.Values, pToType, pValue, pInConstructor);

    /// <summary>Whether a constant converts to a type, written into <paramref name="pValue"/> when given.</summary>
    private static bool ConvertConstant(ConstantNode pConstant, DataType pToType, List<Scalar>? pValue = null, bool pInConstructor = false) =>
        ConvertValues(pConstant.GetDatatype(), pConstant.Values, pToType, pValue, pInConstructor);

    private static bool ConvertValues(DataType pFromType, List<Scalar> pValues, DataType pToType, List<Scalar>? pValue, bool pInConstructor)
    {
        if (pValues.Count == 0)
        {
            if (ConvertScalar(pFromType, pToType))
            {
                return true;
            }
            if (pInConstructor)
            {
                return ConvertBooleanScalar(pFromType, pToType);
            }
            return false;
        }
        if (pFromType == pToType)
        {
            if (pValue != null)
            {
                for (int i = 0; i < pValues.Count; i++)
                {
                    pValue[i] = pValues[i];
                }
            }
            return true;
        }
        if (ConvertScalar(pFromType, pToType, pValues[0], pValue))
        {
            return true;
        }
        if (pInConstructor)
        {
            return ConvertBooleanScalar(pFromType, pToType, pValues[0], pValue);
        }
        return false;
    }

    private static bool ConvertScalar(DataType pFromType, DataType pToType, Scalar? pScalar = null, List<Scalar>? pValue = null)
    {
        if (pFromType == DataType.Int && pToType == DataType.Float)
        {
            if (pScalar is Scalar scalar && pValue != null)
            {
                pValue[0] = new Scalar { Real = scalar.Sint };
            }
            return true;
        }
        else if (pFromType == DataType.Uint && pToType == DataType.Float)
        {
            if (pScalar is Scalar scalar && pValue != null)
            {
                pValue[0] = new Scalar { Real = scalar.Uint };
            }
            return true;
        }
        else if (pFromType == DataType.Int && pToType == DataType.Uint)
        {
            if (pScalar is Scalar scalar)
            {
                if (scalar.Sint < 0)
                {
                    return false;
                }
                if (pValue != null)
                {
                    pValue[0] = new Scalar { Uint = (uint)scalar.Sint };
                }
            }
            return true;
        }
        else if (pFromType == DataType.Uint && pToType == DataType.Int)
        {
            if (pScalar is Scalar scalar)
            {
                if (scalar.Uint > 0x7FFFFFFF)
                {
                    return false;
                }
                if (pValue != null)
                {
                    pValue[0] = new Scalar { Sint = (int)scalar.Uint };
                }
            }
            return true;
        }
        else
        {
            return false;
        }
    }

    private static bool ConvertBooleanScalar(DataType pFromType, DataType pToType, Scalar? pScalar = null, List<Scalar>? pValue = null)
    {
        if (pFromType != DataType.Bool)
        {
            return false;
        }
        if (pToType == DataType.Float)
        {
            if (pScalar is Scalar scalar && pValue != null)
            {
                pValue[0] = new Scalar { Real = scalar.Boolean ? 1.0f : 0.0f };
            }
            return true;
        }
        else if (pToType == DataType.Uint)
        {
            if (pScalar is Scalar scalar && pValue != null)
            {
                pValue[0] = new Scalar { Uint = scalar.Boolean ? 1U : 0U };
            }
            return true;
        }
        else if (pToType == DataType.Int)
        {
            if (pScalar is Scalar scalar && pValue != null)
            {
                pValue[0] = new Scalar { Sint = scalar.Boolean ? 1 : 0 };
            }
            return true;
        }
        return false;
    }

    private static bool IsScalarType(DataType pType)
    {
        return pType == DataType.Bool || pType == DataType.Int || pType == DataType.Uint || pType == DataType.Float;
    }

    private static bool IsFloatType(DataType pType)
    {
        switch (pType)
        {
            case DataType.Float:
            case DataType.Vec2:
            case DataType.Vec3:
            case DataType.Vec4:
            case DataType.Mat2:
            case DataType.Mat3:
            case DataType.Mat4:
            case DataType.Sampler2d:
            case DataType.Sampler2darray:
            case DataType.Sampler3d:
            case DataType.Samplercube:
            case DataType.Samplercubearray:
                {
                    return true;
                }
            default:
                {
                    return false;
                }
        }
    }

    private static bool IsSamplerType(DataType pType)
    {
        return pType > DataType.Mat4 && pType < DataType.Struct;
    }

    private static bool IsHintColor(Hint pHint)
    {
        return pHint == Hint.SourceColor || pHint == Hint.ColorConversionDisabled;
    }

    private static uint GetDatatypeSize(DataType pType)
    {
        switch (pType)
        {
            case DataType.Void:
                return 0;
            case DataType.Bool:
                return 4;
            case DataType.Bvec2:
                return 8;
            case DataType.Bvec3:
                return 12;
            case DataType.Bvec4:
                return 16;
            case DataType.Int:
                return 4;
            case DataType.Ivec2:
                return 8;
            case DataType.Ivec3:
                return 12;
            case DataType.Ivec4:
                return 16;
            case DataType.Uint:
                return 4;
            case DataType.Uvec2:
                return 8;
            case DataType.Uvec3:
                return 12;
            case DataType.Uvec4:
                return 16;
            case DataType.Float:
                return 4;
            case DataType.Vec2:
                return 8;
            case DataType.Vec3:
                return 12;
            case DataType.Vec4:
                return 16;
            case DataType.Mat2:
                return 32; // 4 * 4 + 4 * 4
            case DataType.Mat3:
                return 48; // 4 * 4 + 4 * 4 + 4 * 4
            case DataType.Mat4:
                return 64;
            case DataType.Sampler2d:
                return 16;
            case DataType.Isampler2d:
                return 16;
            case DataType.Usampler2d:
                return 16;
            case DataType.Sampler2darray:
                return 16;
            case DataType.Isampler2darray:
                return 16;
            case DataType.Usampler2darray:
                return 16;
            case DataType.Sampler3d:
                return 16;
            case DataType.Isampler3d:
                return 16;
            case DataType.Usampler3d:
                return 16;
            case DataType.Samplercube:
                return 16;
            case DataType.Samplercubearray:
                return 16;
            case DataType.Samplerext:
                return 16;
            case DataType.Struct:
                return 0;
            case DataType.Max:
                {
                    return 0;
                }
                ;
        }
        return 0;
    }

    private static uint GetDatatypeComponentCount(DataType pType)
    {
        switch (pType)
        {
            case DataType.Bool:
                return 1U;
            case DataType.Bvec2:
                return 2U;
            case DataType.Bvec3:
                return 3U;
            case DataType.Bvec4:
                return 4U;
            case DataType.Int:
                return 1U;
            case DataType.Ivec2:
                return 2U;
            case DataType.Ivec3:
                return 3U;
            case DataType.Ivec4:
                return 4U;
            case DataType.Uint:
                return 1U;
            case DataType.Uvec2:
                return 2U;
            case DataType.Uvec3:
                return 3U;
            case DataType.Uvec4:
                return 4U;
            case DataType.Float:
                return 1U;
            case DataType.Vec2:
                return 2U;
            case DataType.Vec3:
                return 3U;
            case DataType.Vec4:
                return 4U;
            case DataType.Mat2:
                return 4U;
            case DataType.Mat3:
                return 9U;
            case DataType.Mat4:
                return 16U;
            default:
                break;
        }
        return 0U;
    }

    private static readonly DataType[] ScalarTypes =
    [
        DataType.Void,
        DataType.Bool,
        DataType.Bool,
        DataType.Bool,
        DataType.Bool,
        DataType.Int,
        DataType.Int,
        DataType.Int,
        DataType.Int,
        DataType.Uint,
        DataType.Uint,
        DataType.Uint,
        DataType.Uint,
        DataType.Float,
        DataType.Float,
        DataType.Float,
        DataType.Float,
        DataType.Float,
        DataType.Float,
        DataType.Float,
        DataType.Float,
        DataType.Int,
        DataType.Uint,
        DataType.Float,
        DataType.Int,
        DataType.Uint,
        DataType.Float,
        DataType.Int,
        DataType.Uint,
        DataType.Float,
        DataType.Float,
        DataType.Float,
        DataType.Void,
    ];

    private static DataType GetScalarType(DataType pType) => ScalarTypes[(int)pType];

    private static readonly int[] CardinalityTable = [0, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4, 4, 9, 16, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

    private static int GetCardinality(DataType pType) => CardinalityTable[(int)pType];

    private static Hint SanitizeHint(Hint pHint)
    {
        if (pHint == Hint.ScreenTexture ||
                pHint == Hint.NormalRoughnessTexture ||
                pHint == Hint.DepthTexture)
        {
            return pHint;
        }
        return Hint.None;
    }
}
