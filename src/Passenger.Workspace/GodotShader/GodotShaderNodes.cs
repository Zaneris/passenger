namespace Passenger.Workspace.GodotShader;

/// <summary>The node tree Godot's shading language parses a shader into (shader_language.h's nodes, MIT): what each
/// expression, statement, block, function and declaration is, with its type as the parse found it.</summary>
public sealed partial class GodotShaderLanguage
{
    public enum NodeType
    {
        Shader,
        Function,
        Block,
        Variable,
        VariableDeclaration,
        Constant,
        Operator,
        ControlFlow,
        Member,
        Array,
        ArrayConstruct,
        Struct,
    }

    public enum BlockType
    {
        Standard,
        ForInit,
        ForCondition,
        ForExpression,
        Switch,
        Case,
        Default,
    }

    public enum Hint
    {
        None,
        Range,
        Enum,
        SourceColor,
        ColorConversionDisabled,
        Normal,
        RoughnessNormal,
        RoughnessR,
        RoughnessG,
        RoughnessB,
        RoughnessA,
        RoughnessGray,
        DefaultBlack,
        DefaultWhite,
        DefaultTransparent,
        Anisotropy,
        ScreenTexture,
        BlitSource0,
        BlitSource1,
        BlitSource2,
        BlitSource3,
        NormalRoughnessTexture,
        DepthTexture,
        Max,
    }

    public enum UniformScope
    {
        Local,
        Instance,
        Global,
    }

    public enum VaryingStage
    {
        Unknown,
        Vertex,
        Fragment,
    }

    /// <summary>The property usage flags a uniform's are made of (Godot's PropertyUsageFlags, those it uses).</summary>
    [Flags]
    public enum PropertyUsage : uint
    {
        None = 0,
        Storage = 2,
        Editor = 4,
        Default = Storage | Editor,
    }

    public abstract class Node(NodeType type)
    {
        public NodeType Type { get; } = type;

        public virtual DataType GetDatatype() => DataType.Void;

        public virtual string GetDatatypeName() => string.Empty;

        public virtual int GetArraySize() => 0;

        public virtual bool IsIndexed() => false;

        public virtual List<Scalar> GetValues() => [];
    }

    public sealed class OperatorNode() : Node(NodeType.Operator)
    {
        public DataType ReturnCache { get; set; } = DataType.Void;

        public DataPrecision ReturnPrecisionCache { get; set; } = DataPrecision.Default;

        public int ReturnArraySize { get; set; }

        public Operator Op { get; set; } = Operator.Equal;

        public string StructName { get; set; } = string.Empty;

        public List<Node> Arguments { get; set; } = [];

        public List<Scalar> Values { get; set; } = [];

        public override DataType GetDatatype() => ReturnCache;

        public override string GetDatatypeName() => StructName;

        public override int GetArraySize() => ReturnArraySize;

        public override bool IsIndexed() => Op == Operator.Index;

        public override List<Scalar> GetValues() => [.. Values];
    }

    public sealed class VariableNode() : Node(NodeType.Variable)
    {
        public DataType DatatypeCache { get; set; } = DataType.Void;

        public string Name { get; set; } = string.Empty;

        public string Rname { get; set; } = string.Empty;

        public string StructName { get; set; } = string.Empty;

        public bool IsConst { get; set; }

        public bool IsLocal { get; set; }

        public override DataType GetDatatype() => DatatypeCache;

        public override string GetDatatypeName() => StructName;
    }

    public sealed class Declaration
    {
        public string Name { get; set; } = string.Empty;

        public uint Size { get; set; }

        public Node? SizeExpression { get; set; }

        public List<Node> Initializer { get; set; } = [];

        public bool SingleExpression { get; set; }
    }

    public sealed class VariableDeclarationNode() : Node(NodeType.VariableDeclaration)
    {
        public DataPrecision Precision { get; set; } = DataPrecision.Default;

        public DataType Datatype { get; set; } = DataType.Void;

        public string StructName { get; set; } = string.Empty;

        public bool IsConst { get; set; }

        public List<Declaration> Declarations { get; } = [];

        public override DataType GetDatatype() => Datatype;
    }

    public sealed class ArrayNode() : Node(NodeType.Array)
    {
        public DataType DatatypeCache { get; set; } = DataType.Void;

        public string StructName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public Node? IndexExpression { get; set; }

        public Node? CallExpression { get; set; }

        public Node? AssignExpression { get; set; }

        public bool IsConst { get; set; }

        public int ArraySize { get; set; }

        public bool IsLocal { get; set; }

        public override DataType GetDatatype() => CallExpression?.GetDatatype() ?? DatatypeCache;

        public override string GetDatatypeName() => CallExpression?.GetDatatypeName() ?? StructName;

        public override int GetArraySize() => IndexExpression is not null || CallExpression is not null ? 0 : ArraySize;

        public override bool IsIndexed() => IndexExpression is not null;
    }

    public sealed class ArrayConstructNode() : Node(NodeType.ArrayConstruct)
    {
        public DataType Datatype { get; set; } = DataType.Void;

        public string StructName { get; set; } = string.Empty;

        public List<Node> Initializer { get; set; } = [];

        public override DataType GetDatatype() => Datatype;

        public override string GetDatatypeName() => StructName;

        public override int GetArraySize() => Initializer.Count;
    }

    public sealed class ConstantNode() : Node(NodeType.Constant)
    {
        public DataType Datatype { get; set; } = DataType.Void;

        public string StructName { get; set; } = string.Empty;

        public int ArraySize { get; set; }

        public List<Scalar> Values { get; set; } = [];

        public List<Declaration> ArrayDeclarations { get; set; } = [];

        public override DataType GetDatatype() => Datatype;

        public override string GetDatatypeName() => StructName;

        public override int GetArraySize() => ArraySize;

        public override List<Scalar> GetValues() => [.. Values];
    }

    public sealed class BlockVariable
    {
        public DataType Type { get; set; }

        public string StructName { get; set; } = string.Empty;

        public DataPrecision Precision { get; set; }

        public int Line { get; set; }

        public int ArraySize { get; set; }

        public bool IsConst { get; set; }

        public List<Scalar> Values { get; set; } = [];
    }

    public sealed class BlockNode() : Node(NodeType.Block)
    {
        public FunctionNode? ParentFunction { get; set; }

        public BlockNode? ParentBlock { get; set; }

        public BlockType BlockType { get; set; } = BlockType.Standard;

        public SubClassTag BlockTag { get; set; } = SubClassTag.Global;

        public Dictionary<string, BlockVariable> Variables { get; } = new(StringComparer.Ordinal);

        public List<Node> Statements { get; } = [];

        public bool SingleStatement { get; set; }

        public bool UseCommaBetweenStatements { get; set; }

        public bool UseOpEval { get; set; } = true;

        public DataType ExpectedType { get; set; } = DataType.Void;

        public HashSet<int> Constants { get; } = [];
    }

    public sealed class ControlFlowNode() : Node(NodeType.ControlFlow)
    {
        public FlowOperation FlowOp { get; set; } = FlowOperation.If;

        public List<Node> Expressions { get; } = [];

        public List<BlockNode> Blocks { get; } = [];
    }

    public sealed class MemberNode() : Node(NodeType.Member)
    {
        public DataType Basetype { get; set; } = DataType.Void;

        public bool BasetypeConst { get; set; }

        public string BaseStructName { get; set; } = string.Empty;

        public DataPrecision Precision { get; set; } = DataPrecision.Default;

        public DataType Datatype { get; set; } = DataType.Void;

        public int ArraySize { get; set; }

        public string StructName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public Node? Owner { get; set; }

        public Node? IndexExpression { get; set; }

        public Node? AssignExpression { get; set; }

        public Node? CallExpression { get; set; }

        public bool HasSwizzlingDuplicates { get; set; }

        public override DataType GetDatatype() => CallExpression?.GetDatatype() ?? Datatype;

        public override string GetDatatypeName() => CallExpression?.GetDatatypeName() ?? StructName;

        public override int GetArraySize() => IndexExpression is not null || CallExpression is not null ? 0 : ArraySize;

        public override bool IsIndexed() => IndexExpression is not null || CallExpression is not null;
    }

    public sealed class StructNode() : Node(NodeType.Struct)
    {
        public List<MemberNode> Members { get; } = [];
    }

    public sealed class ShaderConstant
    {
        public string Name { get; set; } = string.Empty;

        public DataType Type { get; set; }

        public string StructName { get; set; } = string.Empty;

        public DataPrecision Precision { get; set; }

        public Node? Initializer { get; set; }

        public int ArraySize { get; set; }
    }

    public sealed class ShaderFunction
    {
        public string Name { get; set; } = string.Empty;

        public string Rname { get; set; } = string.Empty;

        public FunctionNode Function { get; set; } = new();

        public HashSet<string> UsesFunction { get; } = new(StringComparer.Ordinal);

        public bool Callable { get; set; }
    }

    public sealed class ShaderStructDecl
    {
        public string Name { get; set; } = string.Empty;

        public StructNode ShaderStruct { get; set; } = new();
    }

    public sealed class Varying
    {
        public VaryingStage Stage { get; set; } = VaryingStage.Unknown;

        public DataType Type { get; set; } = DataType.Void;

        public DataInterpolation Interpolation { get; set; } = DataInterpolation.Flat;

        public DataPrecision Precision { get; set; } = DataPrecision.Default;

        public int ArraySize { get; set; }

        internal TkPos Tkpos { get; set; }

        public uint GetSize()
        {
            uint size = ArraySize > 0 ? (uint)ArraySize : 1;
            return Type switch
            {
                DataType.Mat2 => size * 2,
                DataType.Mat3 => size * 3,
                DataType.Mat4 => size * 4,
                _ => size,
            };
        }
    }

    public sealed class Uniform
    {
        public int Order { get; set; }

        public int PropOrder { get; set; }

        public int TextureOrder { get; set; }

        public int TextureBinding { get; set; }

        public DataType Type { get; set; } = DataType.Void;

        public DataPrecision Precision { get; set; } = DataPrecision.Default;

        public int ArraySize { get; set; }

        public List<Scalar> DefaultValue { get; set; } = [];

        public UniformScope Scope { get; set; } = UniformScope.Local;

        public Hint Hint { get; set; } = Hint.None;

        public bool UseColor { get; set; }

        public TextureFilter Filter { get; set; } = TextureFilter.Default;

        public TextureRepeat Repeat { get; set; } = TextureRepeat.Default;

        public float[] HintRange { get; } = [0.0f, 1.0f, 0.001f];

        public List<string> HintEnumNames { get; set; } = [];

        public PropertyUsage PropertyUsage { get; set; } = PropertyUsage.Default;

        public int InstanceIndex { get; set; }

        public string Group { get; set; } = string.Empty;

        /// <summary>Where it is declared (the code's line), for the card that says what the compile read.</summary>
        public int Line { get; set; }

        public bool IsTexture() => Order < 0;
    }

    public sealed class ShaderNode() : Node(NodeType.Shader)
    {
        public Dictionary<string, ShaderConstant> Constants { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Varying> Varyings { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Uniform> Uniforms { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ShaderStructDecl> Structs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ShaderFunction> Functions { get; } = new(StringComparer.Ordinal);

        public List<string> RenderModes { get; } = [];

        public List<string> StencilModes { get; } = [];

        public int StencilReference { get; set; } = -1;

        public List<ShaderFunction> Vfunctions { get; } = [];

        public List<ShaderConstant> Vconstants { get; } = [];

        public List<ShaderStructDecl> Vstructs { get; } = [];
    }

    public sealed class FunctionArgument
    {
        public ArgumentQualifier Qualifier { get; set; }

        public string Name { get; set; } = string.Empty;

        public DataType Type { get; set; }

        public string StructName { get; set; } = string.Empty;

        public DataPrecision Precision { get; set; }

        public bool TexArgumentCheck { get; set; }

        public TextureFilter TexArgumentFilter { get; set; }

        public TextureRepeat TexArgumentRepeat { get; set; }

        public bool TexBuiltinCheck { get; set; }

        public string TexBuiltin { get; set; } = string.Empty;

        public Hint TexHint { get; set; } = Hint.None;

        public bool IsConst { get; set; }

        public int ArraySize { get; set; }

        public Dictionary<string, HashSet<int>> TexArgumentConnect { get; } = new(StringComparer.Ordinal);
    }

    public sealed class FunctionNode() : Node(NodeType.Function)
    {
        public string Name { get; set; } = string.Empty;

        public string Rname { get; set; } = string.Empty;

        public DataType ReturnType { get; set; } = DataType.Void;

        public string ReturnStructName { get; set; } = string.Empty;

        public DataPrecision ReturnPrecision { get; set; } = DataPrecision.Default;

        public int ReturnArraySize { get; set; }

        public List<FunctionArgument> Arguments { get; } = [];

        public BlockNode? Body { get; set; }

        public bool CanDiscard { get; set; }

        public override DataType GetDatatype() => ReturnType;

        public override string GetDatatypeName() => ReturnStructName;

        public override int GetArraySize() => ReturnArraySize;
    }

    /// <summary>One item of an expression being read: an operator, or a node.</summary>
    private sealed class Expression
    {
        public bool IsOp { get; set; }

        public Operator Op { get; set; }

        public Node? Node { get; set; }
    }

    /// <summary>What reading an expression goes on from: the items read so far, the token that ends it, and whether it is
    /// the last.</summary>
    private sealed class ExpressionInfo
    {
        public List<Expression> Expression { get; set; } = [];

        public Tk TtBreak { get; set; } = Tk.Empty;

        public bool IsLastExpr { get; set; }
    }

    private readonly record struct VarInfo(string Name, DataType Type);
}
