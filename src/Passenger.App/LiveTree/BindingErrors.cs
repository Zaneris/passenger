using System.Text.RegularExpressions;
using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.App.LiveTree;

/// <summary>A failed binding as Problems' Bindings lists it: what the app said of it, and the problem it goes to (the
/// misspelt member in its XAML when that is where the app's build said, else its control's element).</summary>
public sealed record BindingProblem(BindingFailure Failure, Problem Problem)
{
    /// <summary>What it binds and how often it failed, beside its message: "TextBlock#Caption.Text · Nmae · MainWindow.axaml:9:20 · ×3".</summary>
    public string Detail => string.Join(" · ", new[] { $"{Failure.Control}.{Failure.Property}", Failure.Path, Problem.Path is null ? null : Problem.Where, $"×{Numbers.Said(Failure.Count)}" }
        .Where(part => !string.IsNullOrEmpty(part)));
}

/// <summary>An Avalonia or WPF app's failed bindings, as the Run and Debug consoles and Problems show them: a console's line names
/// the control's XAML element first, as a compiler's message names its place, so the console links it; a path that names a
/// member its source's type does not have is offered the type's nearest members as fixes, at the member in the XAML.</summary>
public static partial class BindingErrors
{
    /// <summary>A failure as a console says it, its problem's place first (relative to the open folder when inside it), and
    /// how many times it has failed when it comes again: "MainWindow.axaml(10,26): binding error: TextBlock#Caption.Text to
    /// Nmae: Could not find ... Did you mean Name? (×3)".</summary>
    public static string Line(BindingProblem problem, string? folder)
    {
        (BindingFailure failure, Problem at) = (problem.Failure, problem.Problem);
        string place = at.Path is { } file ? $"{PathNames.Shown(file, folder)}({at.Position.Line + 1},{at.Position.Column + 1}): " : string.Empty;
        string again = failure.Count > 1 ? $" (×{Numbers.Said(failure.Count)})" : string.Empty;
        return $"{place}binding error: {failure.Control}.{failure.Property} to {failure.Path}: {Message(failure)}{again}\n";
    }

    /// <summary>What an app could not tell of its failed bindings, as Problems' Bindings says it under them: the failures it
    /// counted past the bindings it lists, and the messages its inspector left out; nothing when it told all.</summary>
    public static IEnumerable<(string Said, string Why)> Untold(LiveApp app)
    {
        if (app.Untold.Bindings > 0)
        {
            yield return ($"{Plural.Of(app.Untold.Bindings, "more binding failure")}, not listed",
                $"{app.Name} lists its first {Numbers.Said(app.Bindings.Count)} failed bindings and only counts the failures of any after them");
        }
        if (app.Untold.Messages > 0)
        {
            yield return ($"{Numbers.Said(app.Untold.Messages)} of {app.Name}'s inspector's own messages left out", "Too many waited to be sent at once; the IDE's log has those it sent");
        }
    }

    /// <summary>A console's line as an app first counts failures past the bindings it lists, which it no longer writes there.</summary>
    public static string UntoldLine(LiveApp app) =>
        $"binding errors past the first {Numbers.Said(app.Bindings.Count)} failed bindings are counted, not listed: Problems' Bindings says how many\n";

    /// <summary>The framework's message, and the nearest members when the path named one its source's type does not have.</summary>
    public static string Message(BindingFailure failure) => failure.Nearest.Count == 0
        ? failure.Message
        : $"{failure.Message.TrimEnd()} Did you mean {Plural.Either(failure.Nearest)}?";

    /// <summary>A failure the debugger stopped at (Avalonia Binding Errors), as the Debug console says it, its place first so
    /// the console links it: "MainWindow.axaml(10,26): paused on a binding error: TextBlock#Caption.Text to Nmae (DataContext
    /// MainViewModel): Could not find ... Did you mean Name?".</summary>
    public static string StopLine(BindingProblem problem, string? folder)
    {
        Problem at = problem.Problem;
        string place = at.Path is { } file ? $"{PathNames.Shown(file, folder)}({at.Position.Line + 1},{at.Position.Column + 1}): " : string.Empty;
        return $"{place}paused on a binding error: {Stopped(problem.Failure)}\n";
    }

    /// <summary>What a failure the debugger stopped at binds, the part of its path that failed when not the whole, its
    /// control's DataContext and why: "TextBlock#Caption.Text to Person.Nmae at Nmae (DataContext MainViewModel): Could not
    /// find ... Did you mean Name?".</summary>
    public static string Stopped(BindingFailure failure)
    {
        string at = failure.ErrorPoint is { Length: > 0 } point && point != failure.Path ? $" at {point}" : string.Empty;
        return $"{failure.Control}.{failure.Property} to {failure.Path}{at} (DataContext {failure.DataContext ?? "null"}): {Message(failure)}";
    }

    /// <summary>Where a problem is, as the status bar names it: "MainWindow.axaml:10".</summary>
    public static string? Where(BindingProblem problem) =>
        problem.Problem.Path is { } file ? $"{Path.GetFileName(file)}:{problem.Problem.Position.Line + 1}" : null;

    /// <summary>A XAML file's text as the editor has it, else as it is on disk, else none.</summary>
    public static TextSnapshot? TextOf(string file, IEnumerable<OpenDocument> documents)
    {
        if (!FoundPaths.Askable(file) || !File.Exists(file))
        {
            return null;
        }
        string path = PathNames.Canonical(file);
        if (documents.FirstOrDefault(d => PathNames.Same(d.Path, path)) is { } open)
        {
            return open.Text.Snapshot;
        }
        try
        {
            return FileText.Load(path) is { } read ? TextSnapshot.From(read.Text) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A XAML file's path as the editor knows it, when it is there; a share's or a device's as the app named it,
    /// never asked.</summary>
    private static string Canonical(string file) => FoundPaths.Askable(file) && File.Exists(file) ? PathNames.Canonical(file) : file;

    /// <summary>The problem a failure is in Problems: at its misspelt member in <paramref name="text"/> (its file's text,
    /// when there is one to read), else at its control's element, in the editor's lines (<see cref="AppPlaces"/>).</summary>
    public static BindingProblem Problem(BindingFailure failure, TextSnapshot? text)
    {
        string? path = failure.Place is { } at ? Canonical(at.File) : null;
        TextPosition position = failure.Place is { } place ? AppPlaces.PositionOf(place.File, place.Line, place.Column, text) : default;
        if (text is not null && MemberIn(text, failure) is { } member)
        {
            position = text.GetPosition(member.Start);
        }
        ProblemSeverity severity = failure.Level is "Error" or "Fatal" ? ProblemSeverity.Error : ProblemSeverity.Warning;
        return new BindingProblem(failure, new Problem(severity, Message(failure), null, path, position, null));
    }

    /// <summary>Where the member a failed path names is in its XAML: in its control's start tag, in the attribute that sets
    /// its property (a markup extension's path), else inside that property's own element; null when it is not there (a
    /// binding a style's setter gives, the file changed since the app was built).</summary>
    public static TextRange? MemberIn(TextSnapshot text, BindingFailure failure)
    {
        // The control's place is its XAML compiler's, whose lines a lone '\r' ends too.
        if (failure is not { Place: { } place, Member: { Length: > 0 } member } || place.Line < 1
            || place.Line > (ServerPlaces.Compiled(place.File, text)?.LineCount ?? text.LineCount))
        {
            return null;
        }
        int start = text.GetOffset(AppPlaces.PositionOf(place.File, place.Line, place.Column, text));
        string rest = text.GetText(start, Math.Min(text.Length - start, 64 * 1024));
        int end = -1;
        char quote = '\0';
        for (int i = 0; i < rest.Length && end < 0; i++)
        {
            char c = rest[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                end = i;
            }
        }
        if (end < 0)
        {
            return null;
        }
        string property = failure.Property[(failure.Property.LastIndexOf('.') + 1)..];
        Regex word = new($@"(?<![\w$#]){Regex.Escape(member)}(?!\w)");
        foreach (Match attribute in Attribute().Matches(rest[..end]))
        {
            string name = attribute.Groups["name"].Value;
            if ((name == property || name.EndsWith("." + property, StringComparison.Ordinal) || name == failure.Property)
                && word.Match(attribute.Groups["value"].Value) is { Success: true } found)
            {
                return new TextRange(start + attribute.Groups["value"].Index + found.Index, member.Length);
            }
        }
        // <TextBlock.Text><Binding Path="Nmae" /></TextBlock.Text>, its element named as the start tag names the control.
        if (Name().Match(rest) is { Success: true } element
            && rest.IndexOf($"<{element.Value}.{property}", end, StringComparison.Ordinal) is int opened and >= 0
            && rest.IndexOf($"</{element.Value}.{property}", opened, StringComparison.Ordinal) is int closed and >= 0
            && word.Match(rest, opened, closed - opened) is { Success: true } inside)
        {
            return new TextRange(start + inside.Index, member.Length);
        }
        return null;
    }

    /// <summary>The fixes for the failed bindings at an offset of an open XAML file: each nearest member in place of the
    /// misspelt one, for a failure whose member the offset is on.</summary>
    public static IReadOnlyList<EditorFix> Fixes(OpenDocument document, int offset, IEnumerable<BindingFailure> failures)
    {
        List<EditorFix> fixes = [];
        TextSnapshot? text = null;
        foreach (BindingFailure failure in failures)
        {
            if (failure is not { Place: { } place, Member: { } member, Nearest.Count: > 0 } || !PathNames.Same(Canonical(place.File), document.Path))
            {
                continue;
            }
            text ??= document.Text.Snapshot;
            if (MemberIn(text, failure) is not { } range || offset < range.Start || offset > range.End)
            {
                continue;
            }
            foreach (string nearest in failure.Nearest)
            {
                EditorFix fix = new($"Change '{member}' to '{nearest}'", $"a member of {Short(failure.Type)}", range, nearest);
                if (!fixes.Contains(fix))
                {
                    fixes.Add(fix);
                }
            }
        }
        return fixes;
    }

    /// <summary>A type as Avalonia names it, without its namespace.</summary>
    private static string Short(string? type) => type is null ? "its source" : TypeNames.Short(type);

    /// <summary>An attribute of a start tag and its value, in either quotes.</summary>
    [GeneratedRegex(@"(?<name>[\w:.]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')")]
    private static partial Regex Attribute();

    /// <summary>The element's name at the start of its start tag.</summary>
    [GeneratedRegex(@"^[\w:.]+")]
    private static partial Regex Name();
}
