using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A .NET program's own assembly is read from its command line one way, for the name Attach to Process shows and
/// for the container tools' look at whether it is a Release build: the dotnet host's, or Passenger's helper host's. How a
/// process started is read from the system's record of it, its arguments then its environment.</summary>
public sealed class DotnetProcessTests
{
    [Fact]
    public void A_processs_arguments_and_environment_are_read_from_the_systems_record_of_its_start()
    {
        byte[] record = [.. BitConverter.GetBytes(2), .. Text("/Apps/Shop.app/Contents/MacOS/Shop"), 0, 0, 0, .. Text("Shop"), .. Text("-v"),
            .. Text("HOME=/Users/a"), .. Text("PASSENGER_APP_START=A1B2"), 0, .. Text("executable_path=/Apps/Shop.app/Contents/MacOS/Shop")];

        (IReadOnlyList<string> arguments, IReadOnlyList<string> environment) = DotnetProcesses.Started(record);

        Assert.Equal(["Shop", "-v"], arguments);
        Assert.Equal(["HOME=/Users/a", "PASSENGER_APP_START=A1B2"], environment);
        (IReadOnlyList<string> cut, IReadOnlyList<string> none) = DotnetProcesses.Started([.. BitConverter.GetBytes(3), .. Text("/bin/x"), .. Text("Shop")]);
        Assert.Equal(["Shop"], cut);
        Assert.Empty(none);
        Assert.Empty(DotnetProcesses.Started([1, 0]).Arguments);

        static byte[] Text(string text) => [.. System.Text.Encoding.UTF8.GetBytes(text), 0];
    }

    [Theory]
    [InlineData("dotnet", "/usr/share/dotnet/dotnet /app/Shop.Api.dll --urls http://+:80", "/app/Shop.Api.dll", "Shop.Api")]
    [InlineData("dotnet", "dotnet exec --runtimeconfig x.json \"/app/my app/Shop.Api.dll\"", "/app/my app/Shop.Api.dll", "Shop.Api")]
    [InlineData("dotnet.exe", "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\My App\\Shop.Api.dll\"", "C:\\My App\\Shop.Api.dll", "Shop.Api")]
    [InlineData("dotnet", "dotnet watch run", null, "dotnet")]
    [InlineData("Shop.Api", "\"/app/my app/Shop.Api\" --verbose", "/app/my app/Shop.Api.dll", "Shop.Api")]
    [InlineData("Passenger.Tests.exe", "C:\\work\\Passenger.Tests.exe", "C:\\work\\Passenger.Tests.dll", "Passenger.Tests")]
    [InlineData("passenger-helper", "/opt/passenger/passenger-helper /opt/passenger/host/Passenger.Host.dll --folder /src", "/opt/passenger/host/Passenger.Host.dll", "Passenger.Host")]
    [InlineData("dotnet", "dotnet exec --fx-version 11.0.0 /work/passenger-helper.dll /work/roslyn/Microsoft.CodeAnalysis.LanguageServer.dll --stdio",
        "/work/roslyn/Microsoft.CodeAnalysis.LanguageServer.dll", "Microsoft.CodeAnalysis.LanguageServer")]
    [InlineData("passenger-helper.exe", "\"C:\\Passenger\\passenger-helper.exe\" C:\\Passenger\\debugger\\Passenger.DebugAdapter.dll", "C:\\Passenger\\debugger\\Passenger.DebugAdapter.dll", "Passenger.DebugAdapter")]
    public void A_programs_assembly_and_name_come_from_its_command_line(string name, string commandLine, string? assembly, string program)
    {
        DotnetProcess process = new(1, name, commandLine, "me", IsMine: true);
        Assert.Equal(assembly, process.Assembly);
        Assert.Equal(program, process.Program);
    }
}
