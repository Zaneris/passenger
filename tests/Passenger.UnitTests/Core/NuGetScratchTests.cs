using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>NuGet's scratch folder as NuGet names it, which a program given a temporary folder of its own is given by name
/// (NUGET_SCRATCH), so its restores take the same extraction locks as Passenger's builds.</summary>
public class NuGetScratchTests
{
    [Fact]
    public void The_scratch_is_NuGetScratch_in_the_temporary_folder_on_linux_with_the_users_name()
    {
        Assert.Equal(Path.Combine("/var/folders/ab/T/", "NuGetScratch"), NuGetScratch.In("/var/folders/ab/T/", linux: false, "ted"));
        Assert.Equal(Path.Combine("/tmp/", "NuGetScratchted"), NuGetScratch.In("/tmp/", linux: true, "ted"));
    }

    [Fact]
    public void The_scratch_a_program_is_given_by_name_is_the_one_NuGet_takes_in_this_process()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(NuGetScratch.Variable) is { Length: > 0 },
            $"{NuGetScratch.Variable} names this process's scratch, which NuGet then takes as it is, and a program is not given another");
        string taken = NuGet.Common.NuGetEnvironment.GetFolderPath(NuGet.Common.NuGetFolderPath.Temp);
        Assert.True(NuGetScratch.Shared() == taken,
            $"A C# language server's restores would take {NuGetScratch.Shared()} for NuGet's scratch, where Passenger's own take {taken}: their extraction locks apart");
    }
}
