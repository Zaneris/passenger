using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>An image reference as a Dockerfile's FROM, a compose file's image, a push and a login read it: its registry, its
/// path and its repository, Docker Hub by every name it answers to, and the page Go to Declaration opens for it.</summary>
public sealed class ImageReferenceTests
{
    [Fact]
    public void An_image_reference_is_read_as_its_registry_path_and_repository_Docker_Hub_by_every_name_and_its_page_found() => Cases.All(
        ("a reference is read as its registry path and repository", () => Cases.Each<(string, string?, string, string)>(
        [
            ("nginx", null, "nginx", "nginx"),
            ("bitnami/redis:7.2@sha256:0123abcd", null, "bitnami/redis", "bitnami/redis"),
            ("mcr.microsoft.com/dotnet/aspnet:10.0", "mcr.microsoft.com", "dotnet/aspnet", "mcr.microsoft.com/dotnet/aspnet"),
            ("localhost:5000/shop/web", "localhost:5000", "shop/web", "localhost:5000/shop/web"),
            ("localhost/web", "localhost", "web", "localhost/web"),
            ("localhost:5000", null, "localhost", "localhost"),
        ], row => A_reference_is_read_as_its_registry_path_and_repository(row.Item1, row.Item2, row.Item3, row.Item4))),
        ("every name Docker Hub answers to is Docker Hub", () => Cases.Each<string>(
        [
            "docker.io",
            "index.docker.io",
            "Docker.IO",
        ], Every_name_Docker_Hub_answers_to_is_Docker_Hub)),
        ("another registry is not Docker Hub", Another_registry_is_not_Docker_Hub),
        ("an images page is its registrys Docker Hubs by any of its names", () => Cases.Each<(string, string)>(
        [
            ("postgres:17", "https://hub.docker.com/_/postgres"),
            ("docker.io/library/nginx:1.27", "https://hub.docker.com/_/nginx"),
            ("registry.hub.docker.com/bitnami/redis:7.2", "https://hub.docker.com/r/bitnami/redis"),
            ("mcr.microsoft.com/dotnet/aspnet:10.0", "https://mcr.microsoft.com/artifact/mar/dotnet/aspnet"),
            ("ghcr.io/acme/web:1", "https://github.com/acme/web/pkgs/container/web"),
        ], row => An_images_page_is_its_registrys_Docker_Hubs_by_any_of_its_names(row.Item1, row.Item2))),
        ("an image a variable names has no page", () => Cases.Each<string>(
        [
            "${REGISTRY}/web:dev",
            "web:${TAG}",
        ], An_image_a_variable_names_has_no_page)));

    private void A_reference_is_read_as_its_registry_path_and_repository(string image, string? registry, string path, string repository)
    {
        ImageReference read = ImageReference.Parse(image);
        Assert.Equal((registry, path, repository), (read.Registry, read.Path, read.Repository));
    }

    private void Every_name_Docker_Hub_answers_to_is_Docker_Hub(string host)
    {
        Assert.True(ImageReference.IsDockerHub(host), $"{host} was not taken for Docker Hub: its login and its images' pages would be another registry's");
    }

    private void Another_registry_is_not_Docker_Hub()
    {
        Assert.False(ImageReference.IsDockerHub("hub.docker.example"));
        Assert.False(ImageReference.IsDockerHub("ghcr.io"));
    }

    private void An_images_page_is_its_registrys_Docker_Hubs_by_any_of_its_names(string image, string page)
    {
        Assert.Equal(page, ImageReference.Parse(image).Page?.AbsoluteUri);
    }

    private void An_image_a_variable_names_has_no_page(string image)
    {
        Assert.Null(ImageReference.Parse(image).Page);
    }
}
