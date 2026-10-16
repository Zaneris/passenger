using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;

namespace Passenger.App.Run;

/// <summary>An immutable composed import shared by ordinary builds, dotnet watch and isolated design-time builds.
/// Preparation reads the untouched graph first; an unsupported one receives no overriding global property.</summary>
public sealed class WpfPopulationBuild(string stateFolder)
{
    public sealed record Plan(string? Import, string? Problem, IReadOnlyList<Stamp> Inputs)
    {
        internal string? Sdk { get; init; }

        public IReadOnlyList<string> Arguments => Import is { } path ? ["--property:CustomBeforeMicrosoftCommonTargets=" + path.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal)] : [];
    }

    private static readonly Lazy<(string Targets, string Helper)> Sources = new(() => (Resource("WpfPopulation.targets"), Resource("WpfPopulation.g.cs.txt")));
    private Task<Plan>? preparing;
    private string? key;
    private Plan? current;
    private int generation;

    public Plan? Current => current;

    public Task<Plan> PrepareAsync(string folder, IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> designTimeProperties)
    {
        string asked = string.Join('\n', projects.Order(StringComparer.Ordinal).Concat(properties.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value)).Concat(designTimeProperties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "DT:" + p.Key + "=" + p.Value)));
        if (key == asked && preparing is { IsCompleted: false } pending)
        {
            return pending;
        }
        Plan? previous = key == asked && preparing is { IsCompletedSuccessfully: true } ready ? ready.Result : null;
        if (key != asked)
        {
            current = null;
        }
        key = asked;
        return preparing = RecheckAsync(++generation, folder, projects, properties, designTimeProperties, previous);
    }

    private async Task<Plan> RecheckAsync(int started, string folder, IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> designTimeProperties, Plan? previous)
    {
        Plan result;
        try
        {
            // SDK resolution is cheaper than graph evaluation and also detects a new SDK installation or selection
            // that leaves every existing XML input unchanged. Both checks stay off the UI thread.
            ProjectHost host = await Task.Run(() => ProjectHosts.ForAsync(folder));
            if (previous is not null && previous.Sdk is { } sdk && PathNames.Same(sdk, host.Sdk)
                && (previous.Problem is null || previous.Inputs.Count > 0) && await Task.Run(() => previous.Inputs.All(Stamps.Holds)))
            {
                result = previous;
            }
            else
            {
                result = (await PrepareCurrentAsync(host, projects, properties, designTimeProperties)) with { Sdk = host.Sdk };
            }
        }
        catch (Exception error) when (error is ProjectHostFailed or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = new Plan(null, $"WPF future-view Hot Reload needs a restart: its shared build hook could not be prepared: {error.Message}", []);
        }
        if (generation == started)
        {
            current = result;
        }
        return result;
    }

    private async Task<Plan> PrepareCurrentAsync(ProjectHost host, IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> designTimeProperties)
    {
        try
        {
            WpfBuildPrepared prepared = await host.PrepareWpfBuildAsync(projects, properties, designTimeProperties);
            if (prepared.Problem is { } problem)
            {
                return new Plan(null, problem, prepared.Inputs);
            }
            if (!prepared.Wpf)
            {
                return new Plan(null, null, prepared.Inputs);
            }
            (string targets, string helper) = Sources.Value;
            string original = prepared.Before is { } before ? "<Import Project=\"" + System.Security.SecurityElement.Escape(before) + "\" />" : string.Empty;
            string content = targets.Replace("<!-- ORIGINAL_IMPORT -->", original, StringComparison.Ordinal);
            // Input stamps invalidate evaluation, while the actual source content determines its immutable path.
            // A watch rebuild and a later design-time build must still compile the helper at the same path when
            // the import composition has not changed. No wrapper is overwritten beneath an active participant.
            string identity = content + helper;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            string directory = PathNames.Canonical(Path.Combine(stateFolder, "wpf-population", hash));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                Keep(Path.Combine(directory, "Population.targets"), content);
                Keep(Path.Combine(directory, "Population.g.cs"), helper);
            });
            return new Plan(Path.Combine(directory, "Population.targets"), null, prepared.Inputs);
        }
        catch (Exception error) when (error is ProjectHostFailed or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Plan(null, $"WPF future-view Hot Reload needs a restart: its shared build hook could not be prepared: {error.Message}", []);
        }
    }

    private static void Keep(string path, string content)
    {
        if (File.Exists(path))
        {
            if (File.ReadAllText(path) != content)
            {
                throw new IOException("The immutable WPF population import changed: " + path);
            }
            return;
        }
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(pending, content);
        try
        {
            File.Move(pending, path);
        }
        catch (IOException) when (File.Exists(path) && File.ReadAllText(path) == content)
        {
            File.Delete(pending);
        }
    }

    private static string Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WpfPopulation/" + name)
            ?? throw new InvalidOperationException("The WPF population build resource is missing: " + name);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
