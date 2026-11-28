using System.Globalization;
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
public sealed record EfFix(EfFixKind Kind, Words Title)
{
    /// <summary>The context a design-time factory is added for; null for any other fix.</summary>
    public string? On { get; init; }
}

/// <summary>Something that keeps <c>dotnet ef</c> from working, said in one plain line, with the fixes that put it right;
/// a gap that <see cref="Blocks"/> stops a command until it is fixed, one that does not is asked about first.</summary>
public sealed record EfGap(Words Said, IReadOnlyList<EfFix> Fixes, bool Blocks = true);

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
            gaps.Add(new EfGap(MigrationsWords.NotEfCore(startup), [new EfFix(EfFixKind.ChooseStartup, MigrationsWords.ChooseStartupFix)]));
            return gaps;
        }
        if (tool is null)
        {
            gaps.Add(new EfGap(MigrationsWords.NoToolFor(startup, runtime), [PinFix(runtime)]));
        }
        else if (tool.Version != runtime)
        {
            List<EfFix> fixes = [PinFix(runtime)];
            if (tool.Place == EfToolPlace.Global)
            {
                fixes.Add(new EfFix(EfFixKind.UpdateGlobalTool, MigrationsWords.UpdateGlobalFix(EfTools.Id, runtime)));
            }
            // Another major's tool runs on a runtime the project may not have, and reads another major's Design package.
            gaps.Add(new EfGap(MigrationsWords.ToolUnmatched(tool.Place == EfToolPlace.Local ? "local" : "global", EfTools.Id, tool.Version, startup, runtime), fixes,
                Blocks: VersionOrder.Major(tool.Version) != VersionOrder.Major(runtime)));
        }
        if (restore.Design is null)
        {
            gaps.Add(new EfGap(MigrationsWords.LacksDesign(startup, EfRestore.DesignPackage), [DesignFix(runtime)]));
        }
        if (restore.Behind is { } behind)
        {
            gaps.Add(new EfGap(MigrationsWords.ProviderBehind(behind.Id, behind.Version, Supports(behind.Range), startup, runtime),
                [AlignFix(behind.Id)], Blocks: false));
        }
        return gaps;
    }

    /// <summary>The fix that brings a provider and EF Core's packages into step.</summary>
    public static EfFix AlignFix(string provider) => new(EfFixKind.AlignProvider, MigrationsWords.AlignFix(provider));

    /// <summary>The fix that pins the local tool to the project's EF Core.</summary>
    public static EfFix PinFix(string runtime) => new(EfFixKind.PinTool, MigrationsWords.PinFix(EfTools.Id, runtime));

    /// <summary>The fix that adds the Design package at the project's EF Core.</summary>
    private static EfFix DesignFix(string runtime) => new(EfFixKind.AddDesign, MigrationsWords.DesignFix(EfRestore.DesignPackage, runtime));

    /// <summary>A failed command read into one line with its fix where one is known: EF's own error (its first line, or the
    /// tool's or dotnet's words), never a stack frame.</summary>
    public static EfGap After(EfOutcome outcome, string startup, string? runtime, bool idempotent = false)
    {
        string errors = string.Join(' ', outcome.Errors.Select(e => e.Trim()));
        string plain = string.Join('\n', outcome.Lines.Where(l => EfRun.Split(l).Kind.Length == 0));
        if (errors.Contains("doesn't reference Microsoft.EntityFrameworkCore.Design", StringComparison.Ordinal))
        {
            return new EfGap(MigrationsWords.LacksDesign(startup, EfRestore.DesignPackage), runtime is null ? [] : [DesignFix(runtime)]);
        }
        if (UnableToCreate().Match(errors) is { Success: true } unmade)
        {
            string context = unmade.Groups[1].Value;
            Words why = errors.Contains("Unable to resolve service for type 'Microsoft.EntityFrameworkCore.DbContextOptions", StringComparison.Ordinal)
                ? MigrationsWords.NoHostRegisters(startup)
                : Words.Verbatim(FirstSentence(unmade.Groups[2].Value));
            return new EfGap(MigrationsWords.CannotMake(context, why),
                [new EfFix(EfFixKind.AddFactory, MigrationsWords.FactoryFix(context)) { On = context }, new EfFix(EfFixKind.ChooseStartup, MigrationsWords.ChooseStartupFix)]);
        }
        if (errors.Contains("More than one DbContext was found", StringComparison.Ordinal))
        {
            return new EfGap(MigrationsWords.SeveralContexts, [new EfFix(EfFixKind.ChooseContext, MigrationsWords.ChooseContextFix)]);
        }
        if (errors.Contains("No DbContext was found", StringComparison.Ordinal))
        {
            return new EfGap(MigrationsWords.NoContextFound, [new EfFix(EfFixKind.ChooseStartup, MigrationsWords.ChooseStartupFix)]);
        }
        if (idempotent && errors.Contains("idempotent scripts for migrations is not currently supported for SQLite", StringComparison.OrdinalIgnoreCase))
        {
            return new EfGap(MigrationsWords.NoIdempotentScript, [new EfFix(EfFixKind.ScriptPlain, MigrationsWords.ScriptPlainFix)]);
        }
        if (Applied().Match(errors) is { Success: true } applied)
        {
            return new EfGap(MigrationsWords.AppliedAlready(applied.Groups[1].Value), []);
        }
        if (plain.Contains("dotnet tool restore", StringComparison.Ordinal))
        {
            return new EfGap(MigrationsWords.ToolsUnrestored, [new EfFix(EfFixKind.RestoreTools, MigrationsWords.RestoreToolsFix)]);
        }
        if (plain.Contains("command or file was not found", StringComparison.Ordinal) || plain.Contains("dotnet-ef does not exist", StringComparison.Ordinal))
        {
            return new EfGap(MigrationsWords.NoTool, runtime is null ? [] : [PinFix(runtime)]);
        }
        // EF's own words where it said any, as it wrote them.
        string? said = outcome.Errors.FirstOrDefault(e => e.Trim().Length > 0 && !IsFrame(e))?.Trim()
            ?? outcome.Lines.Select(l => EfRun.Split(l).Said.Trim()).LastOrDefault(l => l.Length > 0 && !IsFrame(l) && !l.StartsWith("Build ", StringComparison.Ordinal));
        return new EfGap(said is null ? MigrationsWords.EfEnded(ExitCodes.Words(outcome.ExitCode)) : Words.Verbatim(said), []);
    }

    /// <summary>EF's warning that the tool is older than the runtime, read as the gap it is, with its fix.</summary>
    public static EfGap? Warned(EfOutcome outcome, string startup)
    {
        foreach (string warning in outcome.Warnings)
        {
            if (OlderTool().Match(warning) is { Success: true } older)
            {
                return new EfGap(MigrationsWords.ToolOlder(EfTools.Id, older.Groups[1].Value, startup, older.Groups[2].Value),
                    [PinFix(older.Groups[2].Value)], Blocks: false);
            }
        }
        return null;
    }

/// <summary>The EF Core versions a range speaks of, said as a person would: "EF Core 9" for [9.0.1, 10.0.0).</summary>
    public static Words Supports(string range)
    {
        string given = range.Trim();
        if (!given.StartsWith('[') && !given.StartsWith('('))
        {
            return MigrationsWords.SupportsFrom(given);
        }
        string[] ends = given[1..^1].Split(',');
        string from = ends[0].Trim();
        string to = ends.Length > 1 ? ends[1].Trim() : from;
        if (from == to)
        {
            return MigrationsWords.SupportsOnly(from);
        }
        if (given[^1] == ')' && VersionOrder.Major(from) is { } major && VersionOrder.Major(to) == major + 1 && to.EndsWith(".0.0", StringComparison.Ordinal))
        {
            return MigrationsWords.SupportsOnly(major.ToString(CultureInfo.InvariantCulture));
        }
        return to.Length == 0 ? MigrationsWords.SupportsFrom(from) : MigrationsWords.SupportsRange(from, to);
    }

    private static bool IsFrame(string line) => line.TrimStart().StartsWith("at ", StringComparison.Ordinal) || line.TrimStart().StartsWith("---", StringComparison.Ordinal);

    private static string FirstSentence(string text)
    {
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        return (end < 0 ? text : text[..end]).Trim().TrimEnd('.');
    }

    [GeneratedRegex(@"Unable to create (?:a 'DbContext' of type|an object of type) '([^']+)'\.?(.*)")]
    private static partial Regex UnableToCreate();

    [GeneratedRegex(@"The migration '([^']+)' has already been applied to the database")]
    private static partial Regex Applied();

    [GeneratedRegex(@"tools version '([^']+)' is older than that of the runtime '([^']+)'")]
    private static partial Regex OlderTool();
}
