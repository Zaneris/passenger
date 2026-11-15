using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;

namespace Passenger.Roslyn.Extension;

/// <summary>Why a C# refactoring cannot take what it is asked, read from the words asked alone, and the words it says so
/// in: the extension refuses by these before it reads the solution, and each refactoring's dialog checks its options by them
/// as they are typed, so Next is offered only for what the server takes and a person is told what an agent is. Compiled
/// into the extension and into Passenger.Languages, beside the CodeBrackets each reads types and values through.</summary>
public static class RefactoringChecks
{
    private static readonly string[] Modifiers = ["", "ref", "out", "in", "params", "this", "ref readonly", "scoped", "scoped ref", "this ref", "ref this", "this in", "in this"];

    /// <summary>A name a C# type or member can have.</summary>
    public static bool IsName(string name) => SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    /// <summary>A text's words with one space between each, as a modifier or a type compares.</summary>
    public static string Normalized(string text) => string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A namespace as written, each of its parts trimmed.</summary>
    public static string Namespace(string written) => string.Join(".", written.Split('.').Select(p => p.Trim()));

    /// <summary>What's said of a type moved to the namespace it is in.</summary>
    public static string InNamespaceAlready(string type, string space) => $"'{type}' is already in namespace '{space}'";

    /// <summary>Why a type in namespace <paramref name="current"/> cannot move to the one written; null when it can.</summary>
    public static string? WhyNotNamespace(string written, string type, string current)
    {
        string target = Namespace(written);
        return target.Length == 0 ? "Specify namespace"
            : target.Split('.').Any(p => !IsName(p)) ? $"'{written}' is not a valid namespace name"
            : target == current ? InNamespaceAlready(type, target)
            : null;
    }

    /// <summary>Why a new C# file beside another cannot have this name, whatever the folder holds; null when it can.</summary>
    public static string? WhyNotFileName(string fileName) =>
        fileName.Length == 0 ? "file name is empty"
        : !fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "The new file name extension is not valid for source code files"
        : fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('/') || fileName.Contains('\\') ? $"'{fileName}' is not a file name"
        : null;

    /// <summary>Why Extract Interface cannot name its interface so; null when it can.</summary>
    public static string? WhyNotInterfaceName(string name) =>
        name.Length == 0 ? "The interface needs a name" : !IsName(name) ? $"'{name}' is not a valid interface name" : null;

    /// <summary>Why Extract Interface has too few members chosen; null when it has some.</summary>
    public static string? WhyNotInterfaceMembers(int chosen) => chosen > 0 ? null : "Choose the members the interface is to have";

    /// <summary>Why Pull Members Up or Push Members Down has too few members chosen; null when it has some.</summary>
    public static string? WhyNotMembersMoved(int chosen, bool up) =>
        chosen > 0 ? null : up ? "Select the members to pull up" : "Select the members to push down";

    /// <summary>Why Push Members Down from a type has too few of its derived classes chosen; null when it has some.</summary>
    public static string? WhyNotInheritors(int chosen, string type) => chosen > 0 ? null : $"Choose the classes derived from '{type}' to push members down to";

    /// <summary>Why the method or constructor <paramref name="was"/> describes cannot take this name, return type and these
    /// parameters; null when it can. A parameter is read as its row is, its type, name, modifier and values, then as the
    /// declaration it makes (<see cref="Declared"/>), then by its place among the others.</summary>
    public static string? WhyNotSignature(Signature was, string name, string returnType, IReadOnlyList<ParameterChange> parameters)
    {
        bool method = was.Kind == "method";
        if (method && !IsName(name))
        {
            return name.Trim().Length == 0 ? "Method name can not be empty" : $"'{name}' is not a valid method name";
        }
        if (method)
        {
            switch (IsReturnType(returnType))
            {
                case null:
                    return "The return type is nested too deeply to read";
                case false:
                    return "The return type is invalid";
            }
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<int> kept = [];
        bool optional = false;
        for (int i = 0; i < parameters.Count; i++)
        {
            ParameterChange parameter = parameters[i];
            if (parameter.From >= was.Parameters.Count || (parameter.From >= 0 && !kept.Add(parameter.From)))
            {
                return "A parameter is listed twice";
            }
            if (WhyNotParameter(parameter, names) is { } wrong)
            {
                return wrong;
            }
            if (Normalized(parameter.Modifier) == "params" && i != parameters.Count - 1)
            {
                return $"'{parameter.Name}' parameter must appear after all other parameters";
            }
            if (parameter.DefaultValue.Trim().Length > 0)
            {
                optional = true;
            }
            else if (optional && Normalized(parameter.Modifier) != "params")
            {
                return "Optional parameters must appear after all regular parameters";
            }
        }
        if (was.IsExtension && (parameters.Count == 0 || parameters[0].From != 0))
        {
            return $"The first parameter of the extension method '{was.Name}' must stay first";
        }
        return null;
    }

    /// <summary>A parameter as its declaration reads it, given its modifier, type, name and default value; null for one
    /// nested too deeply to read, as a type or a value read whole on its own may be once a list's brackets hold it.</summary>
    public static ParameterSyntax? Declared(string modifier, string type, string name, string defaultValue)
    {
        string text = $"({Normalized(modifier)} {type.Trim()} {name}{(defaultValue.Trim().Length > 0 ? " = " + defaultValue.Trim() : string.Empty)})";
        return CodeBrackets.ParameterList(text)?.Parameters[0];
    }

    /// <summary>Why a parameter's own words will not do, its name not among <paramref name="names"/>, to which it is added.</summary>
    private static string? WhyNotParameter(ParameterChange parameter, HashSet<string> names)
    {
        switch (IsType(parameter.Type))
        {
            case null:
                return "A parameter's type is nested too deeply to read";
            case false:
                return "A parameter has an invalid type";
        }
        if (parameter.Name.Trim().Length == 0)
        {
            return "Parameter name can not be empty";
        }
        if (!IsName(parameter.Name))
        {
            return "A parameter has an invalid name";
        }
        if (!names.Add(parameter.Name))
        {
            return "Parameters should have different names";
        }
        if (!Modifiers.Contains(Normalized(parameter.Modifier)))
        {
            return $"'{parameter.Modifier}' is not a parameter modifier";
        }
        foreach (string value in (string[])[parameter.DefaultValue, parameter.CallValue])
        {
            switch (IsExpression(value))
            {
                case null:
                    return "A parameter's value is nested too deeply to read";
                case false:
                    return "Error in default value expression";
            }
        }
        return Declared(parameter.Modifier, parameter.Type, parameter.Name, parameter.DefaultValue) is null ? "A parameter is nested too deeply to read" : null;
    }

    /// <summary>Whether a text is a type; null for one nested too deeply for the parser to be given.</summary>
    private static bool? IsType(string text) => text.Trim().Length == 0 ? false : CodeBrackets.TypeName(text) is { } type ? !type.ContainsDiagnostics : null;

    /// <summary>A type a method can return: any type, or void, which a type's parser takes for none.</summary>
    private static bool? IsReturnType(string text) => Normalized(text) == "void" ? true : IsType(text);

    /// <summary>Whether a text is an expression or nothing; null for one nested too deeply for the parser to be given.</summary>
    private static bool? IsExpression(string text) => text.Trim().Length == 0 ? true : CodeBrackets.Expression(text) is { } expression ? !expression.ContainsDiagnostics : null;
}
