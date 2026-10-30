using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What an app's environment variables say of it, as ASP.NET Core reads them: the environment it runs in,
/// ASPNETCORE_ENVIRONMENT before DOTNET_ENVIRONMENT, and whether Kestrel serves HTTPS.</summary>
public class AppVariablesTests
{
    [Theory]
    [InlineData("Staging", "Development", "Staging")]
    [InlineData(null, "Production", "Production")]
    [InlineData("", "Staging", "Staging")]
    [InlineData("  ", null, "Development")]
    [InlineData(null, null, "Development")]
    public void The_environment_is_ASPNETCORE_ENVIRONMENTs_before_DOTNET_ENVIRONMENTs(string? aspNetCore, string? dotnet, string runsIn)
    {
        // DOTNET_ENVIRONMENT first, as a launch profile or compose file may list it.
        Dictionary<string, string> variables = [];
        if (dotnet is not null)
        {
            variables[AspNetCoreVariables.DotnetEnvironment] = dotnet;
        }
        if (aspNetCore is not null)
        {
            variables[AspNetCoreVariables.AspNetCoreEnvironment] = aspNetCore;
        }
        string read = AspNetCoreVariables.EnvironmentOf(variables.GetValueOrDefault, "Development");
        Assert.True(read == runsIn, $"With ASPNETCORE_ENVIRONMENT {aspNetCore ?? "unset"} and DOTNET_ENVIRONMENT {dotnet ?? "unset"} the app was read as "
            + $"running in {read}, not {runsIn}, so its appsettings.{runsIn}.json, user secrets and static web assets would be taken or left wrongly");
    }

    [Theory]
    [InlineData("https://+:443;http://+:80", null, true)]
    [InlineData("HTTPS://localhost:7001", null, true)]
    [InlineData("http://+:8080", null, false)]
    [InlineData(null, "8081", true)]
    [InlineData(null, "", false)]
    [InlineData(null, null, false)]
    public void An_app_serves_HTTPS_where_Kestrel_is_told_an_https_address_or_an_HTTPS_port(string? urls, string? httpsPorts, bool serves)
    {
        Dictionary<string, string> variables = [];
        if (urls is not null)
        {
            variables[AspNetCoreVariables.Urls] = urls;
        }
        if (httpsPorts is not null)
        {
            variables[AspNetCoreVariables.HttpsPorts] = httpsPorts;
        }
        Assert.True(AspNetCoreVariables.ServesHttps(variables) == serves,
            $"An app with ASPNETCORE_URLS {urls ?? "unset"} and ASPNETCORE_HTTPS_PORTS {httpsPorts ?? "unset"} was {(serves ? "not " : string.Empty)}taken to serve HTTPS, "
            + $"so the development certificate would be {(serves ? "missing" : "given for nothing")}");
    }

    [Fact]
    public void Https_addresses_are_served_as_http_on_the_same_ports()
    {
        Assert.Equal("http://localhost:7001;http://localhost:5000", AspNetCoreVariables.AsHttp("HTTPS://localhost:7001;http://localhost:5000"));
    }
}
