using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>An image's Details tab: the engine's word of it and its known vulnerabilities through the person's own Docker
/// Scout (<see cref="DockerScout"/>), asked only by the person's Check with Docker Scout, which says first what Scout
/// sends: their counts by severity, then each CVE with the package that brings it and the version that fixes it. What Scout
/// said of an image is kept for it while the window is open.</summary>
public sealed class ImageDetailsPane : DetailsTables
{
    private readonly Func<ContainerEngine> engine;
    private readonly Dictionary<string, ScoutReport> reports = new(StringComparer.Ordinal);
    private EngineObject? image;
    private string? checking;

    public ImageDetailsPane(Func<ContainerEngine> engine) => this.engine = engine;

    /// <summary>What Scout said of the image shown, once asked; null before.</summary>
    public ScoutReport? Report => image is { } shown ? reports.GetValueOrDefault(shown.Name) : null;

    /// <summary>Whether Scout is checking the image shown.</summary>
    public bool IsChecking => image is { } shown && checking == shown.Name;

    /// <summary>Shows an image, or nothing.</summary>
    public void Show(EngineObject? shown)
    {
        UiThread.Check();
        if (shown?.Id == image?.Id && shown?.Name == image?.Name)
        {
            return;
        }
        image = shown;
        Fill();
    }

    /// <summary>Check with Docker Scout: the image shown asked of Scout, its report kept and shown once it lands.</summary>
    public async Task CheckAsync()
    {
        UiThread.Check();
        if (image is not { } asked || checking is not null)
        {
            return;
        }
        checking = asked.Name;
        Fill();
        ContainerEngine with = engine();
        try
        {
            reports[asked.Name] = await BackgroundWork.Run(() => DockerScout.CheckAsync(with, asked.Name, CancellationToken.None));
        }
        finally
        {
            checking = null;
        }
        Fill();
    }

    private void Fill()
    {
        if (image is not { } shown)
        {
            Say(null);
            return;
        }
        Show(() =>
        {
            List<(string, Avalonia.Controls.Control)> about = [("Name", Words(shown.Name)), ("Id", Words(shown.Id, code: true))];
            if (shown.Detail.Length > 0)
            {
                about.Add(("Size", Words(shown.Detail)));
            }
            Section("Image", about);
            if (checking == shown.Name)
            {
                Section("Vulnerabilities", [("Docker Scout", Words($"Checking {shown.Name}…"))]);
            }
            else if (reports.GetValueOrDefault(shown.Name) is not { } report)
            {
                Section("Vulnerabilities", [("Docker Scout", Words(DockerScout.Sends)),
                    (string.Empty, Link("Check with Docker Scout", $"Ask Docker Scout for {shown.Name}'s known vulnerabilities", () => IdeErrors.Observe(CheckAsync()), "imageScout"))]);
            }
            else if (report.Problem is { } problem)
            {
                Section("Vulnerabilities", [("Docker Scout", Words(problem, brush: TokenKeys.Error)),
                    (string.Empty, Link("Check again", $"Ask Docker Scout for {shown.Name}'s known vulnerabilities again", () => IdeErrors.Observe(Again()), "imageScout"))]);
            }
            else
            {
                List<(string, Avalonia.Controls.Control)> rows = [("Found", Words(report.Counts, brush: report.Grave ? TokenKeys.Error : null))];
                if (report.Recommended is { } tag)
                {
                    rows.Add(("Base image", Words($"Scout recommends its tag {tag}")));
                }
                rows.AddRange(report.Cves.Select(cve => (cve.Id, (Avalonia.Controls.Control)Words(
                    $"{cve.Severity}: {cve.Package}{(cve.Version is { } at ? $" {at}" : string.Empty)}, {(cve.FixedIn is { } fixedIn ? $"fixed in {fixedIn}" : "no fix yet")}",
                    brush: cve.Severity is "critical" or "high" ? TokenKeys.Error : null))));
                rows.Add((string.Empty, Link("Check again", $"Ask Docker Scout for {shown.Name}'s known vulnerabilities again", () => IdeErrors.Observe(Again()), "imageScout")));
                Section("Vulnerabilities", rows);
            }
        });

        Task Again()
        {
            reports.Remove(shown.Name);
            return CheckAsync();
        }
    }
}
