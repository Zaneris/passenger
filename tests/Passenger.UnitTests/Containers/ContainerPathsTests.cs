using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>A container's paths map to the context's files a Dockerfile's COPY lines copied: a source named from the
/// context with or without <c>./</c>, and one whose name begins with a dot kept as that folder, never another.</summary>
public sealed class ContainerPathsTests
{
    private static readonly string Context = Path.Combine(Path.GetTempPath(), "shop");

    [Fact]
    public void A_copy_of_a_dot_folder_maps_the_images_folder_to_the_contexts_dot_folder()
    {
        ContainerPaths map = Map("COPY .config/ /app/.config/\n");
        Assert.Contains(("/app/.config/", Path.Combine(Context, ".config")), map.Pairs);
        Assert.Equal(Path.Combine(Context, ".config", "settings.json"), map.ToHost("/app/.config/settings.json"));
        Assert.Equal("/app/.config/settings.json", map.ToContainer(Path.Combine(Context, ".config", "settings.json")));
    }

    [Fact]
    public void A_source_from_the_contexts_root_maps_alike_written_with_or_without_its_leading_dot_slash()
    {
        foreach (string source in (string[])["src/", "./src/", "/src/", "././src/"])
        {
            Assert.Contains(("/work/src/", Path.Combine(Context, "src")), Map($"COPY {source} /work/src/\n").Pairs);
        }
        Assert.Contains(("/app/", Context), Map("COPY ./ /app/\n").Pairs);
        Assert.Contains(("/app/", Context), Map("COPY . /app/\n").Pairs);
        Assert.Contains(("/src/.github/", Path.Combine(Context, ".github")), Map("COPY [\"./.github/ci.yml\", \"/src/.github/\"]\n").Pairs);
    }

    private static ContainerPaths Map(string copies) =>
        ContainerPaths.For(DockerfileStages.Read($"FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\n{copies}"), Context);
}
