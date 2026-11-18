using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Aspire;

/// <summary>The services an AppHost names, read from its syntax alone: AddProject's by a path or a Projects type and Aspire
/// 17's AddDotnetProject's by a path, their arguments by name or by place, and the path taken as Aspire takes it, a folder
/// by the one project in it.</summary>
public sealed class AspireServicesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static Microsoft.CodeAnalysis.SyntaxNode Root(string code) =>
        CSharpSyntaxTree.ParseText(code, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);

    [Fact]
    public void Both_calls_are_read_for_their_names_and_paths_by_place_or_by_name_and_a_Projects_type_for_its_type()
    {
        const string Host = """
            var builder = DistributedApplication.CreateBuilder(args);
            var api = builder.AddDotnetProject("api", "../Api/Api.csproj", options => options.LaunchProfileName = "http");
            builder.AddDotnetProject(path: "../Worker", name: "worker");
            builder.AddProject("web", "../Web/Web.csproj", launchProfileName: "http");
            builder.AddProject<Projects.Admin>("admin");
            builder.AddRedis("cache");
            builder.Build().Run();
            """;
        AspireServices.Service[] services = [.. AspireServices.In(Root(Host))];
        Assert.Equal([("api", "../Api/Api.csproj", null, "AddDotnetProject"), ("worker", "../Worker", null, "AddDotnetProject"),
            ("web", "../Web/Web.csproj", null, "AddProject"), ("admin", null, "Projects.Admin", "AddProject")],
            services.Select(s => (AspireServices.Written(s.Name), AspireServices.Written(s.Path), s.Type?.ToString(), s.Adding.Method)));
        Assert.All(services, s => Assert.True(s.Placed, $"{s.Call} was read as giving an argument its call has not"));
    }

    [Theory]
    [InlineData("builder.AddDotnetProject(\"api\", \"../Api/Api.csproj\", \"http\", \"more\")")]
    [InlineData("builder.AddDotnetProject(projectPath: \"../Api/Api.csproj\", name: \"api\")")]
    [InlineData("builder.AddProject<Projects.Api>(\"api\", projectPath: \"../Api/Api.csproj\")")]
    [InlineData("builder.AddProject(path: \"../Api/Api.csproj\", \"api\")")]
    public void An_argument_neither_call_takes_there_leaves_the_call_unplaced(string call)
    {
        AspireServices.Service service = Assert.Single(AspireServices.In(Root(call + ";")));
        Assert.False(service.Placed, $"{call} was read as if each argument were one its call takes there");
    }

    [Fact]
    public void A_path_is_taken_from_the_AppHosts_folder_a_folder_by_the_one_project_in_it()
    {
        string host = Directory.CreateDirectory(Path.Join(root, "Host")).FullName;
        Directory.CreateDirectory(Path.Join(root, "Api"));
        File.WriteAllText(Path.Join(root, "Api", "Api.csproj"), "<Project />");
        File.WriteAllText(Path.Join(root, "Api", "Notes.txt"), string.Empty);
        Directory.CreateDirectory(Path.Join(root, "Two"));
        File.WriteAllText(Path.Join(root, "Two", "One.csproj"), "<Project />");
        File.WriteAllText(Path.Join(root, "Two", "Other.fsproj"), "<Project />");
        Assert.Equal(Path.Join(root, "Api", "Api.csproj"), AspireServices.ProjectOf("../Api/Api.csproj", host));
        Assert.Equal(Path.Join(root, "Api", "Api.csproj"), AspireServices.ProjectOf("../Api", host));
        Assert.Equal(Path.Join(root, "Api", "Api.csproj"), AspireServices.ProjectOf(Path.Join(root, "Api"), null));
        Assert.Equal(Path.Join(root, "Tool.cs"), AspireServices.ProjectOf("../Tool.cs", host));
        Assert.Null(AspireServices.ProjectOf("../Two", host));
        Assert.Null(AspireServices.ProjectOf("../Api/Api.csproj", null));
    }
}
