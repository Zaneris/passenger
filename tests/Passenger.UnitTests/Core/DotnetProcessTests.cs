using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A .NET program's own assembly is read from its command line one way, for the name Attach to Process shows and
/// for the container tools' look at whether it is a Release build: the dotnet host's, or Passenger's helper host's. How a
/// process started is read from the system's record of it, its arguments then its environment.</summary>
public sealed class DotnetProcessTests
{
    [Fact]
    public void A_dotnet_programs_assembly_name_and_build_are_read_from_its_command_line() => Cases.All(
        ("A processs arguments and environment are read from the systems record of its start", A_processs_arguments_and_environment_are_read_from_the_systems_record_of_its_start),
        ("A programs assembly and name come from its command line", A_programs_assembly_and_name_come_from_its_command_line),
        ("Processes are asked for their runtimes files only while new so a look costs as much among many as among few", Processes_are_asked_for_their_runtimes_files_only_while_new_so_a_look_costs_as_much_among_many_as_among_few));

    /// <summary>Passenger looks each second for the .NET processes its lineage started apart: once every process is past its
    /// first few looks, a look asks none of them anything and makes nothing among 2,000 processes as among 10, where it
    /// asked each its start and made two paths to ask the disk of; a process come since, or making its files late in its
    /// first looks, is still found, and one gone forgotten.</summary>
    private void Processes_are_asked_for_their_runtimes_files_only_while_new_so_a_look_costs_as_much_among_many_as_among_few()
    {
        HashSet<int> holding = [7, 1007];
        int asked = 0;
        RuntimeFileLooks looks = new(id => id * 10L, (id, started) =>
        {
            asked++;
            return started == id * 10L && holding.Contains(id);
        });
        int[] few = [.. Enumerable.Range(1, 10)];
        int[] many = [.. Enumerable.Range(1, 2_000)];
        List<(int Id, long Started)> found = new(16);
        for (int i = 0; i < RuntimeFileLooks.Asked; i++)
        {
            found.Clear();
            looks.Look(many, found);
        }
        Assert.Equal([(7, 70L), (1007, 10_070L)], found);
        asked = 0;
        found.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        looks.Look(many, found);
        long all = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(asked == 0 && all == 0, $"A look among 2,000 processes past their first looks asked {asked} of them for their files and allocated {all:N0} bytes");

        // Those not listed are forgotten: listed again (a process new under its id), each is asked afresh.
        found.Clear();
        looks.Look(few, found);
        Assert.Equal([(7, 70L)], found);
        holding.Add(2_001);
        found.Clear();
        looks.Look([.. many, 2_001], found);
        Assert.Contains((2_001, 20_010L), found);
        Assert.True(asked == 1_991, $"Listed again after a look that left them out, {asked} processes were asked for their files, not the 1,990 forgotten and the one new");
    }

    private void A_processs_arguments_and_environment_are_read_from_the_systems_record_of_its_start()
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

    private void A_programs_assembly_and_name_come_from_its_command_line() => Cases.Each<(string name, string commandLine, string? assembly, string program)>(
    [
        ("dotnet", "/usr/share/dotnet/dotnet /app/Shop.Api.dll --urls http://+:80", "/app/Shop.Api.dll", "Shop.Api"),
        ("dotnet.exe", "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\My App\\Shop.Api.dll\"", "C:\\My App\\Shop.Api.dll", "Shop.Api"),
        ("dotnet", "/usr/share/dotnet/dotnet run --no-build -e DOTNET_WATCH=1 -e DOTNET_STARTUP_HOOKS=/usr/share/dotnet/sdk/10.0.404/DotnetTools/dotnet-watch/10.0.404/tools/net10.0/any/hotreload/net10.0/Microsoft.Extensions.DotNetDeltaApplier.dll", null, "dotnet"),
        ("dotnet", "/usr/share/dotnet/dotnet exec --fx-version 10.0.15 /opt/passenger/passenger-helper.dll /opt/passenger/debugger/Passenger.DebugAdapter.dll", "/opt/passenger/debugger/Passenger.DebugAdapter.dll", "Passenger.DebugAdapter"),
        ("Passenger.Tests.exe", "C:\\work\\Passenger.Tests.exe", "C:\\work\\Passenger.Tests.dll", "Passenger.Tests"),
        ("passenger-helper", "/opt/passenger/passenger-helper /opt/passenger/host/Passenger.Host.dll --folder /src", "/opt/passenger/host/Passenger.Host.dll", "Passenger.Host"),
        ("passenger-helper.exe", "\"C:\\Passenger\\passenger-helper.exe\" C:\\Passenger\\debugger\\Passenger.DebugAdapter.dll", "C:\\Passenger\\debugger\\Passenger.DebugAdapter.dll", "Passenger.DebugAdapter"),
    ], row =>
    {
        (string name, string commandLine, string? assembly, string program) = row;
        DotnetProcess process = new(1, name, commandLine, "me", IsMine: true);
        Assert.Equal(assembly, process.Assembly);
        Assert.Equal(program, process.Program);
    });
}
