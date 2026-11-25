using System.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>A new WinUI app as Passenger's own template writes it, laid out as Visual Studio's WinUI template lays one out:
/// its project on the Windows App SDK, App.xaml and its window, an app manifest, a launch profile, and, packaged, its
/// package's manifest and logos (<see cref="Logos"/>); unpackaged, the Windows App SDK carried in its output, so it runs
/// whatever runtime the machine has. Every word of it is Passenger's own.</summary>
public static class WinUIApps
{
    /// <summary>The Windows App SDK a new app is made on, the newest release when the template was written; a restore takes it,
    /// and NuGet's updates move it on.</summary>
    public const string WindowsAppSdk = "2.7.0";

    /// <summary>The Windows SDK a new app targets, and the oldest Windows it runs on.</summary>
    private const string TargetPlatform = "10.0.19041.0";
    private const string OldestPlatform = "10.0.17763.0";

    /// <summary>The logos a packaged app's manifest names, each its size.</summary>
    private static readonly (string Name, int Width, int Height)[] LogoSizes =
    [
        ("StoreLogo.png", 50, 50), ("Square44x44Logo.png", 44, 44), ("Square150x150Logo.png", 150, 150), ("Wide310x150Logo.png", 310, 150), ("SplashScreen.png", 620, 300),
    ];

    /// <summary>The app's text files, each a path below its folder and its text: the project <c>{name}.csproj</c> for the
    /// framework given (<c>net11.0</c>), packaged as an MSIX or not, its namespace <see cref="Namespace"/>'s of the name, and
    /// a packaged one's identity (<paramref name="identity"/>, by the <paramref name="publisher"/> given).</summary>
    public static IReadOnlyList<(string Path, string Text)> Files(string name, string framework, bool packaged, string identity, string publisher)
    {
        string space = Namespace(name);
        List<(string, string)> files =
        [
            ($"{name}.csproj", Project(space, framework, packaged)),
            ("App.xaml", AppXaml(space)),
            ("App.xaml.cs", AppCode(space)),
            ("MainWindow.xaml", WindowXaml(space, name)),
            ("MainWindow.xaml.cs", WindowCode(space)),
            ("app.manifest", AppManifest(space)),
            (Path.Combine("Properties", "launchSettings.json"), LaunchSettings(name, packaged)),
        ];
        if (packaged)
        {
            files.Add(("Package.appxmanifest", PackageManifest(name, identity, publisher)));
        }
        return files;
    }

    /// <summary>A packaged app's logos, each a path below its folder and its PNG: a square of the app's colour.</summary>
    public static IReadOnlyList<(string Path, byte[] Png)> Logos() =>
        [.. LogoSizes.Select(logo => (Path.Combine("Assets", logo.Name), Logo(logo.Width, logo.Height)))];

    /// <summary>A C# namespace of a project's name, as dotnet new makes one: each character no name may hold an underscore, and
    /// one more before a name that begins with a digit.</summary>
    public static string Namespace(string name)
    {
        StringBuilder made = new(name.Length + 1);
        foreach (string part in name.Split('.'))
        {
            if (made.Length > 0)
            {
                made.Append('.');
            }
            if (part.Length == 0 || !CSharpNames.Starts(part[0]))
            {
                made.Append('_');
            }
            foreach (char c in part)
            {
                made.Append(CSharpNames.Continues(c) ? c : '_');
            }
        }
        return made.ToString();
    }

    /// <summary>A package identity name of a project's: letters, digits, dots and hyphens alone, at most 50 of them.</summary>
    public static string IdentityOf(string name, Guid unique)
    {
        string kept = new([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')]);
        string made = $"{(kept.Length > 0 ? kept : "App")}.{unique:N}";
        return made.Length <= 50 ? made : made[..50];
    }

    private static string Project(string space, string framework, bool packaged)
    {
        string packaging = packaged
            ? "    <EnableMsixTooling>true</EnableMsixTooling>\n"
            : "    <WindowsPackageType>None</WindowsPackageType>\n    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>\n";
        string logos = packaged ? "  <ItemGroup>\n    <Content Include=\"Assets\\*.png\" />\n  </ItemGroup>\n\n" : string.Empty;
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>{framework}-windows{TargetPlatform}</TargetFramework>
                <TargetPlatformMinVersion>{OldestPlatform}</TargetPlatformMinVersion>
                <RootNamespace>{XmlEntities.Encode(space)}</RootNamespace>
                <ApplicationManifest>app.manifest</ApplicationManifest>
                <Platforms>x86;x64;ARM64</Platforms>
                <RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>
                <UseWinUI>true</UseWinUI>

            """ + packaging + """
                <Nullable>enable</Nullable>
              </PropertyGroup>


            """ + logos + $"""
              <ItemGroup>
                <Manifest Include="$(ApplicationManifest)" />
              </ItemGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.WindowsAppSDK" Version="{WindowsAppSdk}" />
              </ItemGroup>
            </Project>

            """;
    }

    private static string AppXaml(string space) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Application
            x:Class="{space}.App"
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Application.Resources>
                <ResourceDictionary>
                    <ResourceDictionary.MergedDictionaries>
                        <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
                    </ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
            </Application.Resources>
        </Application>

        """;

    private static string AppCode(string space) => $$"""
        using Microsoft.UI.Xaml;

        namespace {{space}};

        public partial class App : Application
        {
            private Window? window;

            public App()
            {
                InitializeComponent();
            }

            protected override void OnLaunched(LaunchActivatedEventArgs args)
            {
                window = new MainWindow();
                window.Activate();
            }
        }

        """;

    private static string WindowXaml(string space, string name) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Window
            x:Class="{space}.MainWindow"
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            Title="{XmlEntities.Encode(name, '"')}">
            <StackPanel Spacing="12" HorizontalAlignment="Center" VerticalAlignment="Center">
                <TextBlock x:Name="Greeting" Text="Hello, WinUI" Style="{"{"}StaticResource TitleTextBlockStyle{"}"}" />
                <Button x:Name="Count" Content="Count" Click="OnCount" HorizontalAlignment="Center" />
            </StackPanel>
        </Window>

        """;

    private static string WindowCode(string space) => $$"""
        using Microsoft.UI.Xaml;

        namespace {{space}};

        public sealed partial class MainWindow : Window
        {
            private int counted;

            public MainWindow()
            {
                InitializeComponent();
            }

            private void OnCount(object sender, RoutedEventArgs e)
            {
                counted++;
                Greeting.Text = counted == 1 ? "Counted once" : $"Counted {counted} times";
            }
        }

        """;

    private static string AppManifest(string space) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
          <assemblyIdentity version="1.0.0.0" name="{XmlEntities.Encode(space, '"')}.app" />
          <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
            <application>
              <!-- Windows 10 and 11 -->
              <supportedOS Id="{"{"}8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a{"}"}" />
            </application>
          </compatibility>
          <application xmlns="urn:schemas-microsoft-com:asm.v3">
            <windowsSettings>
              <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
            </windowsSettings>
          </application>
        </assembly>

        """;

    private static string LaunchSettings(string name, bool packaged)
    {
        string profile = packaged ? $"{name} (Package)" : $"{name} (Unpackaged)";
        string command = packaged ? "MsixPackage" : "Project";
        return $$"""
            {
              "profiles": {
                {{System.Text.Json.JsonSerializer.Serialize(profile)}}: {
                  "commandName": "{{command}}"
                }
              }
            }

            """;
    }

    private static string PackageManifest(string name, string identity, string publisher) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package
          xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
          xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
          xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
          IgnorableNamespaces="uap rescap">
          <Identity Name="{XmlEntities.Encode(identity, '"')}" Publisher="{XmlEntities.Encode(publisher, '"')}" Version="1.0.0.0" />
          <Properties>
            <DisplayName>{XmlEntities.Encode(name)}</DisplayName>
            <PublisherDisplayName>{XmlEntities.Encode(publisher.StartsWith("CN=", StringComparison.Ordinal) ? publisher[3..] : publisher)}</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="{OldestPlatform}" MaxVersionTested="{TargetPlatform}" />
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="{OldestPlatform}" MaxVersionTested="{TargetPlatform}" />
          </Dependencies>
          <Resources>
            <Resource Language="x-generate" />
          </Resources>
          <Applications>
            <Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="$targetentrypoint$">
              <uap:VisualElements DisplayName="{XmlEntities.Encode(name, '"')}" Description="{XmlEntities.Encode(name, '"')}" BackgroundColor="transparent"
                Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png">
                <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
                <uap:SplashScreen Image="Assets\SplashScreen.png" />
              </uap:VisualElements>
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>

        """;

    /// <summary>A logo of the app's colour, as a PNG.</summary>
    private static byte[] Logo(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)0xF0, (byte)0x74, (byte)0x35, (byte)0xFF);
        }
        return PngFiles.OfBgra(pixels, width, height, width * 4);
    }
}
