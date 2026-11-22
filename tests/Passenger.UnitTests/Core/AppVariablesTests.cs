using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What an app's environment variables say of it, as ASP.NET Core reads them: the environment it runs in,
/// ASPNETCORE_ENVIRONMENT before DOTNET_ENVIRONMENT, and whether Kestrel serves HTTPS.</summary>
public class AppVariablesTests
{
    [Fact]
    public void An_apps_environment_and_HTTPS_are_read_from_its_variables_as_ASP_NET_Core_reads_them() => Cases.All(
        ("the environment is ASPNETCORE ENVIRONMENTs before DOTNET ENVIRONMENTs", () => Cases.Each<(string?, string?, string)>(
        [
            ("Staging", "Development", "Staging"),
            (null, "Production", "Production"),
            ("  ", null, "Development"),
        ], row => The_environment_is_ASPNETCORE_ENVIRONMENTs_before_DOTNET_ENVIRONMENTs(row.Item1, row.Item2, row.Item3))),
        ("an app serves HTTPS where Kestrel is told an https address or an HTTPS port", () => Cases.Each<(string?, string?, bool)>(
        [
            ("HTTPS://localhost:7001", null, true),
            ("http://+:8080", null, false),
            (null, "8081", true),
            (null, "", false),
        ], row => An_app_serves_HTTPS_where_Kestrel_is_told_an_https_address_or_an_HTTPS_port(row.Item1, row.Item2, row.Item3))),
        ("https addresses are served as http on the same ports", Https_addresses_are_served_as_http_on_the_same_ports));

    private void The_environment_is_ASPNETCORE_ENVIRONMENTs_before_DOTNET_ENVIRONMENTs(string? aspNetCore, string? dotnet, string runsIn)
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

    private void An_app_serves_HTTPS_where_Kestrel_is_told_an_https_address_or_an_HTTPS_port(string? urls, string? httpsPorts, bool serves)
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

    private void Https_addresses_are_served_as_http_on_the_same_ports()
    {
        Assert.Equal("http://localhost:7001;http://localhost:5000", AspNetCoreVariables.AsHttp("HTTPS://localhost:7001;http://localhost:5000"));
    }
}
