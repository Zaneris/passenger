using Passenger.App.Containers;

namespace Passenger.UnitTests.Containers;

/// <summary>An image reference as a Dockerfile's FROM, a compose file's image, a push and a login read it: its registry, its
/// path and its repository, Docker Hub by every name it answers to, and the page Go to Declaration opens for it.</summary>
public sealed class ImageReferenceTests
{
    [Theory]
    [InlineData("nginx", null, "nginx", "nginx")]
    [InlineData("nginx:1.27", null, "nginx", "nginx")]
    [InlineData("bitnami/redis:7.2@sha256:0123abcd", null, "bitnami/redis", "bitnami/redis")]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:10.0", "mcr.microsoft.com", "dotnet/aspnet", "mcr.microsoft.com/dotnet/aspnet")]
    [InlineData("localhost:5000/shop/web", "localhost:5000", "shop/web", "localhost:5000/shop/web")]
    [InlineData("registry.example:5000/web:dev", "registry.example:5000", "web", "registry.example:5000/web")]
    [InlineData("localhost/web", "localhost", "web", "localhost/web")]
    [InlineData("localhost:5000", null, "localhost", "localhost")]
    public void A_reference_is_read_as_its_registry_path_and_repository(string image, string? registry, string path, string repository)
    {
        ImageReference read = ImageReference.Parse(image);
        Assert.Equal((registry, path, repository), (read.Registry, read.Path, read.Repository));
    }

    [Theory]
    [InlineData("docker.io")]
    [InlineData("index.docker.io")]
    [InlineData("registry-1.docker.io")]
    [InlineData("registry.hub.docker.com")]
    [InlineData("Docker.IO")]
    public void Every_name_Docker_Hub_answers_to_is_Docker_Hub(string host)
    {
        Assert.True(ImageReference.IsDockerHub(host), $"{host} was not taken for Docker Hub: its login and its images' pages would be another registry's");
    }

    [Fact]
    public void Another_registry_is_not_Docker_Hub()
    {
        Assert.False(ImageReference.IsDockerHub("hub.docker.example"));
        Assert.False(ImageReference.IsDockerHub("ghcr.io"));
    }

    [Theory]
    [InlineData("postgres:17", "https://hub.docker.com/_/postgres")]
    [InlineData("docker.io/library/nginx:1.27", "https://hub.docker.com/_/nginx")]
    [InlineData("registry.hub.docker.com/library/nginx", "https://hub.docker.com/_/nginx")]
    [InlineData("registry.hub.docker.com/bitnami/redis:7.2", "https://hub.docker.com/r/bitnami/redis")]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:10.0", "https://mcr.microsoft.com/artifact/mar/dotnet/aspnet")]
    [InlineData("ghcr.io/acme/web:1", "https://github.com/acme/web/pkgs/container/web")]
    [InlineData("quay.io/keycloak/keycloak:26.0", "https://quay.io/repository/keycloak/keycloak")]
    [InlineData("registry.example:5000/web:dev", "https://registry.example:5000/")]
    public void An_images_page_is_its_registrys_Docker_Hubs_by_any_of_its_names(string image, string page)
    {
        Assert.Equal(page, ImageReference.Parse(image).Page?.AbsoluteUri);
    }

    [Theory]
    [InlineData("${REGISTRY}/web:dev")]
    [InlineData("web:${TAG}")]
    [InlineData("")]
    public void An_image_a_variable_names_has_no_page(string image)
    {
        Assert.Null(ImageReference.Parse(image).Page);
    }
}
