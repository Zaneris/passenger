using System.Reflection;
using System.Runtime.CompilerServices;

namespace Passenger.Inspector;

/// <summary>What XAML Hot Reload does alike in either framework's app: a change's two texts read and compared, a change
/// only a build makes (another x:Class, another root element) refused in words, the compiler's words and the app's
/// assemblies as both read them, and a root's lists and handlers as both hold them (<see cref="XamlReloader"/>).</summary>
internal static class XamlReloading
{
    /// <summary>Why a change cannot apply before anything is made of it, or null, with its texts read.</summary>
    public static XamlReloaded? Refusal(XamlChange change, out XamlRoot now, out XamlRoot? before)
    {
        string file = Path.GetFileName(change.File);
        before = Readable(change.Before);
        try
        {
            now = XamlRoot.Read(change.Text);
        }
        catch (UnreadXaml unread)
        {
            now = new XamlRoot(string.Empty, null, new Dictionary<string, string>(), new Dictionary<string, string>());
            return Refused($"{file} does not read as XML: {unread.Message}", [new XamlProblem(unread.Message, unread.Line, unread.Column, true)]);
        }
        if (before is not null && before.Class != now.Class)
        {
            return Refused($"{file}'s x:Class changed from {before.Class ?? "none"} to {now.Class ?? "none"}, which only a build makes: restart the app to apply it", []);
        }
        if (before is not null && before.Type != now.Type)
        {
            return Refused($"{file}'s root element changed from {before.Type} to {now.Type}, which only a build makes: restart the app to apply it", []);
        }
        return null;
    }

    public static XamlReloaded Refused(string why, IReadOnlyList<XamlProblem> problems) => new([], why, problems);

    /// <summary>A text read, or null when there is none or it does not read.</summary>
    public static XamlRoot? Readable(string? text)
    {
        if (text is null)
        {
            return null;
        }
        try
        {
            return XamlRoot.Read(text);
        }
        catch (UnreadXaml)
        {
            return null;
        }
    }

    /// <summary>A compiler's words without the place they end with (" Line 12, position 6."), which the problem carries.</summary>
    public static string Unplaced(string message)
    {
        int place = message.LastIndexOf(" Line ", StringComparison.Ordinal);
        return place > 0 && message.IndexOf(", position ", place, StringComparison.Ordinal) > place ? message[..place].TrimEnd(' ', '.') : message.TrimEnd();
    }

    /// <summary>The app's assembly of that name, or null while it has not loaded it (nothing made from it is open).</summary>
    public static Assembly? Loaded(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name);

    /// <summary>Why a text could not apply when the place its compiler named is an attribute naming, as a handler does, a
    /// method its class does not have, in the person's words; else null.</summary>
    public static string? MissingHandler(string text, int? line, int? column, Type? root)
    {
        if (root is null || line is not int at || column is not int position || XamlRoot.AttributeAt(text, at, position) is not (string name, string value)
            || value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_') || !value.All(c => char.IsLetterOrDigit(c) || c == '_')
            || root.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(m => m.Name == value))
        {
            return null;
        }
        return $"{name}=\"{value}\" names a handler {root.Name} does not have: add {value} in C# and apply both changes";
    }

    /// <summary>A list as a text left it, with the app's own back before and after what the text made, through the list's own
    /// remove, insert and add.</summary>
    public static void Around<T>((List<T> First, List<T> Last) own, Action<T> remove, Action<int, T> insert, Action<T> add)
    {
        foreach (T item in own.First.Concat(own.Last))
        {
            remove(item);
        }
        for (int i = 0; i < own.First.Count; i++)
        {
            insert(i, own.First[i]);
        }
        foreach (T item in own.Last)
        {
            add(item);
        }
    }

    /// <summary>A list as a text left it, with the app's own back before and after what the text made.</summary>
    public static void Around<T>(IList<T>? list, (List<T> First, List<T> Last) own)
    {
        if (list is not null)
        {
            Around(own, item => list.Remove(item), list.Insert, list.Add);
        }
    }

    /// <summary>The event handlers a text names on a root, as its XAML makes them: equal to them, so they can be taken off.</summary>
    public static IEnumerable<(EventInfo Event, Delegate Handler)> Handlers(object made, XamlRoot text)
    {
        foreach ((string name, string value) in text.Attributes)
        {
            if (made.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance) is { EventHandlerType: { } kind } handled
                && Handler(made, kind, value) is { } handler)
            {
                yield return (handled, handler);
            }
        }
    }

    /// <summary>The handler an attribute names on the object, as its XAML made it: equal to it, so it can be taken off.</summary>
    private static Delegate? Handler(object made, Type kind, string method)
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

/// <summary>XAML Hot Reload of a file with an x:Class as either framework does it: each open instance of the class loaded
/// again from the text in place, what loading adds to held first and put back whole if the text fails, the text each
/// instance then holds kept, and the newest text applied to an instance the app makes later. What the app holds, how a
/// text loads and what is said of it are the framework's.</summary>
internal abstract class XamlReloader
{
    /// <summary>The newest text applied of each x:Class, by its type, with the text its build made instances from.</summary>
    private readonly Dictionary<Type, (XamlChange Change, string? Built)> latest = [];

    /// <summary>The text each object made from an x:Class holds once it took one.</summary>
    private readonly ConditionalWeakTable<object, string> holds = new();

    public XamlReloaded Reload(XamlChange change) =>
        XamlReloading.Refusal(change, out XamlRoot now, out _) ?? (now.Class is { } named ? ReloadClass(change, named) : ReloadIncluded(change));

    /// <summary>An object of a reloaded x:Class loaded: made from its build's XAML, it takes the newest text applied.</summary>
    public void Loaded(object? made)
    {
        if (made is null || latest.Count == 0 || !latest.TryGetValue(made.GetType(), out (XamlChange Change, string? Built) newest)
            || (holds.TryGetValue(made, out string? held) && held == newest.Change.Text))
        {
            return;
        }
        try
        {
            Apply(made, newest.Change, newest.Built, []);
        }
        catch (Refusal refused)
        {
            InspectorTrace.Step($"{Words.TypeName(made.GetType())} loaded and could not take {newest.Change.File}: {refused.Message}");
        }
    }

    /// <summary>Every object the app holds that a XAML file's root can make, outer before inner.</summary>
    protected abstract IEnumerable<object> Reachable();

    /// <summary>Why the framework cannot load an x:Class's instances again, said after the class; null where it can.</summary>
    protected abstract string? Unreloadable(Type type);

    /// <summary>A file without an x:Class applied where the app holds what its build made from it.</summary>
    protected abstract XamlReloaded ReloadIncluded(XamlChange change);

    /// <summary>An instance as what a reload says names it.</summary>
    protected abstract string Called(object made);

    /// <summary>What loading an instance again adds to, held as it is now.</summary>
    protected abstract IHeldRoot Hold(object made, string file);

    /// <summary>The text loaded into an instance, what its compiler says added to <paramref name="said"/>.</summary>
    protected abstract void Load(XamlChange change, object made, List<XamlProblem> said);

    /// <summary>Why a text could not apply, in the person's words, the problem it names among <paramref name="said"/>.</summary>
    protected abstract string Why(XamlChange change, Exception error, List<XamlProblem> said, Type? root);

    /// <summary>Points an instance's x:Name fields at the elements the text made.</summary>
    protected abstract void Named(object made);

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
        if (Unreloadable(type) is { } why)
        {
            return XamlReloading.Refused($"{Path.GetFileName(change.File)}'s x:Class {named} {why}: restart the app to apply it", []);
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
            objects.Add(Called(made));
        }
        latest[type] = (change, built);
        return new XamlReloaded(objects, null, said);
    }

    /// <summary>Loads one object again from the text, its reset taken back if the text fails.</summary>
    private void Apply(object made, XamlChange change, string? before, List<XamlProblem> said)
    {
        XamlRoot now = XamlRoot.Read(change.Text);
        XamlRoot? was = XamlReloading.Readable(before);
        IHeldRoot held = Hold(made, change.File);
        try
        {
            held.Reset(now, was);
            Load(change, made, said);
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
}

/// <summary>What loading a root again adds to (its collections, resources, handlers and local values), taken away first and
/// kept to put back. What of these the text before did not write is the app's own, kept through a load that succeeds.</summary>
internal interface IHeldRoot
{
    /// <summary>Clears what loading adds to, and what the text before set on the root that the text now does not.</summary>
    void Reset(XamlRoot now, XamlRoot? was);

    /// <summary>The text failed: the root as it was.</summary>
    void PutBack(XamlRoot now);

    /// <summary>The text applied: what the app put in the root's collections goes back where it stood, and a root property
    /// the text sets as the text before did keeps what the app held in it, unless that is part of the tree.</summary>
    void Keep(XamlRoot now, XamlRoot? was);
}
