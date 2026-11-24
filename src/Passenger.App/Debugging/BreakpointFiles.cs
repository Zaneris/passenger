using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>A folder's line, method and exception breakpoints as a file of their own, which Export writes and Import reads
/// on another machine or in another clone (docs/breakpoints.md): JSON, lines and columns from one, each file named from the
/// open folder in forward slashes, so the breakpoints land on the same code wherever the folder is. What a breakpoint does
/// by default is left out.</summary>
public static class BreakpointFiles
{
    /// <summary>The version of the format, the file's first member.</summary>
    public const int Format = 1;

    private const string FormatName = "passengerBreakpoints";

    /// <summary>The breakpoints kept as a file's text, each file named from <paramref name="folder"/>.</summary>
    public static string Write(BreakpointsState kept, string folder)
    {
        JsonObject file = new() { [FormatName] = Format };
        if (kept.Breakpoints.Count > 0)
        {
            file["lines"] = new JsonArray([.. kept.Breakpoints.Select(b =>
            {
                JsonObject line = new() { ["file"] = PathNames.Route(b.Path, folder), ["line"] = b.Line + 1 };
                if (b.Column is int column)
                {
                    line["column"] = column + 1;
                }
                if (b.Attribute is { } attribute)
                {
                    line["attribute"] = attribute;
                }
                Does(line, b.Enabled, b.Suspend, b.Condition, b.HitCondition, b.LogMessage);
                Arranged(line, b.Temporary, b.WaitsFor, folder);
                return (JsonNode)line;
            })]);
        }
        if (kept.Methods is { Count: > 0 } methods)
        {
            file["methods"] = new JsonArray([.. methods.Select(m =>
            {
                JsonObject method = new() { ["name"] = m.Name };
                if (m.Path is { } path)
                {
                    method["file"] = PathNames.Route(path, folder);
                    method["line"] = m.Line + 1;
                }
                Does(method, m.Enabled, m.Suspend, m.Condition, m.HitCondition, m.LogMessage);
                Arranged(method, m.Temporary, m.WaitsFor, folder);
                return (JsonNode)method;
            })]);
        }
        if (kept.Exceptions is { Count: > 0 } exceptions)
        {
            file["exceptions"] = new JsonArray([.. exceptions.Select(e =>
            {
                JsonObject exception = new() { ["type"] = e.Type ?? ExceptionBreakpoint.AnyException };
                ExceptionScenarios scenarios = ScenariosOf(e);
                if (scenarios != (e.Type is null ? ExceptionBreakpoint.AnyExceptionScenarios : ExceptionScenarios.All))
                {
                    exception["scenarios"] = new JsonArray([.. ExceptionScenarioNames.Of(scenarios).Select(n => (JsonNode)n)]);
                }
                Does(exception, e.Enabled, e.Suspend, e.Condition, e.HitCondition, e.LogMessage);
                return (JsonNode)exception;
            })]);
        }
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The breakpoints a file's text holds, each file found from <paramref name="folder"/>; a text that is not such
    /// a file, or one of a later format, throws saying what is wrong with it.</summary>
    public static BreakpointsState Read(string text, string folder)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new FormatException($"It is not JSON: {error.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object || JsonMembers.Number(root, FormatName) is not int format)
        {
            throw new FormatException($"It is no file of breakpoints: it begins with no \"{FormatName}\" number");
        }
        if (format > Format)
        {
            throw new FormatException($"It is of a later format ({format}) than this Passenger reads ({Format})");
        }
        List<KeptBreakpoint> lines = [];
        foreach (JsonElement line in JsonMembers.Items(root, "lines"))
        {
            (string path, int at) = Place(line, folder, "A line breakpoint");
            int? column = JsonMembers.Number(line, "column") is int written ? Math.Max(0, written - 1) : null;
            lines.Add(new KeptBreakpoint(path, at)
            {
                Column = column,
                Attribute = JsonMembers.Text(line, "attribute"),
                Enabled = JsonMembers.Bool(line, "enabled") ?? true,
                Suspend = JsonMembers.Bool(line, "suspend") ?? true,
                Condition = JsonMembers.Text(line, "condition"),
                HitCondition = JsonMembers.Text(line, "hitCount"),
                LogMessage = JsonMembers.Text(line, "logMessage"),
                Temporary = JsonMembers.Bool(line, "temporary") ?? false,
                WaitsFor = WaitsFor(line, folder),
            });
        }
        List<KeptMethodBreakpoint> methods = [];
        foreach (JsonElement method in JsonMembers.Items(root, "methods"))
        {
            string name = JsonMembers.Text(method, "name") is { Length: > 0 } named ? named : throw new FormatException("A method breakpoint names no method");
            (string? path, int at) = JsonMembers.Value(method, "file") is null ? (null, 0) : Place(method, folder, $"The method breakpoint on {name}");
            methods.Add(new KeptMethodBreakpoint(name)
            {
                Path = path,
                Line = at,
                Enabled = JsonMembers.Bool(method, "enabled") ?? true,
                Suspend = JsonMembers.Bool(method, "suspend") ?? true,
                Condition = JsonMembers.Text(method, "condition"),
                HitCondition = JsonMembers.Text(method, "hitCount"),
                LogMessage = JsonMembers.Text(method, "logMessage"),
                Temporary = JsonMembers.Bool(method, "temporary") ?? false,
                WaitsFor = WaitsFor(method, folder),
            });
        }
        List<KeptExceptionBreakpoint> exceptions = [];
        foreach (JsonElement exception in JsonMembers.Items(root, "exceptions"))
        {
            string type = JsonMembers.Text(exception, "type") is { Length: > 0 } named ? named : throw new FormatException("An exception breakpoint names no type");
            bool any = type == ExceptionBreakpoint.AnyException;
            ExceptionScenarios scenarios;
            try
            {
                scenarios = JsonMembers.Value(exception, "scenarios") is null ? any ? ExceptionBreakpoint.AnyExceptionScenarios : ExceptionScenarios.All
                    : ExceptionScenarioNames.Parse(JsonMembers.Texts(exception, "scenarios"));
            }
            catch (ArgumentException unknown)
            {
                throw new FormatException($"The exception breakpoint on {type}: {unknown.Message}");
            }
            exceptions.Add(new KeptExceptionBreakpoint(any ? null : type)
            {
                Enabled = JsonMembers.Bool(exception, "enabled") ?? true,
                Suspend = JsonMembers.Bool(exception, "suspend") ?? true,
                Condition = JsonMembers.Text(exception, "condition"),
                HitCondition = JsonMembers.Text(exception, "hitCount"),
                LogMessage = JsonMembers.Text(exception, "logMessage"),
                ThrownInUserCode = scenarios.HasFlag(ExceptionScenarios.ThrownInUserCode),
                ThrownInExternalCode = scenarios.HasFlag(ExceptionScenarios.ThrownInExternalCode),
                HandledInUserCode = scenarios.HasFlag(ExceptionScenarios.HandledInUserCode),
                HandledInExternalCode = scenarios.HasFlag(ExceptionScenarios.HandledInExternalCode),
                Unhandled = scenarios.HasFlag(ExceptionScenarios.Unhandled),
            });
        }
        return new BreakpointsState { Breakpoints = lines, Methods = methods, Exceptions = exceptions };
    }

    /// <summary>What a breakpoint does, but what it does by default.</summary>
    private static void Does(JsonObject written, bool enabled, bool suspend, string? condition, string? hitCondition, string? logMessage)
    {
        if (!enabled)
        {
            written["enabled"] = false;
        }
        if (!suspend)
        {
            written["suspend"] = false;
        }
        if (condition is not null)
        {
            written["condition"] = condition;
        }
        if (hitCondition is not null)
        {
            written["hitCount"] = hitCondition;
        }
        if (logMessage is not null)
        {
            written["logMessage"] = logMessage;
        }
    }

    /// <summary>Whether a line or method breakpoint is temporary, and what it waits for.</summary>
    private static void Arranged(JsonObject written, bool temporary, KeptWaitFor? waitsFor, string folder)
    {
        if (temporary)
        {
            written["temporary"] = true;
        }
        if (waitsFor is { Method: { } method })
        {
            written["waitFor"] = new JsonObject { ["method"] = method };
        }
        else if (waitsFor is { Path: { } path })
        {
            JsonObject waited = new() { ["file"] = PathNames.Route(path, folder), ["line"] = waitsFor.Line + 1 };
            if (waitsFor.Column is int column)
            {
                waited["column"] = column + 1;
            }
            if (waitsFor.Attribute is { } attribute)
            {
                waited["attribute"] = attribute;
            }
            written["waitFor"] = waited;
        }
    }

    private static KeptWaitFor? WaitsFor(JsonElement breakpoint, string folder)
    {
        if (JsonMembers.Object(breakpoint, "waitFor") is not { } waited)
        {
            return null;
        }
        if (JsonMembers.Text(waited, "method") is { Length: > 0 } method)
        {
            return new KeptWaitFor { Method = method };
        }
        (string path, int line) = Place(waited, folder, "What a breakpoint waits for");
        return new KeptWaitFor
        {
            Path = path,
            Line = line,
            Column = JsonMembers.Number(waited, "column") is int column ? Math.Max(0, column - 1) : null,
            Attribute = JsonMembers.Text(waited, "attribute"),
        };
    }

    /// <summary>A breakpoint's file, found from the folder where it is named from there, and its line from zero.</summary>
    private static (string Path, int Line) Place(JsonElement breakpoint, string folder, string what)
    {
        string file = JsonMembers.Text(breakpoint, "file") is { Length: > 0 } named ? named : throw new FormatException($"{what} names no file");
        int line = JsonMembers.Number(breakpoint, "line") is int written and >= 1 ? written - 1 : throw new FormatException($"{what} in {file} has no line from 1");
        return (PathNames.Canonical(Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(folder, file))), line);
    }

    private static ExceptionScenarios ScenariosOf(KeptExceptionBreakpoint e) =>
        (e.ThrownInUserCode ? ExceptionScenarios.ThrownInUserCode : 0)
        | (e.ThrownInExternalCode ? ExceptionScenarios.ThrownInExternalCode : 0)
        | (e.HandledInUserCode ? ExceptionScenarios.HandledInUserCode : 0)
        | (e.HandledInExternalCode ? ExceptionScenarios.HandledInExternalCode : 0)
        | (e.Unhandled ? ExceptionScenarios.Unhandled : 0);
}
