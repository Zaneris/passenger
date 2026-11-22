using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Aspire;

/// <summary>What makes an AppHost: a program whose head names Aspire's AppHost SDK or its AppHost package, at any version, is
/// one (so never a plain file-based program), and a project is one that says so or, saying nothing, references the package,
/// whose props say so once restored.</summary>
public sealed class AspireHostsTests
{
    [Fact]
    public void An_AppHost_is_known_by_Aspires_SDK_or_its_AppHost_package_and_adds_its_services_by_path_from_Aspire_17() => Cases.All(
        ("a programs head makes it an AppHost by Aspires SDK or its AppHost package", () => Cases.Each<(string[], bool)>(
        [
            (new[] { "#:sdk Aspire.AppHost.Sdk@13.6.5" }, true),
            (new[] { "#:package Aspire.Hosting.AppHost@17.0.0", "#:package Aspire.Hosting.Dotnet@17.0.0" }, true),
            (new[] { "#!/usr/bin/env dotnet", "#:property Nullable=enable", "#:package aspire.hosting.apphost@17.0.0" }, true),
            (new[] { "#:package Aspire.Hosting.Redis@17.0.0" }, false),
            (new[] { "#:package Aspire.Hosting.AppHost.Extra@1.0.0" }, false),
        ], row => A_programs_head_makes_it_an_AppHost_by_Aspires_SDK_or_its_AppHost_package(row.Item1, row.Item2))),
        ("a project is an AppHost as it says or saying nothing by the AppHost package it references", () => Cases.Each<(string, string[], bool)>(
        [
            ("true", new string[0], true),
            ("", new[] { "Aspire.Hosting.AppHost", "Aspire.Hosting.Dotnet" }, true),
            ("false", new[] { "Aspire.Hosting.AppHost" }, false),
            ("", new[] { "Aspire.Hosting.Testing" }, false),
        ], row => A_project_is_an_AppHost_as_it_says_or_saying_nothing_by_the_AppHost_package_it_references(row.Item1, row.Item2, row.Item3))),
        ("an AppHost adds its services by path from Aspire 17", () => Cases.Each<(string?, bool)>(
        [
            ("17.0.0", true),
            ("17.1.2-preview.1", true),
            ("13.6.5", false),
            (null, false),
            ("$(AspireVersion)", false),
        ], row => An_AppHost_adds_its_services_by_path_from_Aspire_17(row.Item1, row.Item2))));

    private void A_programs_head_makes_it_an_AppHost_by_Aspires_SDK_or_its_AppHost_package(string[] head, bool appHost)
    {
        Assert.Equal(appHost, AspireHosts.IsHead(head));
        Assert.Equal(head.Length > 0 && !appHost, WorkspaceModel.IsProgram("/work/apphost.cs", head));
    }

    private void A_project_is_an_AppHost_as_it_says_or_saying_nothing_by_the_AppHost_package_it_references(string property, string[] packages, bool appHost) =>
        Assert.Equal(appHost, AspireHosts.Is(property, packages));

    private void An_AppHost_adds_its_services_by_path_from_Aspire_17(string? version, bool byPath) =>
        Assert.Equal(byPath, AspireHosts.AddsByPath(version));
}
