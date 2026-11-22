using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>What a Dockerfile's build names, read as its build gives it: each ARG and ENV variable put in from the build's
/// arguments, else an ARG's default, else what an ENV set it to; and the assembly its last ENTRYPOINT runs with dotnet, in
/// the exec form, a shell's -c and the shell form.</summary>
public sealed class DockerfileStagesTests
{
    private const string Shared = """
        FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
        ARG ASSEMBLY_NAME
        ARG PROJECT_FOLDER=.
        RUN dotnet publish "$PROJECT_FOLDER/${ASSEMBLY_NAME}.csproj" -o /app/publish

        FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
        ARG ASSEMBLY_NAME
        ENV ENTRY_ASSEMBLY=${ASSEMBLY_NAME} PORT=${HTTP_PORT:-8080}
        ENTRYPOINT exec dotnet "$ENTRY_ASSEMBLY.dll"
        """;

    [Fact]
    public void A_Dockerfiles_build_is_read_with_its_variables_put_in_and_the_assembly_its_ENTRYPOINT_runs_in_each_form() => Cases.All(
        ("each variable takes the builds argument else the ARGs default else its ENV and one none sets stays", Each_variable_takes_the_builds_argument_else_the_ARGs_default_else_its_ENV_and_one_none_sets_stays),
        ("the assembly an ENTRYPOINT runs with dotnet is read in each form", () => Cases.Each<string>(
        [
            "ENTRYPOINT [\"dotnet\", \"/app/Foo.WebApi.dll\"]",
            "ENTRYPOINT [\"sh\", \"-c\", \"exec dotnet Foo.WebApi.dll --urls http://+:8080\"]",
            "ENTRYPOINT dotnet /app/Foo.WebApi.dll",
            "ENTRYPOINT exec dotnet \"Foo.WebApi.dll\"",
        ], The_assembly_an_ENTRYPOINT_runs_with_dotnet_is_read_in_each_form)),
        ("an ENTRYPOINT of another program runs no assembly", An_ENTRYPOINT_of_another_program_runs_no_assembly));

    private void Each_variable_takes_the_builds_argument_else_the_ARGs_default_else_its_ENV_and_one_none_sets_stays()
    {
        string given = DockerfileStages.Expanded(Shared, new Dictionary<string, string> { ["ASSEMBLY_NAME"] = "Foo.WebApi" });
        Assert.Contains("dotnet publish \"./Foo.WebApi.csproj\"", given, StringComparison.Ordinal);
        Assert.Contains("ENV ENTRY_ASSEMBLY=Foo.WebApi PORT=8080", given, StringComparison.Ordinal);
        Assert.Contains("exec dotnet \"Foo.WebApi.dll\"", given, StringComparison.Ordinal);
        string folder = DockerfileStages.Expanded(Shared, new Dictionary<string, string> { ["ASSEMBLY_NAME"] = "Api", ["PROJECT_FOLDER"] = "src/Api" });
        Assert.Contains("dotnet publish \"src/Api/Api.csproj\"", folder, StringComparison.Ordinal);
        Assert.Contains("dotnet publish \"./${ASSEMBLY_NAME}.csproj\"", DockerfileStages.Expanded(Shared, new Dictionary<string, string>()), StringComparison.Ordinal);
    }

    private void The_assembly_an_ENTRYPOINT_runs_with_dotnet_is_read_in_each_form(string entrypoint)
    {
        Assert.Equal("Foo.WebApi", DockerfileStages.Read($"FROM mcr.microsoft.com/dotnet/aspnet:10.0\n{entrypoint}\n").Final?.EntryAssembly);
    }

    private void An_ENTRYPOINT_of_another_program_runs_no_assembly()
    {
        Assert.Null(DockerfileStages.Read("FROM busybox:1.38\nENTRYPOINT [\"sh\", \"-c\", \"sleep 600\"]\n").Final?.EntryAssembly);
    }
}
