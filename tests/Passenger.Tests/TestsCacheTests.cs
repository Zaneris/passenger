using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The tests a folder's test projects declare, kept in the state's cache as the folder's opening read them: the
/// next opening of the folder parses only the files changed since, and finds every test the first found and those the
/// change made, the tests of an attribute the project derives and those an abstract class leaves to the classes deriving
/// from it read as the first read them.</summary>
public sealed class TestsCacheTests : IDisposable
{
    private const int Sums = 12;

    private readonly string folder = PathNames.Canonical(TestFolders.Create("tests-cache-"));
    private readonly string state = PathNames.Canonical(TestFolders.Create("tests-cache-state-"));

    public TestsCacheTests()
    {
        TestFolders.Write(folder, "Cache.Tests/Cache.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n  </ItemGroup>\n</Project>\n");
        TestFolders.Write(folder, "Cache.Tests/MyFactAttribute.cs", "using Xunit;\n\nnamespace Cache.Tests;\n\npublic sealed class MyFactAttribute : FactAttribute\n{\n}\n");
        TestFolders.Write(folder, "Cache.Tests/OwnTests.cs", "namespace Cache.Tests;\n\npublic class OwnTests\n{\n    [MyFact]\n    public void Via_its_own_attribute() { }\n}\n");
        TestFolders.Write(folder, "Cache.Tests/BaseChecks.cs",
            "using Xunit;\n\nnamespace Cache.Tests;\n\npublic abstract class BaseChecks\n{\n    [Fact]\n    public void Shared() { }\n}\n");
        TestFolders.Write(folder, "Cache.Tests/ConcreteA.cs", "using Xunit;\n\nnamespace Cache.Tests;\n\npublic class ConcreteA : BaseChecks\n{\n    [Fact]\n    public void Own() { }\n}\n");
        TestFolders.Write(folder, "Cache.Tests/Helpers.cs", "namespace Cache.Tests;\n\npublic static class Helpers\n{\n    public static int Twice(int value) => value * 2;\n}\n");
        for (int i = 0; i < Sums; i++)
        {
            TestFolders.Write(folder, $"Cache.Tests/Sums{i}.cs", SumsFile(i, extra: false));
        }
        TestFolders.Write(folder, "Tests.slnx", "<Solution>\n  <Project Path=\"Cache.Tests/Cache.Tests.csproj\" />\n</Solution>\n");
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(state);
    }

    [AvaloniaFact]
    public void A_second_opening_parses_only_the_test_files_changed_since_and_finds_every_test_the_first_did()
    {
        (int firstParsed, string[] first, ulong? sharedCode) = Opened(Sums * 2 + 2);
        Assert.Contains("Cache.Tests.OwnTests.Via_its_own_attribute", first);
        Assert.Contains("Cache.Tests.ConcreteA.Own", first);
        Assert.DoesNotContain("Cache.Tests.BaseChecks.Shared", first);
        Assert.True(sharedCode is not null, "The abstract class's test was not read from its file at the first opening");
        Assert.True(firstParsed >= Sums + 5, $"The first opening parsed {firstParsed} files, where the project has {Sums + 5}");

        TestFolders.Write(folder, "Cache.Tests/Sums3.cs", SumsFile(3, extra: true));
        (int parsed, string[] second, ulong? sharedAgain) = Opened(Sums * 2 + 3);
        Assert.Equal([.. first.Append("Cache.Tests.Sums3.Adds_again").Order(StringComparer.Ordinal)], second);
        Assert.True(sharedAgain == sharedCode, "The abstract class's test, read from the cache, is not the one the first opening read");
        Assert.True(parsed == 1,
            $"The second opening parsed {parsed} files, where one changed since the first opening read every file of the project ({firstParsed} parses)");
    }

    /// <summary>The folder opened in a window keeping its state in the test's own folder, once the tests from code reach
    /// <paramref name="tests"/>: how many files it parsed, the tests found by full name, and the abstract class's test's
    /// code; the window closed after.</summary>
    private (int Parsed, string[] Tests, ulong? Shared) Opened(int tests)
    {
        MainWindow window = new() { Width = 1000, Height = 700, StartsLanguageServer = false, StateFolder = state };
        window.Show();
        window.OpenFolder(folder);
        window.Perform(AppActions.ShowTests);
        Ui.WaitFor(() => !window.Tests.Finder.IsLooking && window.Tests.Tree.Methods.Count == tests,
            () => $"{tests} tests from code, where the tree has [{string.Join(", ", window.Tests.Tree.Methods.Select(m => m.FullName))}]");
        Ui.Settle();
        (int, string[], ulong?) found = (window.Tests.Finder.Parsed, [.. window.Tests.Tree.Methods.Select(m => m.FullName ?? string.Empty).Order(StringComparer.Ordinal)],
            window.Tests.Finder.CodeOf(Path.Combine(folder, "Cache.Tests", "BaseChecks.cs"), "Cache.Tests.BaseChecks.Shared"));
        window.Close();
        Ui.Settle();
        return found;
    }

    private static string SumsFile(int i, bool extra) =>
        $"using Xunit;\n\nnamespace Cache.Tests;\n\npublic class Sums{i}\n{{\n    [Fact]\n    public void Adds{i}() => Assert.Equal({i + 1}, {i} + 1);\n\n" +
        $"    [Theory]\n    [InlineData(1, 2)]\n    public void Doubles{i}(int value, int twice) => Assert.Equal(twice, Helpers.Twice(value));\n" +
        (extra ? "\n    [Fact]\n    public void Adds_again() { }\n" : string.Empty) + "}\n";
}
