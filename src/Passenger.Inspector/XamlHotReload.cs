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
internal sealed class XamlHotReload(Action<XamlUpdateResult> failed)
{
    private sealed record Source(string Original, string Current);
    private sealed record Change(AvaloniaObject Instance, AvaloniaProperty Property, object? Value, object? Before, XamlSourceUpdate Source, XamlPropertyUpdate Edit);
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, XamlPopulation> populations = [];
    private readonly Dictionary<string, IReadOnlyList<XamlUpdateProblem>> failures = new(StringComparer.Ordinal);
    private List<XamlUpdateProblem>? applyingProblems;

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
        Dictionary<Type, XamlPopulation> prepared = [];
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
            PrepareChanges(source, plan, all, changes, problems);
            PreparePopulations(source, plan, all, prepared, problems);
        }
        // A converter can construct a view too. A population failure observed during preflight must not be hidden by
        // the success reply which would otherwise follow its asynchronous diagnostic event.
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
            return new XamlUpdateResult(true, changes.Count, []);
        }
        Dictionary<string, Source?> previous = request.Files.Select(f => f.File).Distinct(StringComparer.Ordinal)
            .ToDictionary(f => f, f => sources.GetValueOrDefault(f), StringComparer.Ordinal);
        List<XamlPopulation> installed = [];
        XamlUpdateResult? refused;
        applyingProblems = [];
        try
        {
            foreach (XamlPopulation population in prepared.Values)
            {
                population.Install();
                installed.Add(population);
                populations[population.Type] = population;
            }
            // A property setter can construct another view. That view must see this same accepted source, rather than
            // the previous edit; if a setter refuses, both the source and our newly installed overrides are restored.
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
                $"The compiled XAML population override could not be installed: {error.Message}. Restart to load the edit", true))]);
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
            foreach (XamlPopulation population in installed)
            {
                population.Restore();
                populations.Remove(population.Type);
            }
            return refused;
        }
        return new XamlUpdateResult(true, changes.Count, []);
    }

    public void Stop()
    {
        foreach (XamlPopulation population in populations.Values)
        {
            population.Restore();
        }
        populations.Clear();
        sources.Clear();
        failures.Clear();
    }

    private void PreparePopulations(XamlSourceUpdate source, XamlUpdatePlan plan, List<AvaloniaObject> all,
        Dictionary<Type, XamlPopulation> prepared, List<XamlUpdateProblem> problems)
    {
        if (plan.Properties.Count == 0)
        {
            return;
        }
        XamlPropertyUpdate edit = plan.Properties[0];
        if (edit.Class is not { } root)
        {
            problems.Add(Problem(source, edit, "XAML without an x:Class cannot preserve updates for newly created views; restart to load the edit", true));
            return;
        }
        foreach (Type type in Roots(all, source.File, root).Select(a => a.GetType()).Distinct())
        {
            try
            {
                if (populations.TryGetValue(type, out XamlPopulation? existing) || prepared.TryGetValue(type, out existing))
                {
                    if (!SameFile(existing.File, source.File) || populations.ContainsKey(type) && !existing.IsInstalled)
                    {
                        problems.Add(Problem(source, edit, $"{type.Name}'s compiled XAML population changed or belongs to another source; restart to load the edit", true));
                    }
                    continue;
                }
                (XamlPopulation? population, string? problem) = XamlPopulation.Prepare(type, source.File, made => Created(source.File, made));
                if (population is not null)
                {
                    prepared[type] = population;
                }
                else
                {
                    problems.Add(Problem(source, edit, problem!, true));
                }
            }
            catch (Exception error)
            {
                problems.Add(Problem(source, edit, $"{type.Name}'s compiled XAML population could not be inspected: {error.Message}. Restart to load the edit", true));
            }
        }
    }

    private void Created(string file, AvaloniaObject root)
    {
        if (!sources.TryGetValue(file, out Source? source) || failures.ContainsKey(file))
        {
            return;
        }
        List<XamlUpdateProblem> problems = [];
        try
        {
            Dispatcher.UIThread.VerifyAccess();
            XamlSourceUpdate update = new(file, source.Original, source.Current);
            XamlUpdatePlan plan = XamlUpdatePlan.Create(update, source.Original);
            problems.AddRange(plan.Problems);
            List<Change> changes = [];
            PrepareChanges(update, plan, BindingReader.Everything(root), changes, problems);
            if (problems.Count == 0 && Commit(changes) is { } refusal)
            {
                problems.AddRange(refusal.Problems);
            }
        }
        catch (Exception error)
        {
            problems.Add(new XamlUpdateProblem(file, 1, 1, error.Message, true));
        }
        if (problems.Count > 0)
        {
            // This is a later observation, not a different answer to the Apply which already completed. Existing views
            // retain that update; this new view kept its compiled population and needs a restart to agree with them.
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

    private static void PrepareChanges(XamlSourceUpdate source, XamlUpdatePlan plan, List<AvaloniaObject> all,
        List<Change> changes, List<XamlUpdateProblem> problems)
    {
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
                change.Instance.SetCurrentValue(change.Property, change.Value);
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
                change.Instance.SetCurrentValue(change.Property, change.Before);
            }
            catch
            {
                // An application setter may have side effects: a failed rollback still requires a restart.
            }
        }
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
            List<AvaloniaObject> roots = Roots(all, file, root);
            if (edit.Root)
            {
                return roots;
            }
            inFile = [.. inFile.Where(a => roots.Any(r => Within(r, a)))];
        }
        return [.. sourced.Where(a => SameFile(a.Place.File, file) && a.Place.Line == edit.Line
            && (a.Place.Column == edit.Column || a.Place.Column == edit.Column - 1) && inFile.Contains(a.Instance)
            && HasType(a.Instance.GetType(), edit.Type)).Select(a => a.Instance)];
    }

    private static List<AvaloniaObject> Roots(List<AvaloniaObject> all, string file, string root)
    {
        HashSet<AvaloniaObject> inFile = [.. all.Where(a => Details.Place(a) is { } at && SameFile(at.File, file))];
        return [.. all.Where(a => a.GetType().FullName == root && (inFile.Contains(a)
            || a is ILogical logical && logical.GetLogicalDescendants().OfType<AvaloniaObject>().Any(inFile.Contains)))];
    }

    private static bool Within(AvaloniaObject root, AvaloniaObject item) => ReferenceEquals(root, item)
        || root is Visual visual && visual.GetVisualDescendants().Any(v => ReferenceEquals(v, item))
        || root is ILogical logical && logical.GetLogicalDescendants().Any(v => ReferenceEquals(v, item));

    private static bool HasType(Type? type, string name) => type is not null && (type.Name == name || HasType(type.BaseType, name));

    private static bool SameFile(string first, string second) => Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
