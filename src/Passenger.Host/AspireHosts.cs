namespace Passenger.Host;

/// <summary>What makes a project or a file-based program an Aspire AppHost, one definition for both ends (the app compiles
/// this file too): Aspire's AppHost package, whose props make whatever references it an AppHost unless it says otherwise,
/// or Aspire's AppHost SDK, which adds that package itself.</summary>
public static class AspireHosts
{
    /// <summary>The AppHost package: its props set <see cref="Property"/> once a project is restored.</summary>
    public const string Package = "Aspire.Hosting.AppHost";

    /// <summary>The AppHost SDK, which sets <see cref="Property"/> and references <see cref="Package"/> itself.</summary>
    public const string Sdk = "Aspire.AppHost.Sdk";

    /// <summary>The property an AppHost's evaluation sets.</summary>
    public const string Property = "IsAspireHost";

    /// <summary>Whether a program's head (<see cref="ProgramFiles.Head(string)"/>) makes it an AppHost: the SDK's <c>#:sdk</c>
    /// or the package's <c>#:package</c> directive, at any version.</summary>
    public static bool IsHead(IEnumerable<string> head) => head.Any(directive => ProgramFiles.Parse(directive) switch
    {
        ("sdk", { } sdk) => Names(sdk, Sdk),
        ("package", { } package) => Names(package, Package),
        _ => false,
    });

    /// <summary>Whether a project's evaluation is an AppHost's, by its <see cref="Property"/> and the packages it references
    /// itself: said true, or unsaid where it references <see cref="Package"/>, whose props say true once it is restored.</summary>
    public static bool Is(string property, IEnumerable<string> packages) => property.Equals("true", StringComparison.OrdinalIgnoreCase)
        || property.Length == 0 && packages.Any(package => package.Equals(Package, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first Aspire whose AppHost adds its services by path alone, referencing none of them.</summary>
    public const int AddsByPathFrom = 17;

    /// <summary>Whether an AppHost of an Aspire version (its AppHost package's or SDK's) adds its services by path alone
    /// (<see cref="AddsByPathFrom"/>); false for a version unknown.</summary>
    public static bool AddsByPath(string? version) => version?.Split('.')[0] is { } major
        && int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) && number >= AddsByPathFrom;

    private static bool Names(string value, string name) => value.Split('@')[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
}
