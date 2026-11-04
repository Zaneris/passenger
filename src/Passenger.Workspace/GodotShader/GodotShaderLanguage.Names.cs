namespace Passenger.Workspace.GodotShader;

/// <summary>What Godot's shading language calls its types, qualifiers, hints and operators in its messages, and which
/// tokens name them (shader_language.cpp's get_*_name and is_token_* helpers, MIT).</summary>
public sealed partial class GodotShaderLanguage
{
    private static readonly string[] OperatorTexts =
    [
        "==", "!=", "<", "<=", ">", ">=", "&&", "||", "!", "-", "+", "-", "*", "/", "%", "<<", ">>", "=", "+=", "-=", "*=",
        "/=", "%=", "<<=", ">>=", "&=", "|=", "^=", "&", "|", "^", "~", "++", "--", "?", ":", "++", "--", "()", "construct",
        "index", "empty",
    ];

    public static string GetOperatorText(Operator pOp) => OperatorTexts[(int)pOp];

    public static string GetDatatypeName(DataType pType) => (int)pType < DatatypeNames.Length ? DatatypeNames[(int)pType] : string.Empty;

    public static bool IsTokenVariableDatatype(Tk pType) => pType >= Tk.TypeVoid && pType <= Tk.TypeMat4;

    public static bool IsTokenDatatype(Tk pType) => pType >= Tk.TypeVoid && pType <= Tk.TypeSamplerext;

    public static bool IsTokenNonvoidDatatype(Tk pType) => IsTokenDatatype(pType) && pType != Tk.TypeVoid;

    public static DataType GetTokenDatatype(Tk pType) => (DataType)(pType - Tk.TypeVoid);

    public static bool IsTokenInterpolation(Tk pType) => pType is Tk.InterpolationFlat or Tk.InterpolationSmooth;

    public static DataInterpolation GetTokenInterpolation(Tk pType) =>
        pType == Tk.InterpolationFlat ? DataInterpolation.Flat : DataInterpolation.Smooth;

    public static bool IsTokenPrecision(Tk pType) => pType is Tk.PrecisionLow or Tk.PrecisionMid or Tk.PrecisionHigh;

    public static bool IsTokenArgQual(Tk pType) => pType is Tk.ArgIn or Tk.ArgOut or Tk.ArgInout;

    public static DataPrecision GetTokenPrecision(Tk pType) => pType switch
    {
        Tk.PrecisionLow => DataPrecision.Lowp,
        Tk.PrecisionHigh => DataPrecision.Highp,
        _ => DataPrecision.Mediump,
    };

    public static string GetPrecisionName(DataPrecision pType) => pType switch
    {
        DataPrecision.Lowp => "lowp",
        DataPrecision.Mediump => "mediump",
        DataPrecision.Highp => "highp",
        _ => string.Empty,
    };

    public static string GetInterpolationName(DataInterpolation pInterpolation) => pInterpolation switch
    {
        DataInterpolation.Flat => "flat",
        DataInterpolation.Smooth => "smooth",
        _ => string.Empty,
    };

    public static string GetUniformHintName(Hint pHint) => pHint switch
    {
        Hint.Range => "hint_range",
        Hint.Enum => "hint_enum",
        Hint.SourceColor => "source_color",
        Hint.ColorConversionDisabled => "color_conversion_disabled",
        Hint.Normal => "hint_normal",
        Hint.RoughnessNormal => "hint_roughness_normal",
        Hint.RoughnessR => "hint_roughness_r",
        Hint.RoughnessG => "hint_roughness_g",
        Hint.RoughnessB => "hint_roughness_b",
        Hint.RoughnessA => "hint_roughness_a",
        Hint.RoughnessGray => "hint_roughness_gray",
        Hint.DefaultBlack => "hint_default_black",
        Hint.DefaultWhite => "hint_default_white",
        Hint.DefaultTransparent => "hint_default_transparent",
        Hint.Anisotropy => "hint_anisotropy",
        Hint.ScreenTexture => "hint_screen_texture",
        Hint.NormalRoughnessTexture => "hint_normal_roughness_texture",
        Hint.DepthTexture => "hint_depth_texture",
        Hint.BlitSource0 => "hint_blit_source0",
        Hint.BlitSource1 => "hint_blit_source1",
        Hint.BlitSource2 => "hint_blit_source2",
        Hint.BlitSource3 => "hint_blit_source3",
        _ => string.Empty,
    };

    public static string GetTextureFilterName(TextureFilter pFilter) => pFilter switch
    {
        TextureFilter.Nearest => "filter_nearest",
        TextureFilter.Linear => "filter_linear",
        TextureFilter.NearestMipmap => "filter_nearest_mipmap",
        TextureFilter.LinearMipmap => "filter_linear_mipmap",
        TextureFilter.NearestMipmapAnisotropic => "filter_nearest_mipmap_anisotropic",
        TextureFilter.LinearMipmapAnisotropic => "filter_linear_mipmap_anisotropic",
        _ => string.Empty,
    };

    public static string GetTextureRepeatName(TextureRepeat pRepeat) => pRepeat switch
    {
        TextureRepeat.Disable => "repeat_disable",
        TextureRepeat.Enable => "repeat_enable",
        _ => string.Empty,
    };

    private static string GetUnsetPropertyUsageName(PropertyUsage pUsage) => pUsage switch
    {
        PropertyUsage.Storage => "no_storage",
        PropertyUsage.Editor => "no_editor",
        _ => string.Empty,
    };
}
