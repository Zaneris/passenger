using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;

namespace Passenger.Host;

/// <summary>A few properties of one project as MSBuild evaluates it with the global properties a build would be given: what
/// Publish reads of a publish profile (its folder, runtime, configuration), which the SDK imports by PublishProfile, so
/// every path and condition in it resolves as the publish itself resolves it. A fresh evaluation each time, since the
/// profile it reads may have just been written.</summary>
internal static class Properties
{
    public static PropertiesAnswer Run(PropertiesParams asked)
    {
        using ProjectCollection collection = new(asked.Properties.ToDictionary(p => p.Key, p => p.Value), loggers: null, ToolsetDefinitionLocations.Default);
        Project? project = null;
        try
        {
            project = WindowsTargeting.Load(given => VirtualProjects.Load(asked.Project, new ProjectOptions
            {
                GlobalProperties = given,
                ProjectCollection = collection,
                EvaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Isolated),
                LoadSettings = Evaluation.Settings,
            }), asked.Properties, collection);
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (string name in asked.Names)
            {
                if (project.GetPropertyValue(name) is { Length: > 0 } value)
                {
                    values[name] = value;
                }
            }
            return new PropertiesAnswer(values, null);
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new PropertiesAnswer(new Dictionary<string, string>(), error.Message);
        }
        finally
        {
            if (project is not null)
            {
                collection.UnloadProject(project);
            }
        }
    }
}
