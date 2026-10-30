using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A .NET program's own assembly is read from its command line one way, for the name Attach to Process shows and
/// for the container tools' look at whether it is a Release build.</summary>
public sealed class DotnetProcessTests
{
    [Theory]
    [InlineData("dotnet", "/usr/share/dotnet/dotnet /app/Shop.Api.dll --urls http://+:80", "/app/Shop.Api.dll", "Shop.Api")]
    [InlineData("dotnet", "dotnet exec --runtimeconfig x.json \"/app/my app/Shop.Api.dll\"", "/app/my app/Shop.Api.dll", "Shop.Api")]
    [InlineData("dotnet.exe", "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\My App\\Shop.Api.dll\"", "C:\\My App\\Shop.Api.dll", "Shop.Api")]
    [InlineData("dotnet", "dotnet watch run", null, "dotnet")]
    [InlineData("Shop.Api", "\"/app/my app/Shop.Api\" --verbose", "/app/my app/Shop.Api.dll", "Shop.Api")]
    [InlineData("Passenger.Tests.exe", "C:\\work\\Passenger.Tests.exe", "C:\\work\\Passenger.Tests.dll", "Passenger.Tests")]
    public void A_programs_assembly_and_name_come_from_its_command_line(string name, string commandLine, string? assembly, string program)
    {
        DotnetProcess process = new(1, name, commandLine, "me", IsMine: true);
        Assert.Equal(assembly, process.Assembly);
        Assert.Equal(program, process.Program);
    }
}
