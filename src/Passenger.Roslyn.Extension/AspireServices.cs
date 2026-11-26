using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;

namespace Passenger.Roslyn.Extension;

/// <summary>The .NET projects an Aspire AppHost names as its services: each call of Aspire's that adds one as a resource
/// (<see cref="Calls"/>), read for the resource's name and the path or type it adds by, the path taken as Aspire takes it
/// (<see cref="ProjectOf"/>). Compiled into the extension, whose readers know a call by its symbol, and into
/// Passenger.Languages, whose readers have its syntax alone: one reader of an AppHost's services for both.</summary>
public static class AspireServices
{
    /// <summary>A call of Aspire's that adds a .NET project as a resource: its method, the class and assembly declaring it,
    /// the parameter it takes the project's path by, and the package that brings it.</summary>
    public sealed record Adding(string Method, string Type, string Assembly, string PathParameter, string Package);

    /// <summary>AddProject, by a path or by the Projects type an AppHost SDK makes of a project it references; obsolete from
    /// Aspire 17, which writes <see cref="Dotnet"/>.</summary>
    public static readonly Adding Project = new("AddProject", "Aspire.Hosting.ProjectResourceBuilderExtensions", "Aspire.Hosting", "projectPath", "Aspire.Hosting.AppHost");

    /// <summary>AddCSharpApp, by a path alone as <see cref="Dotnet"/> takes one; obsolete from Aspire 17, which writes that call.</summary>
    public static readonly Adding CSharpApp = new("AddCSharpApp", "Aspire.Hosting.ProjectResourceBuilderExtensions", "Aspire.Hosting", "path", "Aspire.Hosting.AppHost");

    /// <summary>AddDotnetProject, Aspire 17's, by a path alone: a project file, a folder holding one, or a file-based program.</summary>
    public static readonly Adding Dotnet = new("AddDotnetProject", "Aspire.Hosting.DotnetProjectHostingExtensions", "Aspire.Hosting.Dotnet", "path", "Aspire.Hosting.Dotnet");

    public static IReadOnlyList<Adding> Calls { get; } = [Project, CSharpApp, Dotnet];

    /// <summary>The parameter each call takes the resource's name by.</summary>
    public const string NameParameter = "name";

    /// <summary>A service a call names: the expressions it gives the resource's name and the project's path, the type it gives
    /// for a project (AddProject's Projects type), and whether each argument is one the syntax places (<see cref="ParameterOf"/>).</summary>
    public sealed record Service(InvocationExpressionSyntax Call, Adding Adding, ExpressionSyntax? Name, ExpressionSyntax? Path, TypeSyntax? Type, bool Placed);

    /// <summary>The call a method is, by its symbol; null for any other.</summary>
    public static Adding? Of(IMethodSymbol method) => Calls.FirstOrDefault(adding => adding.Method == method.Name
        && method.ContainingType.ToDisplayString() == adding.Type && method.ContainingAssembly.Name == adding.Assembly);

    /// <summary>The call an invocation is by the name it calls, as its syntax alone says; null for any other.</summary>
    public static Adding? CalledAs(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member
        ? Calls.FirstOrDefault(adding => adding.Method == member.Name.Identifier.ValueText) : null;

    /// <summary>What a call gives its resource's name, its project's path and its project type, each argument's parameter as
    /// <paramref name="parameterOf"/> names it (null for one the call has not).</summary>
    public static Service Read(InvocationExpressionSyntax call, Adding adding, Func<ArgumentSyntax, string?> parameterOf)
    {
        ExpressionSyntax? name = null;
        ExpressionSyntax? path = null;
        bool placed = true;
        foreach (ArgumentSyntax argument in call.ArgumentList.Arguments)
        {
            string? parameter = parameterOf(argument);
            placed &= parameter is not null && argument.RefKindKeyword.IsKind(SyntaxKind.None);
            if (parameter == NameParameter)
            {
                name = argument.Expression;
            }
            else if (parameter == adding.PathParameter)
            {
                path = argument.Expression;
            }
        }
        TypeSyntax? type = call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } ? generic.TypeArgumentList.Arguments.FirstOrDefault() : null;
        return new Service(call, adding, name, path, type, placed);
    }

    /// <summary>The parameter an argument gives, as the syntax alone says: by its name, else by its place, the name first,
    /// then the path where no type argument gives the project, then the launch profile or options; null for none of these, or
    /// one by its place after one named out of its place.</summary>
    public static string? ParameterOf(InvocationExpressionSyntax call, Adding adding, ArgumentSyntax argument)
    {
        bool generic = call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax };
        string[] parameters = generic ? [NameParameter, "options"] : [NameParameter, adding.PathParameter, "options"];
        SeparatedSyntaxList<ArgumentSyntax> arguments = call.ArgumentList.Arguments;
        int place = arguments.IndexOf(argument);
        if (argument.NameColon is { } named)
        {
            string parameter = named.Name.Identifier.ValueText;
            return parameter is NameParameter or "launchProfileName" or "configure" || parameter == adding.PathParameter && !generic ? parameter : null;
        }
        bool reordered = arguments.Take(place).Any(before => before.NameColon is { } colon
            && Array.IndexOf(parameters, colon.Name.Identifier.ValueText) != arguments.IndexOf(before));
        return !reordered && place < parameters.Length ? parameters[place] : null;
    }

    /// <summary>The services an AppHost's file names, read from its syntax alone (<see cref="ParameterOf"/>).</summary>
    public static IEnumerable<Service> In(SyntaxNode root) => root.DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Select(call => CalledAs(call) is { } adding ? Read(call, adding, argument => ParameterOf(call, adding, argument)) : null)
        .OfType<Service>();

    /// <summary>The string a service's expression is, written as a literal, for a reader with no compilation; null for any other.</summary>
    public static string? Written(ExpressionSyntax? expression) => expression is LiteralExpressionSyntax { Token.Value: string value } ? value : null;

    /// <summary>The project a path names as Aspire takes it, from the AppHost's folder: a project or C# file as named, a folder
    /// by the one project file in it; null for a path that names none.</summary>
    public static string? ProjectOf(string path, string? folder)
    {
        try
        {
            string written = OperatingSystem.IsWindows() ? path : path.Replace('\\', '/');
            string? full = Path.IsPathFullyQualified(written) ? Path.GetFullPath(written) : folder is null ? null : Path.GetFullPath(written, folder);
            if (full is null || !Directory.Exists(full))
            {
                return full;
            }
            string[] projects = [.. Directory.EnumerateFiles(full).Where(file => PathNames.HasExtension(file, [".csproj", ".fsproj", ".vbproj"]))];
            return projects.Length == 1 ? projects[0] : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
