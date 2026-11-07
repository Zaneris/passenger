using NuGet.Configuration;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>A nuget.config too deep for NuGet's reader said not to read, before it ends the process, in the folder asked and
/// those above it.</summary>
public sealed class NuGetSettingsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData("")]
    [InlineData("src/App")]
    public void A_config_nested_past_what_is_read_is_said_not_to_read_and_ends_nothing(string below)
    {
        string nested = string.Concat(Enumerable.Repeat("<a>", 100_000)) + string.Concat(Enumerable.Repeat("</a>", 100_000));
        File.WriteAllText(Path.Combine(root, "nuget.config"), $"<configuration><a>{nested}</a></configuration>");
        string folder = Path.Combine(root, below);
        Directory.CreateDirectory(folder);
        SmallStack.Run(() =>
        {
            Assert.Throws<NuGetConfigurationException>(() => NuGetSettings.Load(folder));
            Assert.Contains("nested too deeply", PackageSourceSet.Load(folder).Problem, StringComparison.Ordinal);
        });
    }
}
