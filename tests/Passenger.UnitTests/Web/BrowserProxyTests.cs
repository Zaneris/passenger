using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Web;

/// <summary>The browser debug proxy of a project restored for several frameworks, from an assets file and a package folder of
/// a test's own: the one the launch's framework restored, whichever comes first; none where that framework restored none,
/// though another did; a framework the project lacks by the nearest it can reference; and a project of one by its one.</summary>
public sealed class BrowserProxyTests : IDisposable
{
    private const string Pack = "Microsoft.NET.Sdk.WebAssembly.Pack";

    private const string Tools = "WasmAppHost";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>The proxy of a version in the package folder.</summary>
    private string Proxy(string version)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "packages", Pack.ToLowerInvariant(), version, Tools)).FullName;
        string proxy = Path.Combine(folder, BrowserProxy.FileName);
        File.WriteAllText(proxy, string.Empty);
        return proxy;
    }

    /// <summary>A project whose assets file says the pack's version each framework restored, none for one given null.</summary>
    private ProjectModel Restored(params (string Framework, string? Version)[] frameworks)
    {
        IEnumerable<string> targets = frameworks.Select(f => $"\"{f.Framework}\": {{ {(f.Version is { } version ? $"\"{Pack}/{version}\": {{ \"type\": \"package\" }}" : string.Empty)} }}");
        IEnumerable<string> named = frameworks.Select(f => $"\"{f.Framework}\": {{ \"targetAlias\": \"{f.Framework}\" }}");
        string assets = Path.Combine(root, "project.assets.json");
        File.WriteAllText(assets, $"{{ \"targets\": {{ {string.Join(", ", targets)} }}, \"project\": {{ \"frameworks\": {{ {string.Join(", ", named)} }} }} }}");
        string path = Path.Combine(root, "Wasm.csproj");
        return new ProjectModel(path, "Wasm", "Exe", [.. frameworks.Select(f => f.Framework)], [], [], true)
        {
            Evaluated = new EvaluatedProject
            {
                Path = path,
                Properties = new Dictionary<string, EvaluatedProperty>
                {
                    [CarriedProperties.NuGetPackageRoot] = new(Path.Combine(root, "packages"), false),
                    [CarriedProperties.ProjectAssetsFile] = new(assets, false),
                },
            },
        };
    }

    /// <summary>A launch takes the browser debug proxy its framework restored, else none.</summary>
    [Fact]
    public void A_launch_takes_the_browser_proxy_its_framework_restored() => Cases.All(
        (nameof(A_launch_of_a_later_framework_takes_the_proxy_that_framework_restored),
            () => Apart(test => test.A_launch_of_a_later_framework_takes_the_proxy_that_framework_restored())),
        (nameof(A_launch_whose_framework_restored_none_is_given_no_other_frameworks_proxy),
            () => Apart(test => test.A_launch_whose_framework_restored_none_is_given_no_other_frameworks_proxy())),
        (nameof(A_project_of_one_framework_takes_its_proxy_whatever_the_launch_says),
            () => Apart(test => test.A_project_of_one_framework_takes_its_proxy_whatever_the_launch_says())),
        (nameof(A_launch_builds_the_framework_a_reference_would_take), A_launch_builds_the_framework_a_reference_would_take));

    /// <summary>Runs a check on a test of its own, as each ran before, its state its own.</summary>
    private static void Apart(Action<BrowserProxyTests> check)
    {
        using BrowserProxyTests test = new();
        check(test);
    }

    private void A_launch_of_a_later_framework_takes_the_proxy_that_framework_restored()
    {
        Proxy("8.0.20");
        string ten = Proxy("10.0.5");
        ProjectModel project = Restored(("net8.0", "8.0.20"), ("net10.0", "10.0.5"));

        string? found = BrowserProxy.Of(project, "net10.0", [Pack], Tools);
        Assert.True(found == ten, $"A net10.0 launch took {found}, so its page's runtime would be debugged by another version's proxy");
    }

    private void A_launch_whose_framework_restored_none_is_given_no_other_frameworks_proxy()
    {
        Proxy("8.0.20");
        ProjectModel project = Restored(("net8.0", "8.0.20"), ("net10.0", null));

        Assert.Null(BrowserProxy.Of(project, "net10.0", [Pack], Tools));
    }

    private void A_project_of_one_framework_takes_its_proxy_whatever_the_launch_says()
    {
        string ten = Proxy("10.0.5");
        ProjectModel project = Restored(("net10.0", "10.0.5"));

        Assert.True(BrowserProxy.Of(project, "net11.0", [Pack], Tools) == ten, "A project of one framework found no proxy under a server's newer framework");
    }

    private static void A_launch_builds_the_framework_a_reference_would_take() => Cases.Each<(string? Launched, string? Built)>(
    [
        ("net10.0", "net10.0"),
        ("NET10.0", "net10.0"),
        ("net9.0", "net8.0"),
        ("net7.0", null),
        (null, "net8.0"),
    ], row => Apart(test => test.A_launch_builds_the_framework_a_reference_would_take(row.Launched, row.Built)));

    private void A_launch_builds_the_framework_a_reference_would_take(string? launched, string? built)
    {
        string? taken = LaunchedFrameworks.Of(["net8.0", "net10.0"], launched);
        Assert.True(taken == built, $"A launch of {launched ?? "no framework"} took {taken ?? "none"}, not {built ?? "none"}");
    }

}
