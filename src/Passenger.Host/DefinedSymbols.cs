using Microsoft.Build.Evaluation;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>The preprocessor symbols a project's compiler is given for one target framework, read from its evaluation: its
/// DefineConstants with the symbols the .NET SDK adds for the framework and its platform (NET, NET11_0,
/// NET8_0_OR_GREATER, WINDOWS…), by the rules of the SDK's AddImplicitDefineConstants target, which a build runs before
/// it compiles and an evaluation never does. Each symbol from the evaluation's own properties and items, so a newer
/// SDK's frameworks and platform versions come with it; and its LangVersion, which the evaluation itself defaults for
/// the framework.</summary>
internal static class DefinedSymbols
{
    private const string NetCoreApp = ".NETCoreApp";
    private const string NetFramework = ".NETFramework";
    private const string NetStandard = ".NETStandard";

    /// <summary>A framework's symbols, each once in the order the compiler is given them, and its C# version; null for a
    /// multi-targeted project's outer evaluation, which compiles nothing.</summary>
    public static FrameworkSymbols? Of(Project project)
    {
        string framework = project.GetPropertyValue(MSBuildNames.TargetFramework);
        if (framework.Length == 0)
        {
            return null;
        }
        List<string> symbols = [.. project.GetPropertyValue("DefineConstants").Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (IsTrue(project, "DisableDiagnosticTracing"))
        {
            symbols.RemoveAll(s => s == "TRACE");
        }
        if (!IsTrue(project, "DisableImplicitFrameworkDefines"))
        {
            symbols.AddRange(Implicit(project));
        }
        string version = project.GetPropertyValue("LangVersion").Trim();
        return new FrameworkSymbols(framework, [.. symbols.Distinct(StringComparer.Ordinal)], version.Length > 0 ? version : null);
    }

    /// <summary>The SDK's symbols for the framework, its platform, and each version of either it is compatible with.</summary>
    private static List<string> Implicit(Project project)
    {
        List<string> symbols = [];
        string identifier = project.GetPropertyValue("TargetFrameworkIdentifier");
        string written = project.GetPropertyValue("TargetFrameworkVersion").TrimStart('v', 'V');
        if (identifier.Length == 0 || identifier == ".NETPortable" || Version(written) is not { } version)
        {
            return symbols;
        }
        bool modern = identifier == NetCoreApp && version >= new Version(5, 0);
        string versionless = modern ? "NET" : identifier.Replace(".", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        string named = identifier == NetFramework ? "NET" : versionless;
        string numbered = written.Replace('.', '_');
        symbols.Add(versionless);
        symbols.Add(named + (identifier == NetFramework ? numbered.Replace("_", string.Empty, StringComparison.Ordinal) : numbered));
        if (modern)
        {
            symbols.Add("NETCOREAPP");
        }
        string platform = project.GetPropertyValue(MSBuildNames.TargetPlatformIdentifier);
        if (modern && platform.Length > 0)
        {
            string upper = platform.ToUpperInvariant();
            symbols.Add(upper);
            symbols.Add(upper + project.GetPropertyValue("EffectiveTargetPlatformVersion").Replace('.', '_'));
            if (IsTrue(project, "IncludeWindowsSDKRefFrameworkReferences") && IsTrue(project, "_TargetPlatformVersionUsesCsWinRT3"))
            {
                symbols.Add("CSWINRT3_0");
            }
        }
        string? listed = identifier switch
        {
            NetCoreApp => "_NETCoreAppVersionsForDefines",
            NetFramework => "SupportedNETFrameworkTargetFramework",
            NetStandard => "SupportedNETStandardTargetFramework",
            _ => null,
        };
        // .NET Core's before .NET 5 come after the rest, as the SDK lists them.
        List<string> core = [];
        foreach (ProjectItem supported in listed is null ? [] : project.GetItems(listed))
        {
            string text = supported.EvaluatedInclude.TrimStart((identifier + ",Version=v").ToCharArray());
            if (Version(text) is not { } compatible || compatible > version)
            {
                continue;
            }
            string formatted = identifier == NetFramework ? text.Replace(".", string.Empty, StringComparison.Ordinal) : text;
            bool older = identifier == NetCoreApp && compatible < new Version(5, 0);
            (older ? core : symbols).Add(((older ? "NETCOREAPP" : named) + formatted + "_OR_GREATER").Replace('.', '_'));
        }
        symbols.AddRange(core);
        if (modern && platform.Length > 0 && Version(project.GetPropertyValue("TargetPlatformVersion")) is { } platformVersion)
        {
            foreach (ProjectItem supported in project.GetItems("SdkSupportedTargetPlatformVersion"))
            {
                string normalized = supported.GetMetadataValue("NormalizedSupportedTargetPlatformVersion");
                string text = normalized.Length > 0 ? normalized : supported.EvaluatedInclude;
                if (Version(text) is { } compatible && compatible <= platformVersion)
                {
                    symbols.Add((platform.ToUpperInvariant() + text + "_OR_GREATER").Replace('.', '_'));
                }
            }
        }
        return symbols;
    }

    private static bool IsTrue(Project project, string name) => project.GetPropertyValue(name).Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A version as MSBuild's version comparisons read one: a leading v left out, a lone number taken as its major.</summary>
    private static Version? Version(string text)
    {
        string trimmed = text.Trim().TrimStart('v', 'V');
        return System.Version.TryParse(trimmed, out Version? version) ? version
            : int.TryParse(trimmed, out int major) && major >= 0 ? new Version(major, 0)
            : null;
    }
}
