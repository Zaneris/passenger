namespace Passenger.App.Templates;

/// <summary>The known packages' kinds as New Project lists them, among the templates dotnet new has.</summary>
public static partial class TemplatePacks
{
    /// <summary>Each known kind dotnet new has no template for (none of the installed has its short name), as a template
    /// whose <see cref="ProjectTemplate.Pack"/> is the package that brings it; <paramref name="androidHost"/> is this
    /// machine where it builds no Android app (<see cref="Mobile.AndroidHosts.UnbuiltAsync"/>).</summary>
    public static IEnumerable<ProjectTemplate> Offered(IReadOnlyList<ProjectTemplate> installed, string? androidHost)
    {
        HashSet<string> had = new(installed.SelectMany(t => t.ShortNames), StringComparer.OrdinalIgnoreCase);
        foreach (TemplatePack pack in All.Concat(Workloads(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), androidHost)))
        {
            foreach (PackKind kind in pack.Kinds.Where(k => !had.Contains(k.ShortName)))
            {
                List<TemplateVariant> variants = [.. kind.Languages.Select(language =>
                    new TemplateVariant($"{pack.Id}::{kind.ShortName}::{language}", language, 0, kind.Description, null, [], null, []))];
                yield return new ProjectTemplate(kind.Name, [kind.ShortName], pack.Author, kind.Classifications, kind.DefaultName, variants)
                {
                    IsSolution = kind.IsSolution,
                    Pack = pack,
                };
            }
        }
    }
}
