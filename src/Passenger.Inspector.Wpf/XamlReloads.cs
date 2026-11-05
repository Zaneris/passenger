using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xaml;
using System.Xaml.Permissions;
using System.Xml;
using Passenger.Core;

namespace Passenger.Inspector.Wpf;

/// <summary>XAML Hot Reload in a WPF app: a XAML file's text applied, on the UI thread, by WPF's own XAML loader to what the
/// app made from it: each open instance of its x:Class loaded again in place as its build's BAML was (the object, its
/// DataContext and its code's state kept, its handlers bound to its own methods), or for a resource dictionary without
/// one, each dictionary its build merged from the file swapped for one made from the text. What loading adds to
/// (resources, the name scope, triggers, input and command bindings, the root's handlers) is cleared of what the text
/// before wrote and put back if the text fails; what the app put there itself stays; a root property the edit did not
/// change keeps what the app holds in it; the x:Name fields point at the new elements; an instance made later takes the
/// newest text as it loads.</summary>
internal sealed class XamlReloads(Func<IReadOnlyList<Visual>> tops)
{
    /// <summary>The newest text applied of each x:Class, by its type, with the text its build made instances from.</summary>
    private readonly Dictionary<Type, (XamlChange Change, string? Built)> latest = [];

    /// <summary>The text each object made from an x:Class holds once it took one.</summary>
    private readonly ConditionalWeakTable<object, string> holds = new();

    public XamlReloaded Reload(XamlChange change) =>
        XamlReloading.Refusal(change, out XamlRoot now, out _) ?? (now.Class is { } named ? ReloadClass(change, named) : ReloadDictionary(change));

    /// <summary>An element of a reloaded x:Class loaded: made from its build's BAML, it takes the newest text applied.</summary>
    public void Loaded(object? element)
    {
        if (element is null || latest.Count == 0 || !latest.TryGetValue(element.GetType(), out (XamlChange Change, string? Built) newest)
            || (holds.TryGetValue(element, out string? held) && held == newest.Change.Text))
        {
            return;
        }
        try
        {
            Apply(element, newest.Change, newest.Built, []);
        }
        catch (Refusal refused)
        {
            InspectorTrace.Step($"{Words.TypeName(element.GetType())} loaded and could not take {newest.Change.File}: {refused.Message}");
        }
    }

    private XamlReloaded ReloadClass(XamlChange change, string named)
    {
        if (XamlReloading.Loaded(change.Assembly) is not { } assembly)
        {
            return new XamlReloaded([], null, []);
        }
        if (assembly.GetType(named) is not { } type)
        {
            return XamlReloading.Refused($"{Path.GetFileName(change.File)}'s x:Class {named} is not in the app's {change.Assembly}: restart the app to apply it", []);
        }
        string? built = latest.TryGetValue(type, out (XamlChange Change, string? Built) known) ? known.Built : change.Before;
        List<string> objects = [];
        List<XamlProblem> said = [];
        foreach (object made in Reachable().Where(o => o.GetType() == type).ToList())
        {
            try
            {
                Apply(made, change, holds.TryGetValue(made, out string? held) ? held : built, said);
            }
            catch (Refusal refused)
            {
                return new XamlReloaded(objects, refused.Message, said);
            }
            objects.Add(Words.Describe(type, (made as FrameworkElement)?.Name));
        }
        if (type.IsSubclassOf(typeof(Application)) && Application.Current?.GetType() == type && objects.Count == 0)
        {
            return new XamlReloaded(objects, null, said);
        }
        latest[type] = (change, built);
        return new XamlReloaded(objects, null, said);
    }

    /// <summary>A resource dictionary the build merged where its file is named: each swapped, where it is, for one made from
    /// the text.</summary>
    private XamlReloaded ReloadDictionary(XamlChange change)
    {
        List<(Collection<ResourceDictionary> In, int At)> found = [.. Merged().Where(m => m.In[m.At].Source is { } source && Sources.FileOf(Absolute(source, change)) is { } file
            && PathNames.Same(file, change.File))];
        List<string> objects = [];
        List<XamlProblem> said = [];
        foreach ((Collection<ResourceDictionary> merged, int at) in found)
        {
            object loaded;
            try
            {
                loaded = Load(change, null, XamlReloading.Loaded(change.Assembly));
            }
            catch (Exception error)
            {
                return new XamlReloaded(objects, Why(change, error, said, null), said);
            }
            if (loaded is not ResourceDictionary dictionary)
            {
                return XamlReloading.Refused($"{Path.GetFileName(change.File)} now makes {Words.TypeName(loaded.GetType())} where the app merges a ResourceDictionary: restart the app to apply it", said);
            }
            merged[at] = dictionary;
            objects.Add(nameof(ResourceDictionary));
        }
        return new XamlReloaded(objects, null, said);
    }

    /// <summary>A dictionary's source as the pack URI its file is named by.</summary>
    private static Uri Absolute(Uri source, XamlChange change) =>
        source.IsAbsoluteUri ? source : new Uri(new Uri($"pack://application:,,,/{change.Assembly};component/"), source.OriginalString.TrimStart('/'));

    /// <summary>Loads one object again from the text, its reset taken back if the text fails.</summary>
    private void Apply(object made, XamlChange change, string? before, List<XamlProblem> said)
    {
        XamlRoot now = XamlRoot.Read(change.Text);
        XamlRoot? was = XamlReloading.Readable(before);
        Held held = new(made, change.File);
        try
        {
            held.Reset(now, was);
            Load(change, made, made.GetType().Assembly);
        }
        catch (Exception error)
        {
            held.PutBack(now);
            throw new Refusal(Why(change, error, said, made.GetType()));
        }
        held.Keep(now, was);
        Named(made);
        holds.AddOrUpdate(made, change.Text);
    }

    /// <summary>The text read by WPF's own schema and loaded by WPF's own loader, into <paramref name="root"/> as its BAML
    /// is, with access to its class's own members; a loose dictionary by WPF's public reader.</summary>
    private static object Load(XamlChange change, object? root, Assembly? local)
    {
        XamlXmlReaderSettings settings = new() { LocalAssembly = local, ProvideLineInfo = true, BaseUri = new Uri(change.Uri) };
        using XmlReader xml = XmlReader.Create(new StringReader(change.Text));
        using XamlXmlReader reader = new(xml, System.Windows.Markup.XamlReader.GetWpfSchemaContext(), settings);
        if (root is null)
        {
            return System.Windows.Markup.XamlReader.Load(reader);
        }
        MethodInfo load = typeof(System.Windows.Markup.XamlReader).Assembly.GetType("System.Windows.Markup.WpfXamlLoader")?.GetMethod("LoadBaml",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(System.Xaml.XamlReader), typeof(bool), typeof(object), typeof(XamlAccessLevel), typeof(Uri)])
            ?? throw new Refusal("This WPF no longer loads XAML into an object as its own loader did (WpfXamlLoader.LoadBaml): restart the app to apply it");
        try
        {
            return load.Invoke(null, [reader, false, root, XamlAccessLevel.PrivateAccessTo(root.GetType()), new Uri(change.Uri)])!;
        }
        catch (TargetInvocationException thrown) when (thrown.InnerException is { } inner)
        {
            throw new XamlLoadFailed(inner);
        }
    }

    /// <summary>Why a text could not apply, in the person's words, with the place WPF's reader named.</summary>
    private static string Why(XamlChange change, Exception error, List<XamlProblem> said, Type? root)
    {
        Exception cause = error is XamlLoadFailed failed ? failed.InnerException! : error;
        (int? line, int? column) = cause switch
        {
            System.Windows.Markup.XamlParseException parse => ((int?)parse.LineNumber, (int?)parse.LinePosition),
            System.Xaml.XamlException xaml => (xaml.LineNumber, xaml.LinePosition),
            XmlException xml => (xml.LineNumber, xml.LinePosition),
            _ => (null, null),
        };
        (line, column) = line is > 0 ? (line, column) : (null, null);
        string message = XamlReloading.MissingHandler(change.Text, line, column, root) ?? Unplaced(cause.Message.Split('\n')[0]);
        said.Add(new XamlProblem(message, line, column, true));
        return $"{Path.GetFileName(change.File)} could not apply: {message}";
    }

    /// <summary>WPF's words without the place they end with ("Line number '6' and line position '14'."), which the problem carries.</summary>
    private static string Unplaced(string message)
    {
        int place = message.IndexOf("Line number '", StringComparison.Ordinal);
        return place > 0 ? message[..place].TrimEnd(' ', '\'', '.', ',') : XamlReloading.Unplaced(message);
    }

    /// <summary>Every object the app holds that a XAML file's root can make: its Application, its windows, and each top
    /// level's visual and logical descendants, outer before inner.</summary>
    private IEnumerable<object> Reachable()
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        if (Application.Current is { } app && seen.Add(app))
        {
            yield return app;
        }
        List<DependencyObject> pending = [.. tops()];
        if (Application.Current is { } owner)
        {
            pending.AddRange(owner.Windows.OfType<Window>());
        }
        while (pending.Count > 0)
        {
            DependencyObject next = pending[0];
            pending.RemoveAt(0);
            if (!seen.Add(next))
            {
                continue;
            }
            yield return next;
            if (next is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(next); i++)
                {
                    pending.Add(VisualTreeHelper.GetChild(next, i));
                }
            }
            pending.AddRange(LogicalTreeHelper.GetChildren(next).OfType<DependencyObject>());
        }
    }

    /// <summary>The merged dictionaries the app holds, the Application's and each element's, each by its place in its list.</summary>
    private IEnumerable<(Collection<ResourceDictionary> In, int At)> Merged()
    {
        HashSet<ResourceDictionary> seen = new(ReferenceEqualityComparer.Instance);
        List<ResourceDictionary> owners = [];
        if (Application.Current is { } app)
        {
            owners.Add(app.Resources);
        }
        owners.AddRange(Reachable().OfType<FrameworkElement>().Select(e => e.Resources));
        while (owners.Count > 0)
        {
            ResourceDictionary owner = owners[0];
            owners.RemoveAt(0);
            if (!seen.Add(owner))
            {
                continue;
            }
            for (int i = 0; i < owner.MergedDictionaries.Count; i++)
            {
                yield return (owner.MergedDictionaries, i);
                owners.Add(owner.MergedDictionaries[i]);
            }
        }
    }

    /// <summary>Points the markup compiler's x:Name fields of an object (its class's internal fields, which the compiler's
    /// IComponentConnector sets) at the elements its name scope now has by their names.</summary>
    private static void Named(object made)
    {
        if (made is not (DependencyObject root and IComponentConnector) || NameScope.GetNameScope(root) is not { } scope)
        {
            return;
        }
        foreach (FieldInfo field in made.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (field.IsAssembly && !field.IsInitOnly && scope.FindName(field.Name) is { } element && field.FieldType.IsInstanceOfType(element))
            {
                field.SetValue(made, element);
            }
        }
    }

    /// <summary>What loading an object again adds to, taken away first and kept to put back: its resources, name scope,
    /// triggers, input and command bindings, its root's handlers, and its own local values. What of these the text before
    /// did not write is the app's own, kept through a load that succeeds.</summary>
    private sealed class Held
    {
        private readonly object made;
        private readonly string file;
        private readonly ResourceDictionary? resources;
        private readonly INameScope? scope;
        private readonly List<TriggerBase> triggers;
        private readonly List<System.Windows.Input.InputBinding> inputs;
        private readonly List<System.Windows.Input.CommandBinding> commands;
        private readonly Dictionary<DependencyProperty, object?> locals;
        private readonly List<(EventInfo Event, Delegate Handler)> unhooked = [];

        /// <summary>The app's own resources, lent to the dictionary a text fills so what it looks up as it loads finds them.</summary>
        private readonly ResourceDictionary lent = new();
        private (List<TriggerBase> First, List<TriggerBase> Last) ownTriggers = ([], []);
        private (List<System.Windows.Input.InputBinding> First, List<System.Windows.Input.InputBinding> Last) ownInputs = ([], []);
        private (List<System.Windows.Input.CommandBinding> First, List<System.Windows.Input.CommandBinding> Last) ownCommands = ([], []);
        private (List<ResourceDictionary> First, List<ResourceDictionary> Last) ownMerged = ([], []);

        public Held(object made, string file)
        {
            this.made = made;
            this.file = file;
            resources = ResourcesOf(made);
            scope = made is DependencyObject named ? NameScope.GetNameScope(named) : null;
            triggers = made is FrameworkElement { Triggers: { } own } ? [.. own] : [];
            inputs = made is UIElement { InputBindings: { } keys } ? [.. keys.OfType<System.Windows.Input.InputBinding>()] : [];
            commands = made is UIElement { CommandBindings: { } bound } ? [.. bound.OfType<System.Windows.Input.CommandBinding>()] : [];
            locals = Locals(made);
        }

        private static ResourceDictionary? ResourcesOf(object made) => made switch
        {
            Application app => app.Resources,
            FrameworkElement element => element.Resources,
            FrameworkContentElement content => content.Resources,
            _ => null,
        };

        /// <summary>Clears what loading adds to, and what the text before set on the root that the text now does not.</summary>
        public void Reset(XamlRoot now, XamlRoot? was)
        {
            ownTriggers = XamlRoot.Own(triggers, was?.Items.GetValueOrDefault("Triggers"), Origin);
            ownInputs = XamlRoot.Own(inputs, was?.Items.GetValueOrDefault("InputBindings"), Origin);
            ownCommands = XamlRoot.Own(commands, was?.Items.GetValueOrDefault("CommandBindings"), Origin);
            switch (made)
            {
                case Application app:
                    app.Resources = Fresh(now, was);
                    break;
                case FrameworkElement element:
                    element.Resources = Fresh(now, was);
                    element.Triggers.Clear();
                    break;
                case FrameworkContentElement content:
                    content.Resources = Fresh(now, was);
                    break;
            }
            if (made is UIElement input)
            {
                input.InputBindings.Clear();
                input.CommandBindings.Clear();
            }
            if (made is DependencyObject scoped)
            {
                NameScope.SetNameScope(scoped, new NameScope());
            }
            if (was is null)
            {
                return;
            }
            foreach ((EventInfo handled, Delegate handler) in Handlers(was))
            {
                handled.RemoveEventHandler(made, handler);
                unhooked.Add((handled, handler));
            }
            foreach (string dropped in was.Dropped(now))
            {
                if (made is DependencyObject root && Property(root, dropped) is { ReadOnly: false } property)
                {
                    root.ClearValue(property);
                }
            }
        }

        /// <summary>The dictionary a text fills, holding meanwhile what the app put in the one it replaces, which stays as it is
        /// until the text applied. An entry neither text writes is the app's.</summary>
        private ResourceDictionary Fresh(XamlRoot now, XamlRoot? was)
        {
            ResourceDictionary fresh = new();
            if (resources is null)
            {
                return fresh;
            }
            foreach (object key in resources.Keys.Cast<object>().Where(k => was?.Resources.Writes(Written(k)) != true && !now.Resources.Writes(Written(k))).ToList())
            {
                lent[key] = resources[key];
            }
            ownMerged = XamlRoot.Own([.. resources.MergedDictionaries], was?.Resources.Merged, Origin);
            fresh.MergedDictionaries.Add(lent);
            foreach (ResourceDictionary merged in ownMerged.First.Concat(ownMerged.Last))
            {
                fresh.MergedDictionaries.Add(merged);
            }
            return fresh;
        }

        /// <summary>A resource's key as a text writes it: a template's by the type it is for.</summary>
        private static object Written(object key) => key is DataTemplateKey { DataType: Type type } ? type : key;

        /// <summary>The text failed: the object as it was.</summary>
        public void PutBack(XamlRoot now)
        {
            foreach ((EventInfo handled, Delegate handler) in Handlers(now))
            {
                handled.RemoveEventHandler(made, handler);
            }
            switch (made)
            {
                case Application app when resources is not null:
                    app.Resources = resources;
                    break;
                case FrameworkElement element:
                    if (resources is not null)
                    {
                        element.Resources = resources;
                    }
                    element.Triggers.Clear();
                    foreach (TriggerBase trigger in triggers)
                    {
                        element.Triggers.Add(trigger);
                    }
                    break;
                case FrameworkContentElement content when resources is not null:
                    content.Resources = resources;
                    break;
            }
            if (made is UIElement input)
            {
                input.InputBindings.Clear();
                input.InputBindings.AddRange(inputs);
                input.CommandBindings.Clear();
                input.CommandBindings.AddRange(commands);
            }
            if (made is DependencyObject root)
            {
                if (scope is null)
                {
                    root.ClearValue(NameScope.NameScopeProperty);
                }
                else
                {
                    NameScope.SetNameScope(root, scope);
                }
                foreach (DependencyProperty property in Locals(root).Keys.Where(p => !locals.ContainsKey(p)))
                {
                    root.ClearValue(property);
                }
            }
            foreach ((DependencyProperty property, object? value) in locals)
            {
                Restore(property, value);
            }
            foreach ((EventInfo handled, Delegate handler) in unhooked)
            {
                handled.AddEventHandler(made, handler);
            }
        }

        /// <summary>The text applied: what the app put in the root's collections goes back where it stood, and a root property
        /// the text sets as the text before did keeps what the app held in it, unless that is part of the tree (an element,
        /// which the text made again).</summary>
        public void Keep(XamlRoot now, XamlRoot? was)
        {
            if (made is FrameworkElement element)
            {
                Around(element.Triggers, ownTriggers);
            }
            if (made is UIElement input)
            {
                Around(input.InputBindings, ownInputs);
                Around(input.CommandBindings, ownCommands);
            }
            KeepResources();
            if (was is null)
            {
                return;
            }
            foreach ((DependencyProperty property, object? value) in locals)
            {
                if (value is not (Visual or FrameworkContentElement or BindingExpressionBase) && was.Keeps(now, property.Name))
                {
                    Restore(property, value);
                }
            }
        }

        /// <summary>The app's own resources in the dictionary the text left, which may be another than it filled (one the text
        /// writes itself), a key the text now writes its own.</summary>
        private void KeepResources()
        {
            if (ResourcesOf(made) is not { } current)
            {
                return;
            }
            current.MergedDictionaries.Remove(lent);
            List<object> written = [.. current.Keys.Cast<object>()];
            foreach (object key in lent.Keys.Cast<object>().Where(k => !written.Contains(k)).ToList())
            {
                current.Add(key, lent[key]);
            }
            Around((System.Collections.IList)current.MergedDictionaries, ownMerged);
        }

        /// <summary>A list as a text left it, with the app's own back before and after what the text made.</summary>
        private static void Around<T>(System.Collections.IList list, (List<T> First, List<T> Last) own)
        {
            foreach (T item in own.First.Concat(own.Last))
            {
                list.Remove(item);
            }
            for (int i = 0; i < own.First.Count; i++)
            {
                list.Insert(i, own.First[i]);
            }
            foreach (T item in own.Last)
            {
                list.Add(item);
            }
        }

        private XamlOrigin Origin(object item) => Sources.Origin(item, file);

        /// <summary>The event handlers a text names on the root, as its XAML makes them: equal to them, so they can be taken off.</summary>
        private IEnumerable<(EventInfo Event, Delegate Handler)> Handlers(XamlRoot text)
        {
            foreach ((string name, string value) in text.Attributes)
            {
                if (made.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance) is { EventHandlerType: { } kind } handled
                    && Handler(kind, value) is { } handler)
                {
                    yield return (handled, handler);
                }
            }
        }

        private void Restore(DependencyProperty property, object? value)
        {
            if (made is not DependencyObject root)
            {
                return;
            }
            if (value is BindingExpressionBase bound)
            {
                if (BindingOperations.GetBindingExpressionBase(root, property) != bound)
                {
                    BindingOperations.SetBinding(root, property, bound.ParentBindingBase);
                }
                return;
            }
            if (!Equals(root.ReadLocalValue(property), value))
            {
                root.SetValue(property, value);
            }
        }

        private static Dictionary<DependencyProperty, object?> Locals(object made)
        {
            Dictionary<DependencyProperty, object?> values = [];
            if (made is not DependencyObject root)
            {
                return values;
            }
            LocalValueEnumerator local = root.GetLocalValueEnumerator();
            while (local.MoveNext())
            {
                if (!local.Current.Property.ReadOnly && local.Current.Property != NameScope.NameScopeProperty)
                {
                    values[local.Current.Property] = local.Current.Value;
                }
            }
            return values;
        }

        /// <summary>A root's dependency property by the name its XAML gives it.</summary>
        private static DependencyProperty? Property(DependencyObject made, string name) =>
            name.Contains('.', StringComparison.Ordinal) ? null : DependencyPropertyDescriptor.FromName(name, made.GetType(), made.GetType())?.DependencyProperty;

        /// <summary>The handler an attribute names on the object, as its XAML made it: equal to it, so it can be taken off.</summary>
        private Delegate? Handler(Type kind, string method)
        {
            foreach (MethodInfo candidate in made.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == method))
            {
                if (Delegate.CreateDelegate(kind, made, candidate, throwOnBindFailure: false) is { } handler)
                {
                    return handler;
                }
            }
            return null;
        }
    }

    /// <summary>What WPF's loader threw from inside its reflection call, kept apart from the call's own failure.</summary>
    private sealed class XamlLoadFailed(Exception inner) : Exception(inner.Message, inner);
}
