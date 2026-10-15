using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace Passenger.Inspector.Wpf;

/// <summary>Changes literal XAML properties in place on every matching live instance. Existing objects, namescopes,
/// bindings and values of untouched properties are retained; an unsupported edit changes none of the batch.</summary>
internal sealed class XamlHotReload(Inspector inspector)
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

    private sealed record Source(string Original, string Current);
    private sealed record Change(DependencyObject Instance, DependencyProperty Property, object? Value, object? Before, XamlSourceUpdate Source, XamlPropertyUpdate Edit);
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);

    public XamlUpdateResult Apply(XamlUpdate request)
    {
        dispatcher.VerifyAccess();
        if (Expired(request) is { } expired)
        {
            return expired;
        }
        List<DependencyObject> all = Everything();
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
                List<DependencyObject> instances = Find(all, source.File, edit);
                if (instances.Count == 0)
                {
                    problems.Add(Problem(source, edit, "No unambiguous live instance matches this XAML element; restart to load the edit", true));
                    continue;
                }
                foreach (DependencyObject instance in instances)
                {
                    if (Properties.Find(instance, edit.Property) is not { ReadOnly: false } property)
                    {
                        problems.Add(Problem(source, edit, $"{edit.Type}.{edit.Property} is not a writable WPF dependency property; this edit needs a restart", true));
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
        return type.IsEnum ? Enum.Parse(type, text, ignoreCase: true)
            : TypeDescriptor.GetConverter(type).ConvertFrom(null, CultureInfo.InvariantCulture, text);
    }

    private List<DependencyObject> Find(List<DependencyObject> all, string file, XamlPropertyUpdate edit)
    {
        List<(DependencyObject Instance, SourcePlace Place)> sourced = [.. all.Select(a => (Instance: a, Place: Sources.Place(a))).Where(a => a.Place is not null)
            .Select(a => (a.Instance, a.Place!))];
        // The compiler's absolute source path is the identity. A shared filename (even the only one currently loaded)
        // cannot establish which project made a control. WPF pack URIs have already been resolved through the assembly's symbols by Sources.Place.
        List<DependencyObject> inFile = [.. sourced.Where(a => SameFile(a.Place.File, file)).Select(a => a.Instance)];
        if (edit.Class is { } root)
        {
            List<DependencyObject> roots = [.. all.Where(a => a.GetType().FullName == root && (inFile.Contains(a)
                || Descendants(a).Any(inFile.Contains)))];
            if (edit.Root)
            {
                return roots;
            }
            inFile = [.. inFile.Where(a => roots.Any(r => ReferenceEquals(r, a)
                || Shown.IsWithin(a, r)))];
        }
        return [.. sourced.Where(a => SameFile(a.Place.File, file) && a.Place.Line == edit.Line
            && (a.Place.Column == edit.Column || a.Place.Column == edit.Column - 1) && inFile.Contains(a.Instance)
            && HasType(a.Instance.GetType(), edit.Type)).Select(a => a.Instance)];
    }

    private List<DependencyObject> Everything() => [.. inspector.TopLevels().SelectMany(Descendants).Distinct<DependencyObject>(ReferenceEqualityComparer.Instance)];

    private IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        HashSet<DependencyObject> seen = new(ReferenceEqualityComparer.Instance);
        Stack<DependencyObject> left = new([root]);
        while (left.TryPop(out DependencyObject? at))
        {
            if (!seen.Add(at))
            {
                continue;
            }
            yield return at;
            foreach (DependencyObject child in inspector.Children(at))
            {
                left.Push(child);
            }
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(at).OfType<DependencyObject>())
            {
                left.Push(child);
            }
        }
    }
    private static bool HasType(Type? type, string name) => type is not null && (type.Name == name || HasType(type.BaseType, name));

    private static bool SameFile(string first, string second) => Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
