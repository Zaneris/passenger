using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Reqnroll solution the BDD tests take, on Reqnroll's xunit v3 with the tests' own xunit v3, VSTest adapter
/// and test SDK, restored from the package cache (tests/UnitTestGraphs/Reqnroll): Calc.Specs, whose Calculator feature has
/// a background, a scenario, an outline and a step no definition binds, and whose step definitions use Cucumber
/// Expressions and a regular expression; and Calc.Steps, a library it references holding a shared step, a hook and a
/// transformation.</summary>
public static class ReqnrollProjects
{
    public const string Feature =
        "@calc\n" +
        "Feature: Calculator\n" +
        "  Simple calculator for adding numbers\n" +
        "\n" +
        "Background:\n" +
        "  Given the calculator is cleared\n" +
        "\n" +
        "Scenario: Add two numbers\n" +
        "  Given the first number is 50\n" +
        "  And the second number is 70\n" +
        "  When the two numbers are added\n" +
        "  Then the result should be 120\n" +
        "\n" +
        "Scenario Outline: Add numbers\n" +
        "  Given the first number is <a>\n" +
        "  And the second number is <b>\n" +
        "  When the two numbers are added\n" +
        "  Then the result should be <sum>\n" +
        "\n" +
        "Examples:\n" +
        "  | a | b | sum |\n" +
        "  | 1 | 2 | 3   |\n" +
        "  | 5 | 5 | 10  |\n" +
        "\n" +
        "Scenario: Not yet written\n" +
        "  Given a step nobody wrote\n";

    public const string Steps =
        "namespace Calc.Specs.StepDefinitions;\n" +
        "\n" +
        "[Binding]\n" +
        "public sealed class CalculatorStepDefinitions\n" +
        "{\n" +
        "    private int first;\n" +
        "    private int second;\n" +
        "    private int result;\n" +
        "\n" +
        "    /// <summary>Keys the first number into the <see cref=\"CalculatorStepDefinitions\"/>.</summary>\n" +
        "    [Given(\"the first number is {int}\")]\n" +
        "    public void GivenTheFirstNumberIs(int number)\n" +
        "    {\n" +
        "        first = number;\n" +
        "    }\n" +
        "\n" +
        "    [Given(@\"the second number is (\\d+)\")]\n" +
        "    public void GivenTheSecondNumberIs(int number)\n" +
        "    {\n" +
        "        second = number;\n" +
        "    }\n" +
        "\n" +
        "    [When(\"the two numbers are added\")]\n" +
        "    public void WhenTheTwoNumbersAreAdded()\n" +
        "    {\n" +
        "        result = first + second;\n" +
        "    }\n" +
        "\n" +
        "    [Then(\"the result should be {int}\")]\n" +
        "    public void ThenTheResultShouldBe(int expected)\n" +
        "    {\n" +
        "        Assert.Equal(expected, result);\n" +
        "    }\n" +
        "\n" +
        "    [Then(\"nothing is ever said\")]\n" +
        "    public void ThenNothingIsEverSaid()\n" +
        "    {\n" +
        "    }\n" +
        "}\n";

    public const string Shared =
        "using Reqnroll;\n" +
        "\n" +
        "namespace Calc.Steps;\n" +
        "\n" +
        "[Binding]\n" +
        "public sealed class SharedSteps\n" +
        "{\n" +
        "    [StepDefinition(\"the calculator is cleared\")]\n" +
        "    public void Clear()\n" +
        "    {\n" +
        "    }\n" +
        "\n" +
        "    [BeforeScenario(\"@calc\", Order = 5)]\n" +
        "    public void Ready()\n" +
        "    {\n" +
        "    }\n" +
        "\n" +
        "    [StepArgumentTransformation(@\"(\\d+) percent\")]\n" +
        "    public double Percent(int value) => value / 100.0;\n" +
        "}\n";

    /// <summary>Writes the solution into a folder and gives what to restore, by the folder's real path, so the project the
    /// specs reference is never restored under two spellings at once; <paramref name="intermediate"/> has Reqnroll write its
    /// code-behind under obj (ReqnrollUseIntermediateOutputPathForCodeBehind).</summary>
    public static string Write(string folder, bool intermediate = false)
    {
        TestFolders.Write(folder, "Calc.slnx",
            "<Solution>\n  <Project Path=\"Calc.Specs/Calc.Specs.csproj\" />\n  <Project Path=\"Calc.Steps/Calc.Steps.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Calc.Specs/Calc.Specs.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n" +
            "    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n    <IsPackable>false</IsPackable>\n" +
            (intermediate ? "    <ReqnrollUseIntermediateOutputPathForCodeBehind>true</ReqnrollUseIntermediateOutputPathForCodeBehind>\n" : "") + "  </PropertyGroup>\n" +
            $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n" +
            $"    <PackageReference Include=\"Reqnroll.xunit.v3\" Version=\"{TestPackages.Reqnroll}\" />\n" +
            $"    <PackageReference Include=\"xunit.v3\" Version=\"{AvaloniaApp.Metadata("XunitVersion")}\" />\n" +
            $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{AvaloniaApp.Metadata("XunitRunnerVersion")}\" />\n" +
            "    <ProjectReference Include=\"../Calc.Steps/Calc.Steps.csproj\" />\n  </ItemGroup>\n</Project>\n");
        TestFolders.Write(folder, "Calc.Specs/Usings.cs", "global using Reqnroll;\nglobal using Xunit;\n");
        TestFolders.Write(folder, "Calc.Specs/reqnroll.json", "{\n  \"bindingAssemblies\": [ { \"assembly\": \"Calc.Steps\" } ]\n}\n");
        TestFolders.Write(folder, "Calc.Specs/Features/Calculator.feature", Feature);
        TestFolders.Write(folder, "Calc.Specs/StepDefinitions/CalculatorStepDefinitions.cs", Steps);
        TestFolders.Write(folder, "Calc.Steps/Calc.Steps.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n" +
            "    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n" +
            $"  <ItemGroup>\n    <PackageReference Include=\"Reqnroll\" Version=\"{TestPackages.Reqnroll}\" />\n  </ItemGroup>\n</Project>\n");
        TestFolders.Write(folder, "Calc.Steps/SharedSteps.cs", Shared);
        return Path.Combine(PathNames.Canonical(folder), "Calc.slnx");
    }
}
