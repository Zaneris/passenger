using System.CodeDom.Compiler;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>XAML Hot Reload in an Avalonia app: a XAML file's text applied, on the UI thread, by Avalonia's runtime XAML
/// loader to what the app made from it: each open instance of its x:Class populated again in place (the object, its
/// DataContext and its code's state kept), or for a file of styles or resources without one, the object its build made
/// from it swapped for one made from the text. What populating adds to (styles, resources, templates, key bindings,
/// classes, the root's event handlers) is cleared of what the text before wrote and put back if the text fails, so the app
/// stays as it was; what the app put there itself stays. A root property the edit did not change keeps what the app holds
/// in it now; the name generator's x:Name fields point at the new controls; an instance made later takes the newest text as
/// it loads.</summary>
internal sealed class XamlReloads(Func<IReadOnlyList<TopLevel>> tops) : XamlReloader
{
    /// <summary>A file of styles or resources the build included where it is named: each object its build made from it (its
    /// source info is the file's) swapped, where it is, for one made from the text.</summary>
    protected override XamlReloaded ReloadIncluded(XamlChange change)
    {
        if (XamlReloading.Loaded(change.Assembly) is not { } assembly)
        {
            return new XamlReloaded([], null, []);
        }
        List<(object Made, Action<object> Swap)> found = [.. Providers().Where(p => From(p.Made, change.File))];
        List<string> objects = [];
        List<XamlProblem> said = [];
        foreach ((object made, Action<object> swap) in found)
        {
            object loaded;
            try
            {
                loaded = Load(change, null, assembly, said);
            }
            catch (Exception error)
            {
                return new XamlReloaded(objects, Why(change, error, said, null), said);
            }
            if (loaded.GetType() != made.GetType() && !(made is IStyle && loaded is IStyle) && !(made is IResourceProvider && loaded is IResourceProvider))
            {
                return XamlReloading.Refused($"{Path.GetFileName(change.File)} now makes {Words.TypeName(loaded.GetType())} where the app holds {Words.TypeName(made.GetType())}: restart the app to apply it", said);
            }
            swap(loaded);
            objects.Add(Words.TypeName(made.GetType()));
        }
        return new XamlReloaded(objects, null, said);
    }

    protected override string? Unreloadable(Type type) =>
        typeof(AvaloniaObject).IsAssignableFrom(type) ? null : "is no Avalonia object, which XAML Hot Reload makes again";

    protected override string Called(object made) => Words.Describe(made.GetType(), (made as StyledElement)?.Name);

    protected override IHeldRoot Hold(object made, string file) => new Held((AvaloniaObject)made, file);

    protected override void Load(XamlChange change, object made, List<XamlProblem> said) => Load(change, made, made.GetType().Assembly, said);

    private static object Load(XamlChange change, object? root, Assembly local, List<XamlProblem> said)
    {
        RuntimeXamlLoaderDocument document = new(new Uri(change.Uri), root, change.Text) { Document = change.File };
        RuntimeXamlLoaderConfiguration configuration = new()
        {
            LocalAssembly = local,
            CreateSourceInfo = true,
            UseCompiledBindingsByDefault = change.CompiledBindings == true,
            DiagnosticHandler = diagnostic =>
            {
                XamlProblem problem = new(XamlReloading.Unplaced(diagnostic.Title), diagnostic.LineNumber, diagnostic.LinePosition,
                    diagnostic.Severity >= RuntimeXamlDiagnosticSeverity.Error);
                // What follows from a type it could not resolve, said at that type's own place, adds nothing.
                bool follows = problem.Message.Contains("{Unknown type}", StringComparison.Ordinal)
                    && said.Any(p => p.Error && p.Line == problem.Line && p.Column == problem.Column);
                if (diagnostic.Severity >= RuntimeXamlDiagnosticSeverity.Warning && !follows && !said.Contains(problem))
                {
                    said.Add(problem);
                }
                return diagnostic.Severity;
            },
        };
        return AvaloniaRuntimeXamlLoader.Load(document, configuration);
    }

    /// <summary>Why a text could not apply, in the person's words: the compiler's first error (a handler the class lacks said
    /// as such), else what the load threw, its place left to the problem it adds.</summary>
    protected override string Why(XamlChange change, Exception error, List<XamlProblem> said, Type? root)
    {
        string file = Path.GetFileName(change.File);
        if (said.FirstOrDefault(p => p.Error) is not { } first)
        {
            Exception cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            string message = XamlReloading.Unplaced(cause.Message.Split('\n')[0]);
            System.Xml.XmlException? xml = cause as System.Xml.XmlException;
            said.Add(new XamlProblem(message, xml?.LineNumber, xml?.LinePosition, true));
            return $"{file} could not apply: {message}";
        }
        if (XamlReloading.MissingHandler(change.Text, first.Line, first.Column, root) is { } handler)
        {
            said[said.IndexOf(first)] = first with { Message = handler };
            return $"{file} could not apply: {handler}";
        }
        return $"{file} could not apply: {first.Message}";
    }

    /// <summary>Whether the build made an object from this file, by the source info it gave it.</summary>
    private static bool From(object made, string file) =>
        XamlSourceInfo.GetXamlSourceInfo(made) is { SourceUri: { } uri } && PathNames.Same(uri.IsFile ? uri.LocalPath : uri.ToString(), file);

    /// <summary>Every object the app holds that a XAML file's root can make: its Application, each top level's visuals and
    /// logical children, and the styles and resources each of them holds, outer before inner.</summary>
    protected override IEnumerable<object> Reachable()
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        if (Application.Current is { } app && seen.Add(app))
        {
            yield return app;
        }
        foreach (TopLevel top in tops())
        {
            foreach (object made in new object[] { top }.Concat(Inspector.Below(top)).Concat(ElementTrees.Below<ILogical>(top, static l => l.LogicalChildren)))
            {
                if (seen.Add(made))
                {
                    yield return made;
                }
            }
        }
        foreach ((object made, _) in Providers())
        {
            if (seen.Add(made))
            {
                yield return made;
            }
        }
    }

    /// <summary>The styles and resource dictionaries the app holds, each with how to put another in its place, outer
    /// before inner: the Application's, then each element's.</summary>
    private IEnumerable<(object Made, Action<object> Swap)> Providers()
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        List<(IList<IStyle> Styles, IResourceDictionary Resources)> owners = [];
        if (Application.Current is { } app)
        {
            owners.Add((app.Styles, app.Resources));
        }
        foreach (TopLevel top in tops())
        {
            foreach (StyledElement element in Inspector.Below(top).Prepend(top).OfType<StyledElement>())
            {
                owners.Add((element.Styles, element.Resources));
            }
        }
        foreach ((IList<IStyle> styles, IResourceDictionary resources) in owners)
        {
            foreach ((object Made, Action<object> Swap) provider in Styles(styles, seen).Concat(Resources(resources, seen)))
            {
                yield return provider;
            }
        }
    }

    private static IEnumerable<(object Made, Action<object> Swap)> Styles(IList<IStyle> styles, HashSet<object> seen)
    {
        if (!seen.Add(styles))
        {
            yield break;
        }
        for (int i = 0; i < styles.Count; i++)
        {
            IStyle style = styles[i];
            int at = i;
            yield return (style, swapped => styles[at] = (IStyle)swapped);
            IEnumerable<(object, Action<object>)> inner = style switch
            {
                Styles nested => Styles(nested, seen).Concat(Resources(nested.Resources, seen)),
                StyleBase single => Resources(single.Resources, seen),
                _ => [],
            };
            foreach ((object, Action<object>) found in inner)
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<(object Made, Action<object> Swap)> Resources(IResourceDictionary resources, HashSet<object> seen)
    {
        if (!seen.Add(resources))
        {
            yield break;
        }
        IList<IResourceProvider> merged = resources.MergedDictionaries;
        for (int i = 0; i < merged.Count; i++)
        {
            IResourceProvider provider = merged[i];
            int at = i;
            yield return (provider, swapped => merged[at] = (IResourceProvider)swapped);
            if (provider is IResourceDictionary inner)
            {
                foreach ((object, Action<object>) found in Resources(inner, seen))
                {
                    yield return found;
                }
            }
        }
        foreach ((ThemeVariant variant, IThemeVariantProvider theme) in resources.ThemeDictionaries.ToList())
        {
            yield return (theme, swapped => resources.ThemeDictionaries[variant] = (IThemeVariantProvider)swapped);
        }
    }

    /// <summary>Points the name generator's x:Name fields of an object at the controls its name scope now has by their names.</summary>
    protected override void Named(object made)
    {
        if (made is not StyledElement element || NameScope.GetNameScope(element) is not { } scope)
        {
            return;
        }
        foreach (FieldInfo field in made.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (field.GetCustomAttribute<GeneratedCodeAttribute>() is { Tool: { } tool } && tool.StartsWith("Avalonia.Generators", StringComparison.Ordinal)
                && scope.Find(field.Name) is { } control && field.FieldType.IsInstanceOfType(control))
            {
                field.SetValue(made, control);
            }
        }
    }

    /// <summary>What populating an object again adds to, taken away first and kept to put back: its styles, resources,
    /// data templates, key bindings and classes, its root's handlers, and its own local values. What of these the text
    /// before did not write is the app's own, kept through a load that succeeds.</summary>
    private sealed class Held : IHeldRoot
    {
        private readonly AvaloniaObject made;
        private readonly string file;
        private readonly IList<IStyle>? styles;
        private readonly List<IStyle> heldStyles;
        private readonly IResourceDictionary? resources;
        private readonly IList<IDataTemplate>? templates;
        private readonly List<IDataTemplate> heldTemplates;
        private readonly List<KeyBinding>? keys;
        private readonly List<KeyBinding> heldKeys;
        private readonly List<string> heldClasses;
        private readonly Dictionary<AvaloniaProperty, object?> locals;
        private readonly List<(EventInfo Event, Delegate Handler)> unhooked = [];

        /// <summary>The app's own resources, lent to the dictionary a text fills so what it looks up as it loads finds them.</summary>
        private readonly ResourceDictionary lent = new();
        private (List<IStyle> First, List<IStyle> Last) ownStyles = ([], []);
        private (List<IDataTemplate> First, List<IDataTemplate> Last) ownTemplates = ([], []);
        private (List<KeyBinding> First, List<KeyBinding> Last) ownKeys = ([], []);
        private (List<IResourceProvider> First, List<IResourceProvider> Last) ownMerged = ([], []);
        private List<KeyValuePair<ThemeVariant, IThemeVariantProvider>> ownThemes = [];
        private List<string> ownClasses = [];

        public Held(AvaloniaObject made, string file)
        {
            this.made = made;
            this.file = file;
            (styles, resources, templates) = made switch
            {
                Application app => ((IList<IStyle>?)app.Styles, (IResourceDictionary?)app.Resources, (IList<IDataTemplate>?)app.DataTemplates),
                Control control => (control.Styles, control.Resources, control.DataTemplates),
                StyledElement element => (element.Styles, element.Resources, null),
                _ => (null, null, null),
            };
            keys = (made as InputElement)?.KeyBindings;
            heldStyles = styles is null ? [] : [.. styles];
            heldTemplates = templates is null ? [] : [.. templates];
            heldKeys = keys is null ? [] : [.. keys];
            heldClasses = made is StyledElement styled ? [.. styled.Classes.Where(c => !c.StartsWith(':'))] : [];
            locals = Locals(made);
        }

        /// <summary>Clears what populating adds to, and what the text before set on the root that the text now does not.</summary>
        public void Reset(XamlRoot now, XamlRoot? was)
        {
            ownStyles = XamlRoot.Own(heldStyles, was?.Items.GetValueOrDefault("Styles"), Origin);
            ownTemplates = XamlRoot.Own(heldTemplates, was?.Items.GetValueOrDefault("DataTemplates"), Origin);
            ownKeys = XamlRoot.Own(heldKeys, was?.Items.GetValueOrDefault("KeyBindings"), Origin);
            styles?.Clear();
            templates?.Clear();
            keys?.Clear();
            switch (made)
            {
                case Application app:
                    app.Resources = Fresh(now, was);
                    break;
                case StyledElement element:
                    element.Resources = Fresh(now, was);
                    List<string> written = [.. heldClasses.Where(c => was?.Classes.Contains(c) == true)];
                    ownClasses = [.. heldClasses.Except(written)];
                    element.Classes.RemoveAll(written);
                    break;
            }
            if (was is null)
            {
                return;
            }
            foreach ((EventInfo handled, Delegate handler) in XamlReloading.Handlers(made, was))
            {
                handled.RemoveEventHandler(made, handler);
                unhooked.Add((handled, handler));
            }
            foreach (string dropped in was.Dropped(now))
            {
                if (Property(made, dropped) is { IsReadOnly: false } property)
                {
                    made.ClearValue(property);
                }
            }
        }

        /// <summary>The dictionary a text fills, holding meanwhile what the app put in the one it replaces, which stays as it is
        /// until the text applied. An entry neither text writes is the app's; Avalonia records the place of each one XAML made.</summary>
        private ResourceDictionary Fresh(XamlRoot now, XamlRoot? was)
        {
            ResourceDictionary fresh = new();
            if (resources is null)
            {
                return fresh;
            }
            foreach (object key in resources.Keys.Where(k => XamlSourceInfo.GetXamlSourceInfo(resources, k) is null && was?.Resources.Writes(k) != true && !now.Resources.Writes(k)).ToList())
            {
                lent[key] = resources[key];
            }
            ownMerged = XamlRoot.Own([.. resources.MergedDictionaries], was?.Resources.Merged, Origin);
            ownThemes = [.. resources.ThemeDictionaries.Where(t => XamlSourceInfo.GetXamlSourceInfo(t.Value) is null
                && was?.Resources.Themes.Contains(t.Key.ToString()) != true && !now.Resources.Themes.Contains(t.Key.ToString()))];
            fresh.MergedDictionaries.Add(lent);
            foreach (IResourceProvider merged in ownMerged.First.Concat(ownMerged.Last))
            {
                fresh.MergedDictionaries.Add(merged);
            }
            return fresh;
        }

        /// <summary>The text failed: the object as it was.</summary>
        public void PutBack(XamlRoot now)
        {
            foreach ((EventInfo handled, Delegate handler) in XamlReloading.Handlers(made, now))
            {
                handled.RemoveEventHandler(made, handler);
            }
            if (styles is not null)
            {
                styles.Clear();
                foreach (IStyle style in heldStyles)
                {
                    styles.Add(style);
                }
            }
            if (templates is not null)
            {
                templates.Clear();
                foreach (IDataTemplate template in heldTemplates)
                {
                    templates.Add(template);
                }
            }
            if (keys is not null)
            {
                keys.Clear();
                keys.AddRange(heldKeys);
            }
            switch (made)
            {
                case Application app when resources is not null:
                    app.Resources = resources;
                    break;
                case StyledElement element:
                    if (resources is not null)
                    {
                        element.Resources = resources;
                    }
                    element.Classes.RemoveAll([.. element.Classes.Where(c => !c.StartsWith(':'))]);
                    element.Classes.AddRange(heldClasses);
                    break;
            }
            foreach ((AvaloniaProperty property, object? _) in Locals(made).Where(l => !locals.ContainsKey(l.Key)))
            {
                made.ClearValue(property);
            }
            foreach ((AvaloniaProperty property, object? value) in locals.Where(l => !Equals(made.GetValue(l.Key), l.Value)))
            {
                made.SetValue(property, value);
            }
            foreach ((EventInfo handled, Delegate handler) in unhooked)
            {
                handled.AddEventHandler(made, handler);
            }
        }

        /// <summary>The text applied: what the app put in the root's collections goes back where it stood, and a root property
        /// the text sets as the text before did keeps what the app held in it, unless that is part of the tree (a control,
        /// which the text made again).</summary>
        public void Keep(XamlRoot now, XamlRoot? was)
        {
            XamlReloading.Around(styles, ownStyles);
            XamlReloading.Around(templates, ownTemplates);
            XamlReloading.Around(keys, ownKeys);
            KeepResources();
            (made as StyledElement)?.Classes.AddRange(ownClasses);
            if (was is null)
            {
                return;
            }
            foreach ((AvaloniaProperty property, object? value) in locals)
            {
                if (value is not (Visual or ILogical or IStyle) && was.Keeps(now, NameOf(property)) && !Equals(made.GetValue(property), value))
                {
                    made.SetValue(property, value);
                }
            }
        }

        /// <summary>The app's own resources in the dictionary the text left, which may be another than it filled (one the text
        /// writes itself), a key the text now writes its own.</summary>
        private void KeepResources()
        {
            IResourceDictionary? held = made switch
            {
                Application app => app.Resources,
                StyledElement element => element.Resources,
                _ => null,
            };
            if (held is not { } current)
            {
                return;
            }
            current.MergedDictionaries.Remove(lent);
            foreach (object key in lent.Keys.Where(k => !current.ContainsKey(k)).ToList())
            {
                current.Add(key, lent[key]);
            }
            XamlReloading.Around(current.MergedDictionaries, ownMerged);
            foreach ((ThemeVariant variant, IThemeVariantProvider theme) in ownThemes)
            {
                current.ThemeDictionaries.TryAdd(variant, theme);
            }
        }

        private XamlOrigin Origin(object item) =>
            XamlSourceInfo.GetXamlSourceInfo(item) is null ? XamlOrigin.Unrecorded : From(item, file) ? XamlOrigin.ThisFile : XamlOrigin.AnotherFile;

        private static Dictionary<AvaloniaProperty, object?> Locals(AvaloniaObject made)
        {
            Dictionary<AvaloniaProperty, object?> values = [];
            Type type = made.GetType();
            foreach (AvaloniaProperty property in AvaloniaPropertyRegistry.Instance.GetRegistered(type).Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(type)))
            {
                if (!property.IsDirect && !property.IsReadOnly && property.OwnerType != typeof(XamlSourceInfo)
                    && made.GetDiagnostic(property) is { Priority: BindingPriority.LocalValue } local)
                {
                    values.TryAdd(property, local.Value);
                }
            }
            return values;
        }

        private static string NameOf(AvaloniaProperty property) => Words.PropertyName(property.OwnerType, property.Name, property.IsAttached);

        /// <summary>A root's property by the name its XAML gives it (<see cref="NameOf"/>).</summary>
        private static AvaloniaProperty? Property(AvaloniaObject made, string name) =>
            name.Contains('.', StringComparison.Ordinal)
                ? AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(made.GetType()).FirstOrDefault(p => NameOf(p) == name)
                : AvaloniaPropertyRegistry.Instance.FindRegistered(made, name);
    }
}
