using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Debugging;

/// <summary>What debugging a Blazor app's WebAssembly half needs, found before it launches: the Chromium browser it runs in,
/// .NET's browser debug proxy (BrowserDebugHost, the runtime's own) as the app's restore brought it, the page the browser
/// opens, and the projects whose code runs there. The proxy is the WebAssembly SDK pack's every browser project restores,
/// else the development server's or Blazor's server package's the app's own project restored: Passenger carries none.</summary>
public sealed record BrowserDebugging(ChromiumFound Browser, string Proxy, Uri Page, IReadOnlyList<string> Projects)
{
    /// <summary>The package of the runtime's WebAssembly SDK, which every browser project restores and which holds the proxy.</summary>
    private const string WebAssemblyPack = "Microsoft.NET.Sdk.WebAssembly.Pack";

    /// <summary>The packages that host the proxy for an app, each under its tools.</summary>
    private static readonly string[] Hosting = ["Microsoft.AspNetCore.Components.WebAssembly.Server", "Microsoft.AspNetCore.Components.WebAssembly.DevServer"];

    /// <summary>The proxy's own limits, said where its session begins.</summary>
    public const string Limits = "In the browser, .NET's debug proxy evaluates a narrower C# than Passenger's engine and sets no values; Hot Reload of "
        + "the WebAssembly half comes through dotnet watch's browser refresh (Run with Hot Reload), not through the debugger; and only Chromium browsers "
        + "(Chrome, Edge, Chromium, Brave) are debugged";

    /// <summary>What a plan's browser half needs on this machine, or why it cannot be debugged there (no browser, no proxy
    /// restored, no page); none at all for a plan with no code in the browser.</summary>
    public static (BrowserDebugging? Ready, string? WhyNot) For(Run.LaunchPlan plan, IReadOnlyList<ProjectModel> projects, string? browserSetting)
    {
        if (plan.InBrowser.Count == 0 || plan.Project is not { } own)
        {
            return (null, null);
        }
        if (ChromiumBrowsers.Find(Chosen(browserSetting)) is not { } browser)
        {
            return (null, ChromiumBrowsers.NoneFound(browserSetting, $"{SettingsDialog.PlaceOfPage(SettingsDialog.RunPage)} › {BrowserRow}"));
        }
        if (plan.Page is not { } page)
        {
            return (null, "Its configuration opens no page (a launch profile's applicationUrl or launchUrl names it), so there is nothing to debug in the browser");
        }
        ProjectModel? client = projects.FirstOrDefault(p => PathNames.Same(p.Path, plan.InBrowser[0]));
        ProjectModel? server = projects.FirstOrDefault(p => PathNames.Same(p.Path, own));
        string? proxy = (client is null ? null : BrowserProxy.Of(client, plan.RunsOn, [WebAssemblyPack], "WasmAppHost"))
            ?? (server is null ? null : BrowserProxy.Of(server, plan.RunsOn, Hosting, Path.Combine("tools", "BlazorDebugProxy")));
        if (proxy is null)
        {
            return (null, $"{Path.GetFileNameWithoutExtension(plan.InBrowser[0])}'s restore brought no .NET browser debug proxy ({BrowserProxy.FileName}, in {WebAssemblyPack}): restore it, then debug again");
        }
        return (new BrowserDebugging(browser, proxy, page, plan.InBrowser), null);
    }

    /// <summary>The setting's row, which names the browser to debug in.</summary>
    public const string BrowserRow = "Browser for WebAssembly";

    /// <summary>The program and the arguments the setting's command line gives: the whole line when it is a path that is
    /// there (one with spaces needs no quotes), else its first word and those after it; null for none.</summary>
    public static (string Program, IReadOnlyList<string> Arguments)? Chosen(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }
        string line = setting.Trim();
        if (File.Exists(line))
        {
            return (line, []);
        }
        IReadOnlyList<string> words = Run.CommandLine.Split(line);
        return words is [{ Length: > 0 } program, ..] ? (program, [.. words.Skip(1)]) : null;
    }
}
