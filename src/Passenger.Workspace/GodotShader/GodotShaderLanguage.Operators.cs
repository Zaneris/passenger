namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's checks of an operator's operands and the type it gives (shader_language.cpp's _validate_operator, MIT), and
/// its working out of an operator on constants, as Godot folds them.</summary>
public sealed partial class GodotShaderLanguage
{
    private bool ValidateOperator(BlockNode? pBlock, FunctionInfo pFunctionInfo, OperatorNode pOp, bool setsStructName = false)
    {
        bool valid = false;
        DataType retType = DataType.Void;
        int retSize = 0;
        string retStructName = "";

        switch (pOp.Op)
        {
            case Operator.Equal:
            case Operator.NotEqual:
                {
                    if ((!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0) || (!pOp.Arguments[1].IsIndexed() && pOp.Arguments[1].GetArraySize() > 0))
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();
                    valid = na == nb;
                    retType = DataType.Bool;
                }
                break;
            case Operator.Less:
            case Operator.LessEqual:
            case Operator.Greater:
            case Operator.GreaterEqual:
                {
                    if ((!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0) || (!pOp.Arguments[1].IsIndexed() && pOp.Arguments[1].GetArraySize() > 0))
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();

                    valid = na == nb && (na == DataType.Uint || na == DataType.Int || na == DataType.Float);
                    retType = DataType.Bool;

                }
                break;
            case Operator.And:
            case Operator.Or:
                {
                    if ((!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0) || (!pOp.Arguments[1].IsIndexed() && pOp.Arguments[1].GetArraySize() > 0))
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();

                    valid = na == nb && na == DataType.Bool;
                    retType = DataType.Bool;

                }
                break;
            case Operator.Not:
                {
                    if (!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0)
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    valid = na == DataType.Bool;
                    retType = DataType.Bool;

                }
                break;
            case Operator.Increment:
            case Operator.Decrement:
            case Operator.PostIncrement:
            case Operator.PostDecrement:
            case Operator.Negate:
                {
                    if (!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0)
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    valid = na > DataType.Bvec4 && na < DataType.Mat2;
                    retType = na;
                }
                break;
            case Operator.Add:
            case Operator.Sub:
            case Operator.Mul:
            case Operator.Div:
                {
                    if ((!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0) || (!pOp.Arguments[1].IsIndexed() && pOp.Arguments[1].GetArraySize() > 0))
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();

                    if (na > nb)
                    {
                        //make things easier;
                        (na, nb) = (nb, na);
                    }

                    if (na == nb)
                    {
                        valid = (na > DataType.Bvec4 && na <= DataType.Mat4);
                        retType = na;
                    }
                    else if (na == DataType.Int && nb == DataType.Ivec2)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Int && nb == DataType.Ivec3)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Int && nb == DataType.Ivec4)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                    }
                    else if (na == DataType.Uint && nb == DataType.Uvec2)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uint && nb == DataType.Uvec3)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uint && nb == DataType.Uvec4)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                    else if (na == DataType.Float && nb == DataType.Vec2)
                    {
                        valid = true;
                        retType = DataType.Vec2;
                    }
                    else if (na == DataType.Float && nb == DataType.Vec3)
                    {
                        valid = true;
                        retType = DataType.Vec3;
                    }
                    else if (na == DataType.Float && nb == DataType.Vec4)
                    {
                        valid = true;
                        retType = DataType.Vec4;
                    }
                    else if (na == DataType.Float && nb == DataType.Mat2)
                    {
                        valid = true;
                        retType = DataType.Mat2;
                    }
                    else if (na == DataType.Float && nb == DataType.Mat3)
                    {
                        valid = true;
                        retType = DataType.Mat3;
                    }
                    else if (na == DataType.Float && nb == DataType.Mat4)
                    {
                        valid = true;
                        retType = DataType.Mat4;
                    }
                    else if (pOp.Op == Operator.Mul && na == DataType.Vec2 && nb == DataType.Mat2)
                    {
                        valid = true;
                        retType = DataType.Vec2;
                    }
                    else if (pOp.Op == Operator.Mul && na == DataType.Vec3 && nb == DataType.Mat3)
                    {
                        valid = true;
                        retType = DataType.Vec3;
                    }
                    else if (pOp.Op == Operator.Mul && na == DataType.Vec4 && nb == DataType.Mat4)
                    {
                        valid = true;
                        retType = DataType.Vec4;
                    }
                }
                break;
            case Operator.AssignMod:
            case Operator.Mod:
                {
                    // Both integers of one signedness, a scalar applied to each part of a vector, vectors of one size.

                    if ((!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0) || (!pOp.Arguments[1].IsIndexed() && pOp.Arguments[1].GetArraySize() > 0))
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();

                    if (na == DataType.Int && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Int;
                    }
                    else if (na == DataType.Ivec2 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Ivec3 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Ivec4 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                    }
                    else if (na == DataType.Ivec2 && nb == DataType.Ivec2)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Ivec3 && nb == DataType.Ivec3)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Ivec4 && nb == DataType.Ivec4)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                        /////
                    }
                    else if (na == DataType.Uint && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uint;
                    }
                    else if (na == DataType.Uvec2 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uvec3 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uvec4 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                    else if (na == DataType.Uvec2 && nb == DataType.Uvec2)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uvec3 && nb == DataType.Uvec3)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uvec4 && nb == DataType.Uvec4)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                }
                break;
            case Operator.AssignShiftLeft:
            case Operator.AssignShiftRight:
            case Operator.ShiftLeft:
            case Operator.ShiftRight:
                {
                    if ((!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0) || (!pOp.Arguments[1].IsIndexed() && pOp.Arguments[1].GetArraySize() > 0))
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();

                    if (na == DataType.Int && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Int;
                    }
                    else if (na == DataType.Ivec2 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Ivec3 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Ivec4 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                    }
                    else if (na == DataType.Ivec2 && nb == DataType.Ivec2)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Ivec3 && nb == DataType.Ivec3)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Ivec4 && nb == DataType.Ivec4)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                    }
                    else if (na == DataType.Uint && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uint;
                    }
                    else if (na == DataType.Uvec2 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uvec3 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uvec4 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                    else if (na == DataType.Uvec2 && nb == DataType.Uvec2)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uvec3 && nb == DataType.Uvec3)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uvec4 && nb == DataType.Uvec4)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                }
                break;
            case Operator.Assign:
                {
                    int sa = 0;
                    int sb = 0;
                    if (!pOp.Arguments[0].IsIndexed())
                    {
                        sa = pOp.Arguments[0].GetArraySize();
                    }
                    if (!pOp.Arguments[1].IsIndexed())
                    {
                        sb = pOp.Arguments[1].GetArraySize();
                    }
                    if (sa != sb)
                    {
                        break; // don't accept arrays if their sizes are not equal
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();
                    if (na == DataType.Struct || nb == DataType.Struct)
                    {
                        valid = pOp.Arguments[0].GetDatatypeName() == pOp.Arguments[1].GetDatatypeName();
                    }
                    else
                    {
                        valid = na == nb;
                    }
                    retType = na;
                    retSize = sa;
                }
                break;
            case Operator.AssignAdd:
            case Operator.AssignSub:
            case Operator.AssignMul:
            case Operator.AssignDiv:
                {
                    int sa = 0;
                    int sb = 0;
                    if (!pOp.Arguments[0].IsIndexed())
                    {
                        sa = pOp.Arguments[0].GetArraySize();
                    }
                    if (!pOp.Arguments[1].IsIndexed())
                    {
                        sb = pOp.Arguments[1].GetArraySize();
                    }
                    if (sa > 0 || sb > 0)
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();

                    if (na == nb)
                    {
                        valid = (na > DataType.Bvec4 && na <= DataType.Mat4);
                        retType = na;
                    }
                    else if (na == DataType.Ivec2 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Ivec3 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Ivec4 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                    }
                    else if (na == DataType.Uvec2 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uvec3 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uvec4 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                    else if (na == DataType.Vec2 && nb == DataType.Float)
                    {
                        valid = true;
                        retType = DataType.Vec2;
                    }
                    else if (na == DataType.Vec3 && nb == DataType.Float)
                    {
                        valid = true;
                        retType = DataType.Vec3;
                    }
                    else if (na == DataType.Vec4 && nb == DataType.Float)
                    {
                        valid = true;
                        retType = DataType.Vec4;
                    }
                    else if (na == DataType.Mat2 && nb == DataType.Float)
                    {
                        valid = true;
                        retType = DataType.Mat2;
                    }
                    else if (na == DataType.Mat3 && nb == DataType.Float)
                    {
                        valid = true;
                        retType = DataType.Mat3;
                    }
                    else if (na == DataType.Mat4 && nb == DataType.Float)
                    {
                        valid = true;
                        retType = DataType.Mat4;
                    }
                    else if (pOp.Op == Operator.AssignMul && na == DataType.Vec2 && nb == DataType.Mat2)
                    {
                        valid = true;
                        retType = DataType.Vec2;
                    }
                    else if (pOp.Op == Operator.AssignMul && na == DataType.Vec3 && nb == DataType.Mat3)
                    {
                        valid = true;
                        retType = DataType.Vec3;
                    }
                    else if (pOp.Op == Operator.AssignMul && na == DataType.Vec4 && nb == DataType.Mat4)
                    {
                        valid = true;
                        retType = DataType.Vec4;
                    }
                }
                break;
            case Operator.AssignBitAnd:
            case Operator.AssignBitOr:
            case Operator.AssignBitXor:
            case Operator.BitAnd:
            case Operator.BitOr:
            case Operator.BitXor:
                {
                    // Both integers of one signedness, a scalar applied to each part of a vector, vectors of one size.

                    int sa = 0;
                    int sb = 0;
                    if (!pOp.Arguments[0].IsIndexed())
                    {
                        sa = pOp.Arguments[0].GetArraySize();
                    }
                    if (!pOp.Arguments[1].IsIndexed())
                    {
                        sb = pOp.Arguments[1].GetArraySize();
                    }
                    if (sa > 0 || sb > 0)
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();

                    if (na > nb && pOp.Op >= Operator.BitAnd)
                    {
                        //can swap for non assign
                        (na, nb) = (nb, na);
                    }

                    if (na == DataType.Int && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Int;
                    }
                    else if (na == DataType.Ivec2 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Ivec3 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Ivec4 && nb == DataType.Int)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                    }
                    else if (na == DataType.Ivec2 && nb == DataType.Ivec2)
                    {
                        valid = true;
                        retType = DataType.Ivec2;
                    }
                    else if (na == DataType.Ivec3 && nb == DataType.Ivec3)
                    {
                        valid = true;
                        retType = DataType.Ivec3;
                    }
                    else if (na == DataType.Ivec4 && nb == DataType.Ivec4)
                    {
                        valid = true;
                        retType = DataType.Ivec4;
                        /////
                    }
                    else if (na == DataType.Uint && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uint;
                    }
                    else if (na == DataType.Uvec2 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uvec3 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uvec4 && nb == DataType.Uint)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                    else if (na == DataType.Uvec2 && nb == DataType.Uvec2)
                    {
                        valid = true;
                        retType = DataType.Uvec2;
                    }
                    else if (na == DataType.Uvec3 && nb == DataType.Uvec3)
                    {
                        valid = true;
                        retType = DataType.Uvec3;
                    }
                    else if (na == DataType.Uvec4 && nb == DataType.Uvec4)
                    {
                        valid = true;
                        retType = DataType.Uvec4;
                    }
                }
                break;
            case Operator.BitInvert:
                { //unaries
                    if (!pOp.Arguments[0].IsIndexed() && pOp.Arguments[0].GetArraySize() > 0)
                    {
                        break; // don't accept arrays
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    valid = na >= DataType.Int && na < DataType.Float;
                    retType = na;
                }
                break;
            case Operator.SelectIf:
                {
                    int sa = 0;
                    int sb = 0;
                    if (!pOp.Arguments[1].IsIndexed())
                    {
                        sa = pOp.Arguments[1].GetArraySize();
                    }
                    if (!pOp.Arguments[2].IsIndexed())
                    {
                        sb = pOp.Arguments[2].GetArraySize();
                    }
                    if (sa != sb)
                    {
                        break; // don't accept arrays if their sizes are not equal
                    }

                    DataType na = pOp.Arguments[0].GetDatatype();
                    DataType nb = pOp.Arguments[1].GetDatatype();
                    DataType nc = pOp.Arguments[2].GetDatatype();

                    bool isSame = false;
                    if (nb == nc)
                    {
                        isSame = true;

                        if (nb == DataType.Struct)
                        {
                            string tb = pOp.Arguments[1].GetDatatypeName();
                            string tc = pOp.Arguments[2].GetDatatypeName();

                            if (tb != tc)
                            {
                                break;
                            }

                            retStructName = tb;
                        }
                    }

                    valid = na == DataType.Bool && isSame && !IsSamplerType(nb);
                    retType = nb;
                    retSize = sa;
                }
                break;
            default:
                {
                    return false;
                }
        }

        pOp.ReturnCache = retType;
        pOp.ReturnArraySize = retSize;
        if (setsStructName)
        {
            pOp.StructName = retStructName;
        }

        if (valid)
        {
            if (pOp.Arguments[0].GetDatatype() == DataType.Struct)
            {
                return true; // Only applies on ==/!= operators for structs.
            }

            if (pBlock == null || pBlock.UseOpEval)
            {
                // Need to be placed here and not in the `reduceExpression` because otherwise expressions like `1 + 2 / 2` will not work correctly.
                valid = EvalOperator(pBlock, pFunctionInfo, pOp);
            }
        }

        return valid;
    }

    private List<Scalar> GetNodeValues(BlockNode? pBlock, FunctionInfo pFunctionInfo, Node pNode)
    {
        List<Scalar> result = [];

        switch (pNode.Type)
        {
            case NodeType.Variable:
                {
                    IdentifierFound found = new() { WantsValues = true };
                    if (FindIdentifier(pBlock, false, pFunctionInfo, ((VariableNode)pNode).Name, found) && found.ConstantValues != null)
                    {
                        result = found.ConstantValues;
                    }
                }
                break;
            default:
                {
                    result = pNode.GetValues();
                }
                break;
        }

        return result;
    }

    private bool EvalOperator(BlockNode? pBlock, FunctionInfo pFunctionInfo, OperatorNode pOp)
    {
        bool isValid = true;

        switch (pOp.Op)
        {
            case Operator.Equal:
            case Operator.NotEqual:
            case Operator.Less:
            case Operator.LessEqual:
            case Operator.Greater:
            case Operator.GreaterEqual:
            case Operator.And:
            case Operator.Or:
            case Operator.Add:
            case Operator.Sub:
            case Operator.Mul:
            case Operator.Div:
            case Operator.Mod:
            case Operator.ShiftLeft:
            case Operator.ShiftRight:
            case Operator.BitAnd:
            case Operator.BitOr:
            case Operator.BitXor:
                {
                    DataType a = pOp.Arguments[0].GetDatatype();
                    DataType b = pOp.Arguments[1].GetDatatype();

                    bool isOpVecTransform = false;
                    if (pOp.Op == Operator.Mul)
                    {
                        DataType ta = a;
                        DataType tb = b;

                        if (ta > tb)
                        {
                            (ta, tb) = (tb, ta);
                        }
                        if (ta == DataType.Vec2 && tb == DataType.Mat2)
                        {
                            isOpVecTransform = true;
                        }
                        else if (ta == DataType.Vec3 && tb == DataType.Mat3)
                        {
                            isOpVecTransform = true;
                        }
                        else if (ta == DataType.Vec4 && tb == DataType.Mat4)
                        {
                            isOpVecTransform = true;
                        }
                    }

                    List<Scalar> va = GetNodeValues(pBlock, pFunctionInfo, pOp.Arguments[0]);
                    List<Scalar> vb = GetNodeValues(pBlock, pFunctionInfo, pOp.Arguments[1]);

                    if (isOpVecTransform)
                    {
                        pOp.Values = EvalVectorTransform(va, vb, a, b, pOp.GetDatatype());
                    }
                    else
                    {
                        pOp.Values = EvalVector(va, vb, a, b, pOp.GetDatatype(), pOp.Op, ref isValid);
                    }
                }
                break;
            case Operator.Not:
            case Operator.Negate:
            case Operator.BitInvert:
                {
                    pOp.Values = EvalUnaryVector(GetNodeValues(pBlock, pFunctionInfo, pOp.Arguments[0]), pOp.GetDatatype(), pOp.Op);
                }
                break;
            default:
                {
                }
                break;
        }

        return isValid;
    }

    private static Scalar EvalUnaryScalar(Scalar pA, Operator pOp, DataType pRetType)
    {
        Scalar scalar = default;

        switch (pOp)
        {
            case Operator.Not:
                {
                    scalar.Boolean = !pA.Boolean;
                }
                break;
            case Operator.Negate:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = -pA.Sint;
                    }
                    else if (pRetType >= DataType.Uint && pRetType <= DataType.Uvec4)
                    {
                        // Intentionally wrap the unsigned int value, because GLSL does.
                        scalar.Uint = 0 - pA.Uint;
                    }
                    else
                    { // float types
                        scalar.Real = -scalar.Real;
                    }
                }
                break;
            case Operator.BitInvert:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = ~pA.Sint;
                    }
                    else
                    { // uint types
                        scalar.Uint = ~pA.Uint;
                    }
                }
                break;
            default:
                {
                }
                break;
        }

        return scalar;
    }

    private Scalar EvalScalar(Scalar pA, Scalar pB, Operator pOp, DataType pRetType, ref bool rIsValid)
    {
        Scalar scalar = default;

        switch (pOp)
        {
            case Operator.Equal:
                {
                    scalar.Boolean = pA.Boolean == pB.Boolean;
                }
                break;
            case Operator.NotEqual:
                {
                    scalar.Boolean = pA.Boolean != pB.Boolean;
                }
                break;
            case Operator.Less:
                {
                    if (pRetType == DataType.Int)
                    {
                        scalar.Boolean = pA.Sint < pB.Sint;
                    }
                    else if (pRetType == DataType.Uint)
                    {
                        scalar.Boolean = pA.Uint < pB.Uint;
                    }
                    else
                    { // float type
                        scalar.Boolean = pA.Real < pB.Real;
                    }
                }
                break;
            case Operator.LessEqual:
                {
                    if (pRetType == DataType.Int)
                    {
                        scalar.Boolean = pA.Sint <= pB.Sint;
                    }
                    else if (pRetType == DataType.Uint)
                    {
                        scalar.Boolean = pA.Uint <= pB.Uint;
                    }
                    else
                    { // float type
                        scalar.Boolean = pA.Real <= pB.Real;
                    }
                }
                break;
            case Operator.Greater:
                {
                    if (pRetType == DataType.Int)
                    {
                        scalar.Boolean = pA.Sint > pB.Sint;
                    }
                    else if (pRetType == DataType.Uint)
                    {
                        scalar.Boolean = pA.Uint > pB.Uint;
                    }
                    else
                    { // float type
                        scalar.Boolean = pA.Real > pB.Real;
                    }
                }
                break;
            case Operator.GreaterEqual:
                {
                    if (pRetType == DataType.Int)
                    {
                        scalar.Boolean = pA.Sint >= pB.Sint;
                    }
                    else if (pRetType == DataType.Uint)
                    {
                        scalar.Boolean = pA.Uint >= pB.Uint;
                    }
                    else
                    { // float type
                        scalar.Boolean = pA.Real >= pB.Real;
                    }
                }
                break;
            case Operator.And:
                {
                    scalar.Boolean = pA.Boolean && pB.Boolean;
                }
                break;
            case Operator.Or:
                {
                    scalar.Boolean = pA.Boolean || pB.Boolean;
                }
                break;
            case Operator.Add:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint + pB.Sint;
                    }
                    else if (pRetType >= DataType.Uint && pRetType <= DataType.Uvec4)
                    {
                        scalar.Uint = pA.Uint + pB.Uint;
                    }
                    else
                    { // float + matrix types
                        scalar.Real = pA.Real + pB.Real;
                    }
                }
                break;
            case Operator.Sub:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint - pB.Sint;
                    }
                    else if (pRetType >= DataType.Uint && pRetType <= DataType.Uvec4)
                    {
                        scalar.Uint = pA.Uint - pB.Uint;
                    }
                    else
                    { // float + matrix types
                        scalar.Real = pA.Real - pB.Real;
                    }
                }
                break;
            case Operator.Mul:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint * pB.Sint;
                    }
                    else if (pRetType >= DataType.Uint && pRetType <= DataType.Uvec4)
                    {
                        scalar.Uint = pA.Uint * pB.Uint;
                    }
                    else
                    { // float + matrix types
                        scalar.Real = pA.Real * pB.Real;
                    }
                }
                break;
            case Operator.Div:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        if (pB.Sint == 0)
                        {
                            SetError(("Division by zero error."));
                            rIsValid = false;
                            break;
                        }
                        scalar.Sint = pA.Sint / pB.Sint;
                    }
                    else if (pRetType == DataType.Uint && pRetType <= DataType.Uvec4)
                    {
                        if (pB.Uint == 0U)
                        {
                            SetError(("Division by zero error."));
                            rIsValid = false;
                            break;
                        }
                        scalar.Uint = pA.Uint / pB.Uint;
                    }
                    else
                    { // float + matrix types
                        scalar.Real = pA.Real / pB.Real;
                    }
                }
                break;
            case Operator.Mod:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        if (pB.Sint == 0)
                        {
                            SetError(("Modulo by zero error."));
                            rIsValid = false;
                            break;
                        }
                        scalar.Sint = pA.Sint % pB.Sint;
                    }
                    else
                    { // uint types
                        if (pB.Uint == 0U)
                        {
                            SetError(("Modulo by zero error."));
                            rIsValid = false;
                            break;
                        }
                        scalar.Uint = pA.Uint % pB.Uint;
                    }
                }
                break;
            case Operator.ShiftLeft:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint << pB.Sint;
                    }
                    else
                    { // uint types
                        scalar.Uint = pA.Uint << (int)pB.Uint;
                    }
                }
                break;
            case Operator.ShiftRight:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint >> pB.Sint;
                    }
                    else
                    { // uint types
                        scalar.Uint = pA.Uint >> (int)pB.Uint;
                    }
                }
                break;
            case Operator.BitAnd:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint & pB.Sint;
                    }
                    else
                    { // uint types
                        scalar.Uint = pA.Uint & pB.Uint;
                    }
                }
                break;
            case Operator.BitOr:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint | pB.Sint;
                    }
                    else
                    { // uint types
                        scalar.Uint = pA.Uint | pB.Uint;
                    }
                }
                break;
            case Operator.BitXor:
                {
                    if (pRetType >= DataType.Int && pRetType <= DataType.Ivec4)
                    {
                        scalar.Sint = pA.Sint ^ pB.Sint;
                    }
                    else
                    { // uint types
                        scalar.Uint = pA.Uint ^ pB.Uint;
                    }
                }
                break;
            default:
                {
                }
                break;
        }

        return scalar;
    }

    private static List<Scalar> EvalUnaryVector(List<Scalar> pVa, DataType pRetType, Operator pOp)
    {
        int size = (int)GetDatatypeComponentCount(pRetType);
        // Godot compares the count with the type's number, so only a few types' values are worked out.
        if (pVa.Count != (int)pRetType)
        {
            return []; // Non-evaluable values should not be parsed further.
        }
        List<Scalar> value = new(size);
        for (int i = 0; i < size; i++)
        {
            value.Add(EvalUnaryScalar(pVa[i], pOp, pRetType));
        }
        return value;
    }

    private List<Scalar> EvalVector(List<Scalar> pVa, List<Scalar> pVb, DataType pLeftType, DataType pRightType, DataType pRetType, Operator pOp, ref bool rIsValid)
    {
        int leftSize = (int)GetDatatypeComponentCount(pLeftType);
        int rightSize = (int)GetDatatypeComponentCount(pRightType);

        if (pVa.Count != leftSize || pVb.Count != rightSize)
        {
            return []; // Non-evaluable values should not be parsed further.
        }

        int retSize = (int)GetDatatypeComponentCount(pRetType);
        List<Scalar> value = [.. new Scalar[retSize]];
        for (int i = 0; i < retSize; i++)
        {
            value[i] = EvalScalar(pVa[Math.Min(i, leftSize - 1)], pVb[Math.Min(i, rightSize - 1)], pOp, pRetType, ref rIsValid);
            if (!rIsValid)
            {
                return value;
            }
        }
        return value;
    }

    private static List<Scalar> EvalVectorTransform(List<Scalar> pVa, List<Scalar> pVb, DataType pLeftType, DataType pRightType, DataType pRetType)
    {
        int leftSize = (int)GetDatatypeComponentCount(pLeftType);
        int rightSize = (int)GetDatatypeComponentCount(pRightType);

        if (pVa.Count != leftSize || pVb.Count != rightSize)
        {
            return []; // Non-evaluable values should not be parsed further.
        }

        int retSize = (int)GetDatatypeComponentCount(pRetType);
        float[] w = new float[retSize];
        float[] a = [.. pVa.Select(s => s.Real)];
        float[] b = [.. pVb.Select(s => s.Real)];
        switch (pRetType)
        {
            case DataType.Vec2:
                if (leftSize == 2)
                { // v * m
                    w[0] = b[0] * a[0] + b[1] * a[1];
                    w[1] = b[2] * a[0] + b[3] * a[1];
                }
                else
                { // m * v
                    w[0] = a[0] * b[0] + a[2] * b[1];
                    w[1] = a[1] * b[0] + a[3] * b[1];
                }
                break;
            case DataType.Vec3:
                if (leftSize == 3)
                { // v * m
                    w[0] = b[0] * a[0] + b[1] * a[1] + b[2] * a[2];
                    w[1] = b[3] * a[0] + b[4] * a[1] + b[5] * a[2];
                    w[2] = b[6] * a[0] + b[7] * a[1] + b[8] * a[2];
                }
                else
                { // m * v
                    w[0] = a[0] * b[0] + a[3] * b[1] + a[6] * b[2];
                    w[1] = a[1] * b[0] + a[4] * b[1] + a[7] * b[2];
                    w[2] = a[2] * b[0] + a[5] * b[1] + a[8] * b[2];
                }
                break;
            case DataType.Vec4:
                if (leftSize == 4)
                { // v * m
                    w[0] = b[0] * a[0] + b[1] * a[1] + b[2] * a[2] + b[3] * a[3];
                    w[1] = b[4] * a[0] + b[5] * a[1] + b[6] * a[2] + b[7] * a[3];
                    w[2] = b[8] * a[0] + b[9] * a[1] + b[10] * a[2] + b[11] * a[3];
                    w[3] = b[12] * a[0] + b[13] * a[1] + b[14] * a[2] + b[15] * a[3];
                }
                else
                { // m * v
                    w[0] = a[0] * b[0] + a[4] * b[1] + a[8] * b[2] + a[12] * b[3];
                    w[1] = a[1] * b[0] + a[5] * b[1] + a[9] * b[2] + a[13] * b[3];
                    w[2] = a[2] * b[0] + a[6] * b[1] + a[10] * b[2] + a[14] * b[3];
                    w[3] = a[3] * b[0] + a[7] * b[1] + a[11] * b[2] + a[15] * b[3];
                }
                break;
            default:
                break;
        }

        return [.. w.Select(real => new Scalar { Real = real })];
    }

    private static bool IsOperatorAssign(Operator pOp)
    {
        switch (pOp)
        {
            case Operator.Assign:

            case Operator.AssignAdd:
            case Operator.AssignSub:
            case Operator.AssignMul:
            case Operator.AssignDiv:
            case Operator.AssignMod:
            case Operator.AssignShiftLeft:
            case Operator.AssignShiftRight:
            case Operator.AssignBitAnd:
            case Operator.AssignBitOr:
            case Operator.AssignBitXor:
                return true;
            default:
                return false;
        }
    }
}
