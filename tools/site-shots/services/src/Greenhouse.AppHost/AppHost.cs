using Aspire.Hosting.Dotnet;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<DotnetProjectResource> api = builder.AddDotnetProject("api", "../Greenhouse.Api/Greenhouse.Api.csproj");

builder.AddDotnetProject("sensors", "../Greenhouse.Sensors/Greenhouse.Sensors.csproj")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
