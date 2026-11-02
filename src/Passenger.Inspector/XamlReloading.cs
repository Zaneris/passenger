using System.Reflection;

namespace Passenger.Inspector;

/// <summary>What XAML Hot Reload does alike in either framework's app: a change's two texts read and compared, a change
/// only a build makes (another x:Class, another root element) refused in words, and the compiler's words and the app's
/// assemblies as both read them.</summary>
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
}
