using Xunit.v3;

namespace Passenger.Tests;

/// <summary>A report class that reaches macOS's own behaviour: macOS's report lane runs only these, by their trait,
/// AboutMacOS=true (tools/test-shards.py's MAC_OS), and Windows' and Linux's run them too. Every other report class runs on
/// Windows and Linux alone, which stand in for macOS there. <see cref="AboutMacOSRulesTests"/> holds from the built tests
/// that a class reaching what <see cref="AboutMacOSRulesTests"/>' table names carries the mark, and that a class
/// carrying it either reaches it or says, in <see cref="Why"/>, what of macOS's its subject is.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AboutMacOSAttribute(string? why = null) : Attribute, ITraitAttribute
{
    public const string Name = "AboutMacOS";

    /// <summary>What of macOS's own behaviour the class's subject is where its code reaches nothing the table names: what
    /// the IDE does on its behalf (its watcher, its menus), never what the test calls itself.</summary>
    public string? Why { get; } = why;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(Name, "true")];
}
