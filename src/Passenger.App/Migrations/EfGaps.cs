using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Migrations;

/// <summary>What a fix of the Migrations window does.</summary>
public enum EfFixKind
{
    /// <summary>The local tool pinned to the project's EF Core, a manifest made where the folder has none.</summary>
    PinTool,

    /// <summary>The person's global tool moved to the project's EF Core, which only they ask for.</summary>
    UpdateGlobalTool,

    /// <summary><c>dotnet tool restore</c>, for a manifest whose tools are not in the package cache.</summary>
    RestoreTools,

    /// <summary>Microsoft.EntityFrameworkCore.Design added to the startup project at the EF Core runtime's version.</summary>
    AddDesign,

    /// <summary>A provider behind the project's EF Core brought into step: moved up where a version supports that EF Core,
    /// else EF Core's packages moved to the newest version the provider supports (<see cref="EfProviders.AlignAsync"/>).</summary>
    AlignProvider,

    /// <summary>An <c>IDesignTimeDbContextFactory</c> for the context, from a template.</summary>
    AddFactory,

    /// <summary>Another startup project, chosen in the window.</summary>
    ChooseStartup,

    /// <summary>Another context, chosen in the window.</summary>
    ChooseContext,

    /// <summary>The script made again without idempotence, which SQLite cannot take.</summary>
    ScriptPlain,

    /// <summary>Add Migration…, for a context's model changes no migration covers.</summary>
    AddMigration,
}

/// <summary>A fix of a gap, with its title as its button says it.</summary>
public sealed record EfFix(EfFixKind Kind, string Title);

/// <summary>Something that keeps <c>dotnet ef</c> from working, said in one plain line, with the fixes that put it right;
/// a gap that <see cref="Blocks"/> stops a command until it is fixed, one that does not is asked about first.</summary>
public sealed record EfGap(string Said, IReadOnlyList<EfFix> Fixes, bool Blocks = true);

/// <summary>Reads what keeps <c>dotnet ef</c> from working, before a command runs (<see cref="Before"/>: the tool and the
/// restore as they stand) and after one fails (<see cref="After"/>: its errors), into one plain line each with its fix:
/// a stack trace is never the only word.</summary>
public static partial class EfGaps
{
    /// <summary>What a startup project's restore and the tool it would run lack, found before a command: the tool missing or
    /// of another version than the project's EF Core, the Design package missing, a provider whose range leaves the
    /// project's EF Core out. None for a project not restored yet, which the build restores first.</summary>
    public static IReadOnlyList<EfGap> Before(EfTool? tool, EfRestore restore, string startup)
    {
        List<EfGap> gaps = [];
        if (restore.Runtime is not { } runtime)
        {
            gaps.Add(new EfGap($"{startup} does not use EF Core: choose the startup project that runs the context", [new EfFix(EfFixKind.ChooseStartup, "Choose Startup Project")]));
            return gaps;
        }
        if (tool is null)
        {
            gaps.Add(new EfGap($"dotnet ef is not installed here, and {startup} has EF Core {runtime}", [PinFix(runtime)]));
        }
        else if (tool.Version != runtime)
        {
            List<EfFix> fixes = [PinFix(runtime)];
            if (tool.Place == EfToolPlace.Global)
            {
                fixes.Add(new EfFix(EfFixKind.UpdateGlobalTool, $"Update the Global {EfTools.Id} to {runtime}"));
            }
            // Another major's tool runs on a runtime the project may not have, and reads another major's Design package.
            gaps.Add(new EfGap($"{Capital(tool.Shown)} does not match {startup}'s EF Core {runtime}", fixes, Blocks: VersionOrder.Major(tool.Version) != VersionOrder.Major(runtime)));
        }
        if (restore.Design is null)
        {
            gaps.Add(new EfGap($"{startup} lacks {EfRestore.DesignPackage}, which dotnet ef loads from the startup project",
                [new EfFix(EfFixKind.AddDesign, $"Add {EfRestore.DesignPackage} {runtime}")]));
        }
        if (restore.Behind is { } behind)
        {
            gaps.Add(new EfGap($"{behind.Id} {behind.Version} supports {EfRestore.Supports(behind.Range)}; {startup} has EF Core {runtime}",
                [AlignFix(behind.Id)], Blocks: false));
        }
        return gaps;
    }

    /// <summary>The fix that brings a provider and EF Core's packages into step.</summary>
    public static EfFix AlignFix(string provider) => new(EfFixKind.AlignProvider, $"Bring {provider} and EF Core into Step");

    /// <summary>The fix that pins the local tool to the project's EF Core.</summary>
    public static EfFix PinFix(string runtime) => new(EfFixKind.PinTool, $"Pin the Local {EfTools.Id} to {runtime}");

    /// <summary>A failed command read into one line with its fix where one is known: EF's own error (its first line, or the
    /// tool's or dotnet's words), never a stack frame.</summary>
    public static EfGap After(EfOutcome outcome, string startup, string? runtime, bool idempotent = false)
    {
        string errors = string.Join(' ', outcome.Errors.Select(e => e.Trim()));
        string plain = string.Join('\n', outcome.Lines.Where(l => EfRun.Split(l).Kind.Length == 0));
        string version = runtime ?? "the project's EF Core";
        if (errors.Contains("doesn't reference Microsoft.EntityFrameworkCore.Design", StringComparison.Ordinal))
        {
            return new EfGap($"{startup} lacks {EfRestore.DesignPackage}, which dotnet ef loads from the startup project",
                runtime is null ? [] : [new EfFix(EfFixKind.AddDesign, $"Add {EfRestore.DesignPackage} {runtime}")]);
        }
        if (UnableToCreate().Match(errors) is { Success: true } unmade)
        {
            string context = unmade.Groups[1].Value;
            string why = errors.Contains("Unable to resolve service for type 'Microsoft.EntityFrameworkCore.DbContextOptions", StringComparison.Ordinal)
                ? $"no host in {startup} registers it, and it has no parameterless constructor or design-time factory"
                : FirstSentence(unmade.Groups[2].Value);
            return new EfGap($"dotnet ef cannot make {context}: {why}",
                [new EfFix(EfFixKind.AddFactory, $"Add a Design-Time Factory for {context}"), new EfFix(EfFixKind.ChooseStartup, "Choose Startup Project")]);
        }
        if (errors.Contains("More than one DbContext was found", StringComparison.Ordinal))
        {
            return new EfGap("The project has several contexts: choose one", [new EfFix(EfFixKind.ChooseContext, "Choose Context")]);
        }
        if (errors.Contains("No DbContext was found", StringComparison.Ordinal))
        {
            return new EfGap("dotnet ef finds no DbContext in the project or the startup project", [new EfFix(EfFixKind.ChooseStartup, "Choose Startup Project")]);
        }
        if (idempotent && errors.Contains("idempotent scripts for migrations is not currently supported for SQLite", StringComparison.OrdinalIgnoreCase))
        {
            return new EfGap("SQLite takes no idempotent script", [new EfFix(EfFixKind.ScriptPlain, "Script Without Idempotence")]);
        }
        if (Applied().Match(errors) is { Success: true } applied)
        {
            return new EfGap($"{applied.Groups[1].Value} is applied to the database: update the database to the migration before it first", []);
        }
        if (plain.Contains("dotnet tool restore", StringComparison.Ordinal))
        {
            return new EfGap("The folder's local tools are not restored", [new EfFix(EfFixKind.RestoreTools, "Restore Local Tools")]);
        }
        if (plain.Contains("command or file was not found", StringComparison.Ordinal) || plain.Contains("dotnet-ef does not exist", StringComparison.Ordinal))
        {
            return new EfGap("dotnet ef is not installed here", runtime is null ? [] : [PinFix(runtime)]);
        }
        string said = outcome.Errors.FirstOrDefault(e => e.Trim().Length > 0 && !IsFrame(e))?.Trim()
            ?? outcome.Lines.Select(l => EfRun.Split(l).Said.Trim()).LastOrDefault(l => l.Length > 0 && !IsFrame(l) && !l.StartsWith("Build ", StringComparison.Ordinal))
            ?? $"dotnet ef exited with code {outcome.ExitCode}";
        return new EfGap(said, []);
    }

    /// <summary>EF's warning that the tool is older than the runtime, read as the gap it is, with its fix.</summary>
    public static EfGap? Warned(EfOutcome outcome, string startup)
    {
        foreach (string warning in outcome.Warnings)
        {
            if (OlderTool().Match(warning) is { Success: true } older)
            {
                return new EfGap($"The {EfTools.Id} {older.Groups[1].Value} run is older than {startup}'s EF Core {older.Groups[2].Value}",
                    [PinFix(older.Groups[2].Value)], Blocks: false);
            }
        }
        return null;
    }

    private static bool IsFrame(string line) => line.TrimStart().StartsWith("at ", StringComparison.Ordinal) || line.TrimStart().StartsWith("---", StringComparison.Ordinal);

    private static string FirstSentence(string text)
    {
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        return (end < 0 ? text : text[..end]).Trim().TrimEnd('.');
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"Unable to create (?:a 'DbContext' of type|an object of type) '([^']+)'\.?(.*)")]
    private static partial Regex UnableToCreate();

    [GeneratedRegex(@"The migration '([^']+)' has already been applied to the database")]
    private static partial Regex Applied();

    [GeneratedRegex(@"tools version '([^']+)' is older than that of the runtime '([^']+)'")]
    private static partial Regex OlderTool();
}
