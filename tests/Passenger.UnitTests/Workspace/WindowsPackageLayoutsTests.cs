using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A packaged WinUI app's build read as the Windows SDK's MSIX tooling writes it: its recipe's layout folder, its
/// manifest and each packaged file from where the build has it to where the package has it, and its manifest's identity;
/// and Passenger's own WinUI app template, packaged or not, whose manifest reads as such an identity.</summary>
public sealed class WindowsPackageLayoutsTests
{
    private const string Recipe = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <PackageIdentityName>App1</PackageIdentityName>
            <LayoutDir>C:\App1\bin\ARM64\Debug\net11.0-windows10.0.19041.0\AppX</LayoutDir>
          </PropertyGroup>
          <ItemGroup>
            <AppXManifest Include="C:\App1\bin\ARM64\Debug\net11.0-windows10.0.19041.0\AppxManifest.xml">
              <PackagePath>AppxManifest.xml</PackagePath>
            </AppXManifest>
          </ItemGroup>
          <ItemGroup>
            <AppxPackagedFile Include="C:\App1\obj\ARM64\Debug\net11.0-windows10.0.19041.0\apphost.exe">
              <PackagePath>App1.exe</PackagePath>
            </AppxPackagedFile>
            <AppxPackagedFile Include="C:\App1\Assets\StoreLogo.png">
              <PackagePath>Assets\StoreLogo.png</PackagePath>
            </AppxPackagedFile>
          </ItemGroup>
        </Project>
        """;

    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" IgnorableNamespaces="uap">
          <Identity Name="App1" Publisher="CN=Passenger" Version="1.0.0.0" ProcessorArchitecture="arm64" />
          <Dependencies>
            <PackageDependency Name="Microsoft.WindowsAppRuntime.2" MinVersion="2.7.0.0" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />
          </Dependencies>
          <Applications>
            <Application Id="App" Executable="App1.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
        </Package>
        """;

    [Fact]
    public void A_packaged_apps_build_is_read_as_its_recipe_and_manifest_say() => Cases.All(
        ("the recipe gives the layouts folder its manifest and each file from where the build has it", The_recipe_gives_the_layouts_folder_its_manifest_and_each_file),
        ("the manifest gives the identity the applications and the packages it depends on", The_manifest_gives_the_identity_the_applications_and_the_packages_it_depends_on),
        ("passengers winui app template writes a packaged apps manifest and logos and an unpackaged one neither", Passengers_winui_app_template_writes_a_packaged_apps_manifest_and_logos_and_an_unpackaged_one_neither),
        ("text that reads as neither is passed over", () =>
        {
            Assert.Null(WindowsPackageLayouts.Recipe("<Project />"));
            Assert.Null(WindowsPackageLayouts.Identity("not xml"));
        }));

    private void The_recipe_gives_the_layouts_folder_its_manifest_and_each_file()
    {
        WindowsPackageLayout layout = WindowsPackageLayouts.Recipe(Recipe)!;
        Assert.Equal(@"C:\App1\bin\ARM64\Debug\net11.0-windows10.0.19041.0\AppX", layout.Folder);
        Assert.Equal(["AppxManifest.xml", "App1.exe", @"Assets\StoreLogo.png"], layout.Files.Select(f => f.To));
        Assert.Equal(@"C:\App1\obj\ARM64\Debug\net11.0-windows10.0.19041.0\apphost.exe", layout.Files[1].From);
    }

    private void Passengers_winui_app_template_writes_a_packaged_apps_manifest_and_logos_and_an_unpackaged_one_neither()
    {
        IReadOnlyList<(string Path, string Text)> packaged = WinUIApps.Files("My App", "net11.0", packaged: true, "My.App.1234", "CN=Ada");
        IReadOnlyList<(string Path, string Text)> unpackaged = WinUIApps.Files("My App", "net11.0", packaged: false, "My.App.1234", "CN=Ada");
        string project = packaged.Single(f => f.Path == "My App.csproj").Text;
        Assert.Contains("<EnableMsixTooling>true</EnableMsixTooling>", project, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net11.0-windows10.0.19041.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("<RootNamespace>My_App</RootNamespace>", project, StringComparison.Ordinal);
        Assert.Contains("x:Class=\"My_App.MainWindow\"", packaged.Single(f => f.Path == "MainWindow.xaml").Text, StringComparison.Ordinal);
        WindowsPackageIdentity identity = WindowsPackageLayouts.Identity(packaged.Single(f => f.Path == "Package.appxmanifest").Text)!;
        Assert.Equal(("My.App.1234", "CN=Ada", "App"), (identity.Name, identity.Publisher, string.Join(", ", identity.Applications)));
        Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", unpackaged.Single(f => f.Path == "My App.csproj").Text, StringComparison.Ordinal);
        Assert.DoesNotContain(unpackaged, f => f.Path == "Package.appxmanifest");
        Assert.All(WinUIApps.Logos(), logo => Assert.Equal((byte[])[0x89, 0x50, 0x4E, 0x47], logo.Png[..4]));
        Assert.Equal("_2nd.App", WinUIApps.Namespace("2nd.App"));
    }

    private void The_manifest_gives_the_identity_the_applications_and_the_packages_it_depends_on()
    {
        WindowsPackageIdentity identity = WindowsPackageLayouts.Identity(Manifest)!;
        Assert.Equal(("App1", "CN=Passenger", "1.0.0.0", "arm64"), (identity.Name, identity.Publisher, identity.Version, identity.Processor));
        Assert.Equal(["App"], identity.Applications);
        Assert.Equal("Microsoft.WindowsAppRuntime.2 2.7.0.0", string.Join(", ", identity.Dependencies.Select(d => $"{d.Name} {d.MinVersion}")));
    }
}
