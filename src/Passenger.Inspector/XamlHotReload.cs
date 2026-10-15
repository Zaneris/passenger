using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Passenger.Inspector;

/// <summary>Changes literal XAML properties in place on every matching live instance. Existing objects, namescopes,
/// bindings and values of untouched properties are retained; an unsupported edit changes none of the batch.</summary>
internal sealed class XamlHotReload
{
    private sealed record Source(string Original, string Current);
    private sealed record Change(AvaloniaObject Instance, AvaloniaProperty Property, object? Value, object? Before, XamlSourceUpdate Source, XamlPropertyUpdate Edit);
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);

    public XamlUpdateResult Apply(XamlUpdate request)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Expired(request) is { } expired)
        {
            return expired;
        }
        List<AvaloniaObject> all = BindingReader.Everything();
        List<Change> changes = [];
        List<XamlUpdateProblem> problems = [];
        foreach (XamlSourceUpdate source in request.Files)
        {
            Source known = sources.GetValueOrDefault(source.File) ?? new Source(source.Before, source.Before);
            if (known.Current != source.Before)
            {
                problems.Add(new XamlUpdateProblem(source.File, 1, 1, "The XAML changed since the last applied update: restart to establish its source again", true));
                continue;
            }
            XamlUpdatePlan plan = XamlUpdatePlan.Create(source, known.Original);
            problems.AddRange(plan.Problems);
            if (plan.Problems.Count > 0)
            {
                continue;
            }
            foreach (XamlPropertyUpdate edit in plan.Properties)
            {
                List<AvaloniaObject> instances = Find(all, source.File, edit);
                if (instances.Count == 0)
                {
                    problems.Add(Problem(source, edit, "No unambiguous live instance matches this XAML element; restart to load the edit", true));
                    continue;
                }
                foreach (AvaloniaObject instance in instances)
                {
                    if (BindingReader.PropertyNamed(instance, edit.Property) is not { IsReadOnly: false } property)
                    {
                        problems.Add(Problem(source, edit, $"{edit.Type}.{edit.Property} is not a writable Avalonia property; this edit needs a restart", true));
                        continue;
                    }
                    try
                    {
                        object? value = Convert(edit.Value, property.PropertyType);
                        if (!property.IsValidValue(value))
                        {
                            throw new FormatException($"'{edit.Value}' is not valid for {edit.Property}");
                        }
                        object? before;
                        try
                        {
                            before = instance.GetValue(property);
                        }
                        catch (Exception error)
                        {
                            problems.Add(Problem(source, edit, $"{edit.Property}: the app refused to read the previous value: {error.Message}", false));
                            continue;
                        }
                        changes.Add(new Change(instance, property, value, before, source, edit));
                    }
                    catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException or OverflowException)
                    {
                        problems.Add(Problem(source, edit, $"{edit.Property}: {error.Message}", false));
                    }
                }
            }
        }
        if (problems.Count > 0)
        {
            return new XamlUpdateResult(false, 0, problems);
        }
        // Traversal and application-defined converters may be slow too: a request that expired during preflight must
        // never mutate the app after Passenger has already reported that its answer did not arrive.
        if (Expired(request) is { } overdue)
        {
            return overdue;
        }
        if (request.ValidateOnly)
        {
            return new XamlUpdateResult(true, changes.Count, []);
        }
        List<Change> applied = [];
        Change? attempted = null;
        try
        {
            foreach (Change change in changes)
            {
                attempted = change;
                applied.Add(change);
                change.Instance.SetCurrentValue(change.Property, change.Value);
            }
        }
        catch (Exception error)
        {
            foreach (Change change in applied.AsEnumerable().Reverse())
            {
                try
                {
                    change.Instance.SetCurrentValue(change.Property, change.Before);
                }
                catch
                {
                    // A throwing application setter may have side effects: the result requires a restart either way.
                }
            }
            return new XamlUpdateResult(false, 0, [Problem(attempted!.Source, attempted.Edit,
                $"The app refused the {attempted.Edit.Property} update: {error.Message}. Restart to restore a known state", true)]);
        }
        foreach (XamlSourceUpdate source in request.Files)
        {
            sources[source.File] = new Source(sources.GetValueOrDefault(source.File)?.Original ?? source.Before, source.After);
        }
        return new XamlUpdateResult(true, changes.Count, []);
    }

    private static XamlUpdateProblem Problem(XamlSourceUpdate source, XamlPropertyUpdate edit, string message, bool restart) =>
        new(source.File, edit.EditedLine, edit.EditedColumn, message, restart);

    private static XamlUpdateResult? Expired(XamlUpdate request) => request.ExpiresUtc is long expires && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expires
        ? new XamlUpdateResult(false, 0, [.. request.Files.Select(f => new XamlUpdateProblem(f.File, 1, 1,
            "The XAML update expired before it could be applied; no XAML properties were changed", false))])
        : null;

    private static object? Convert(string text, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(object))
        {
            return text;
        }
        if (type == typeof(IBrush) || type == typeof(Brush))
        {
            return Brush.Parse(text);
        }
        return type.IsEnum ? Enum.Parse(type, text, ignoreCase: true)
            : TypeDescriptor.GetConverter(type).ConvertFrom(null, CultureInfo.InvariantCulture, text);
    }

    private static List<AvaloniaObject> Find(List<AvaloniaObject> all, string file, XamlPropertyUpdate edit)
    {
        List<(AvaloniaObject Instance, SourcePlace Place)> sourced = [.. all.Select(a => (Instance: a, Place: Details.Place(a))).Where(a => a.Place is not null)
            .Select(a => (a.Instance, a.Place!))];
        // The compiler's absolute source path is the identity. A shared filename (even the only one currently loaded)
        // cannot establish which project made a control. Unresolved avares: URIs must not guess another source's file.
        List<AvaloniaObject> inFile = [.. sourced.Where(a => SameFile(a.Place.File, file)).Select(a => a.Instance)];
        if (edit.Class is { } root)
        {
            List<AvaloniaObject> roots = [.. all.Where(a => a.GetType().FullName == root && (inFile.Contains(a)
                || a is ILogical logical && logical.GetLogicalDescendants().OfType<AvaloniaObject>().Any(inFile.Contains)))];
            if (edit.Root)
            {
                return roots;
            }
            inFile = [.. inFile.Where(a => roots.Any(r => ReferenceEquals(r, a)
                || r is Visual visual && visual.GetVisualDescendants().Any(v => ReferenceEquals(v, a))))];
        }
        return [.. sourced.Where(a => SameFile(a.Place.File, file) && a.Place.Line == edit.Line
            && (a.Place.Column == edit.Column || a.Place.Column == edit.Column - 1) && inFile.Contains(a.Instance)
            && HasType(a.Instance.GetType(), edit.Type)).Select(a => a.Instance)];
    }

    private static bool HasType(Type? type, string name) => type is not null && (type.Name == name || HasType(type.BaseType, name));

    private static bool SameFile(string first, string second) => Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
