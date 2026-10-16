using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace Passenger.Inspector.Wpf;

/// <summary>Changes literal XAML properties on matching live instances or registers them for verified future views. Objects, namescopes,
/// bindings and values of untouched properties are retained; an unsupported edit changes none of the batch.</summary>
internal sealed class XamlHotReload
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Inspector inspector;
    private readonly Action<XamlUpdateResult> failed;
    private readonly XamlPopulation populations;

    private sealed record Source(string Original, string Current);
    private sealed record Change(DependencyObject Instance, DependencyProperty Property, object? Value, object? Before, object LocalBefore, XamlSourceUpdate Source, XamlPropertyUpdate Edit);
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<XamlUpdateProblem>> failures = new(StringComparer.Ordinal);
    private List<XamlUpdateProblem>? applyingProblems;

    public XamlHotReload(Inspector inspector, Action<XamlUpdateResult> failed)
    {
        this.inspector = inspector;
        this.failed = failed;
        populations = new XamlPopulation(Created);
    }

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
        Dictionary<Type, string> prepared = [];
        int futureRegistrations = 0;
        foreach (XamlSourceUpdate source in request.Files)
        {
            if (failures.TryGetValue(source.File, out IReadOnlyList<XamlUpdateProblem>? previousFailure))
            {
                problems.AddRange(previousFailure);
                continue;
            }
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
            if (plan.Properties.FirstOrDefault() is { } first)
            {
                if (first.Class is not { } root)
                {
                    PrepareChanges(source, plan, all, changes, problems);
                    problems.Add(Problem(source, first, "XAML without an x:Class cannot preserve updates for newly created views; restart to load the edit", true));
                }
                else
                {
                    List<DependencyObject> roots = Roots(all, source.File, root);
                    List<Type> types;
                    if (roots.Count == 0 && !all.Any(a => a.GetType().FullName == root))
                    {
                        Type? uncreated = UncreatedXaml.Prepare(source, known.Original, plan, problems);
                        types = uncreated is null ? [] : [uncreated];
                        if (uncreated is not null)
                        {
                            futureRegistrations++;
                        }
                    }
                    else
                    {
                        PrepareChanges(source, plan, all, changes, problems);
                        types = [.. roots.Select(a => a.GetType()).Distinct()];
                    }
                    foreach (Type type in types)
                    {
                        try
                        {
                            if (populations.Prepare(type, source.File) is { } reason)
                            {
                                problems.Add(Problem(source, first, reason, true));
                            }
                            else
                            {
                                prepared[type] = source.File;
                            }
                        }
                        catch (Exception error)
                        {
                            problems.Add(Problem(source, first, $"{type.Name}'s compiled WPF population could not be inspected: {error.Message}. Restart to load the edit", true));
                        }
                    }
                }
            }
        }
        foreach (string file in request.Files.Select(f => f.File).Distinct(StringComparer.Ordinal))
        {
            if (failures.TryGetValue(file, out IReadOnlyList<XamlUpdateProblem>? populationProblems))
            {
                problems.AddRange(populationProblems.Where(p => !problems.Contains(p)));
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
            return new XamlUpdateResult(true, changes.Count, []) { FutureViewRegistrations = futureRegistrations };
        }
        Dictionary<string, Source?> previous = request.Files.Select(f => f.File).Distinct(StringComparer.Ordinal)
            .ToDictionary(f => f, f => sources.GetValueOrDefault(f), StringComparer.Ordinal);
        Type[] added = [.. prepared.Keys.Where(t => !populations.Contains(t))];
        XamlUpdateResult? refused;
        applyingProblems = [];
        try
        {
            populations.Install(prepared);
            foreach (XamlSourceUpdate source in request.Files)
            {
                sources[source.File] = new Source(sources.GetValueOrDefault(source.File)?.Original ?? source.Before, source.After);
            }
            refused = Commit(changes);
            if (refused is null && applyingProblems.Count > 0)
            {
                Rollback(changes);
                refused = new XamlUpdateResult(false, 0, [.. applyingProblems]);
            }
        }
        catch (Exception error)
        {
            refused = new XamlUpdateResult(false, 0, [.. request.Files.Select(f => new XamlUpdateProblem(f.File, 1, 1,
                $"The WPF population callback could not be installed: {error.Message}. Restart to load the edit", true))]);
        }
        finally
        {
            applyingProblems = null;
        }
        if (refused is not null)
        {
            foreach ((string file, Source? source) in previous)
            {
                if (source is null)
                {
                    sources.Remove(file);
                }
                else
                {
                    sources[file] = source;
                }
            }
            populations.Remove(added);
            return refused;
        }
        return new XamlUpdateResult(true, changes.Count, []) { FutureViewRegistrations = futureRegistrations };
    }

    public void Stop()
    {
        populations.Stop();
        sources.Clear();
        failures.Clear();
    }

    private void Created(string file, DependencyObject root, bool matches)
    {
        if (!sources.TryGetValue(file, out Source? source) || failures.ContainsKey(file))
        {
            return;
        }
        List<XamlUpdateProblem> problems = [];
        try
        {
            dispatcher.VerifyAccess();
            XamlSourceUpdate update = new(file, source.Original, source.Current);
            XamlUpdatePlan plan = XamlUpdatePlan.Create(update, source.Original);
            problems.AddRange(plan.Problems);
            if (!matches)
            {
                problems.Add(new XamlUpdateProblem(file, 1, 1, "The populated component URI does not match the accepted XAML source", true));
            }
            List<Change> changes = [];
            if (problems.Count == 0)
            {
                PrepareChanges(update, plan, [.. Descendants(root)], changes, problems);
            }
            if (problems.Count == 0 && Commit(changes) is { } refused)
            {
                problems.AddRange(refused.Problems);
            }
        }
        catch (Exception error)
        {
            problems.Add(new XamlUpdateProblem(file, 1, 1, error.Message, true));
        }
        if (problems.Count > 0)
        {
            IReadOnlyList<XamlUpdateProblem> reported = [.. problems.Select(p => p with
            {
                Message = $"A newly created {root.GetType().Name} could not use the accepted XAML edit: {p.Message}. Restart to load a consistent view",
                RestartRequired = true,
            })];
            failures[file] = reported;
            if (applyingProblems is { } current)
            {
                current.AddRange(reported);
            }
            else
            {
                failed(new XamlUpdateResult(false, 0, reported));
            }
        }
    }

    private void PrepareChanges(XamlSourceUpdate source, XamlUpdatePlan plan, List<DependencyObject> all, List<Change> changes, List<XamlUpdateProblem> problems)
    {
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
                    object local = DependencyProperty.UnsetValue;
                    if (edit.Added)
                    {
                        ValueSource valueSource = DependencyPropertyHelper.GetValueSource(instance, property);
                        if (valueSource.IsExpression || valueSource.IsAnimated || valueSource.IsCurrent)
                        {
                            problems.Add(Problem(source, edit, $"Adding {edit.Property} would replace a binding, animation, resource or current value set by the app; restart to load the edit", true));
                            continue;
                        }
                        local = instance.ReadLocalValue(property);
                    }
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
                    changes.Add(new Change(instance, property, value, before, local, source, edit));
                }
                catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException or OverflowException)
                {
                    problems.Add(Problem(source, edit, $"{edit.Property}: {error.Message}", false));
                }
            }
        }
    }

    private static XamlUpdateResult? Commit(List<Change> changes)
    {
        List<Change> applied = [];
        Change? attempted = null;
        try
        {
            foreach (Change change in changes)
            {
                attempted = change;
                applied.Add(change);
                if (change.Edit.Added)
                {
                    change.Instance.SetValue(change.Property, change.Value);
                }
                else
                {
                    change.Instance.SetCurrentValue(change.Property, change.Value);
                }
            }
        }
        catch (Exception error)
        {
            Rollback(applied);
            return new XamlUpdateResult(false, 0, [Problem(attempted!.Source, attempted.Edit,
                $"The app refused the {attempted.Edit.Property} update: {error.Message}. Restart to restore a known state", true)]);
        }
        return null;
    }

    private static void Rollback(List<Change> changes)
    {
        foreach (Change change in changes.AsEnumerable().Reverse())
        {
            try
            {
                if (!change.Edit.Added)
                {
                    change.Instance.SetCurrentValue(change.Property, change.Before);
                }
                else if (change.LocalBefore == DependencyProperty.UnsetValue)
                {
                    change.Instance.ClearValue(change.Property);
                }
                else
                {
                    change.Instance.SetValue(change.Property, change.LocalBefore);
                }
            }
            catch
            {
                // A throwing application setter may have side effects: the result requires a restart either way.
            }
        }
    }

    private static XamlUpdateProblem Problem(XamlSourceUpdate source, XamlPropertyUpdate edit, string message, bool restart) =>
        new(source.File, edit.EditedLine, edit.EditedColumn, message, restart);

    private static XamlUpdateResult? Expired(XamlUpdate request) => request.ExpiresUtc is long expires && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expires
        ? new XamlUpdateResult(false, 0, [.. request.Files.Select(f => new XamlUpdateProblem(f.File, 1, 1,
            "The XAML update expired before it could be applied; no XAML properties were changed", false))])
        : null;

    internal static object? Convert(string text, Type type)
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
            List<DependencyObject> roots = Roots(all, file, root);
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

    private List<DependencyObject> Roots(List<DependencyObject> all, string file, string root) => [.. all.Where(a => a.GetType().FullName == root
        && Descendants(a).Any(d => Sources.Place(d) is { } place && SameFile(place.File, file)))];

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
