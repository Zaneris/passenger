namespace Passenger.Workspace;

/// <summary>What a Godot project's text files say written as Godot's editor writes it: a setting of project.godot (an input
/// action, an autoload) put last in its section, the section made in its place among the others where there is none, and a
/// group given to a node in its scene's header. Pure: each takes a file's text and gives the text it becomes, its line
/// breaks the file's.</summary>
public static class GodotWrites
{
    /// <summary>An input action's value as Godot's editor writes one it has just made: no events, the default deadzone.</summary>
    public static string ActionValue(string newline) => $"{{{newline}\"deadzone\": 0.2,{newline}\"events\": []{newline}}}";

    /// <summary>An autoload's value: its file's res:// path, a star before it making it a node of the tree as Godot's editor does.</summary>
    public static string AutoloadValue(string res) => $"\"*{res}\"";

    /// <summary>The line breaks a file's text writes: its first one's, else a line feed.</summary>
    public static string NewlineOf(string text) => text.IndexOf('\n', StringComparison.Ordinal) is > 0 and var at && text[at - 1] == '\r' ? "\r\n" : "\n";

    /// <summary>A key of a section set to a value, put after the section's last entry, the section made where the file has
    /// none (before the first that sorts after it, as Godot keeps them); null where the section has the key already.</summary>
    public static string? WithSetting(string text, string section, string key, string value)
    {
        string newline = NewlineOf(text);
        GodotFile file = GodotFile.Parse(text);
        string line = $"{key}={value}";
        if (file.Sections.FirstOrDefault(s => s.Tag == section) is { } found)
        {
            if (found.Properties.Any(p => p.Key == key))
            {
                return null;
            }
            int after = found.Properties.Count > 0 ? found.Properties[^1].Value.End : found.End;
            string lead = found.Properties.Count > 0 ? newline : newline + newline;
            return text[..after] + lead + line + text[after..];
        }
        string made = $"[{section}]{newline}{newline}{line}{newline}";
        if (file.Sections.FirstOrDefault(s => string.CompareOrdinal(s.Tag, section) > 0) is { } next)
        {
            return text[..next.Start] + made + newline + text[next.Start..];
        }
        string body = text.TrimEnd('\r', '\n');
        return body + newline + newline + made;
    }

    /// <summary>project.godot's text with the editor plugins enabled exactly those given, by their res:// paths, in the order
    /// given: its <c>[editor_plugins]</c>' <c>enabled</c> written over, else made where Godot puts it.</summary>
    public static string WithPlugins(string text, IReadOnlyList<string> enabled)
    {
        string value = $"PackedStringArray({string.Join(", ", enabled.Select(Quoted))})";
        GodotFile file = GodotFile.Parse(text);
        if (file.Sections.FirstOrDefault(s => s.Tag == GodotPlugins.Section)?.Property(GodotPlugins.Key) is { } property)
        {
            return text[..property.Value.Start] + value + text[property.Value.End..];
        }
        return WithSetting(text, GodotPlugins.Section, GodotPlugins.Key, value) ?? text;
    }

    /// <summary>project.godot's text with an autoload made a global the project's scripts name (its path written after a
    /// <c>*</c>, as Godot's Autoload settings write it) or a node alone; null where it is so already, or no autoload has the name.</summary>
    public static string? WithAutoloadGlobal(string text, string name, bool global)
    {
        GodotFile file = GodotFile.Parse(text);
        if (file.Sections.FirstOrDefault(s => s.Tag == "autoload")?.Property(name)?.Value is not { Text: { } path } value || path.StartsWith('*') == global)
        {
            return null;
        }
        return global ? text[..value.TextStart] + "*" + text[value.TextStart..] : text[..value.TextStart] + text[(value.TextStart + 1)..];
    }

    /// <summary>A name as project.godot writes a key: bare where Godot leaves it so, else quoted.</summary>
    public static string SettingKey(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') ? name : Quoted(name);

    /// <summary>project.godot's text with its main scene the one given (<c>run/main_scene</c> of <c>[application]</c>): its
    /// value written over, else the key put after the project's name, description, version and tags, where Godot's own
    /// save puts it; null where it is that scene already.</summary>
    public static string? WithMainScene(string text, string res)
    {
        const string Key = "run/main_scene";
        GodotFile file = GodotFile.Parse(text);
        string value = Quoted(res);
        if (file.Sections.FirstOrDefault(s => s.Tag == "application") is not { } application)
        {
            return WithSetting(text, "application", Key, value);
        }
        if (application.Property(Key) is { } set)
        {
            return set.Value.Text == res ? null : text[..set.Value.Start] + value + text[set.Value.End..];
        }
        string newline = NewlineOf(text);
        if (application.Properties.LastOrDefault(p => p.Key is "config/name" or "config/name_localized" or "config/description" or "config/version" or "config/tags") is { } before)
        {
            return text[..before.Value.End] + newline + $"{Key}={value}" + text[before.Value.End..];
        }
        return application.Properties.Count > 0
            ? text[..application.Properties[0].KeyStart] + $"{Key}={value}" + newline + text[application.Properties[0].KeyStart..]
            : WithSetting(text, "application", Key, value);
    }

    /// <summary>Why Godot's editor refuses a new input action's name (none, a character its Input Map refuses, one of its own
    /// actions); null for a name it takes.</summary>
    public static string? WhyNotActionName(string name) =>
        name.Trim().Length == 0 ? "An input action needs a name"
        : name.IndexOfAny(['/', ':', '=', '\\', '"']) >= 0 ? $"An input action's name holds none of / : = \\ \": {name} does"
        : GodotDefinitions.BuiltInActions.Contains(name) ? $"{name} is one of Godot's own input actions"
        : null;

    /// <summary>project.godot's text with a new input action of no events, last in <c>[input]</c> as Godot's editor adds
    /// one; null where it has the action already.</summary>
    public static string? WithAction(string text, string name) =>
        GodotFile.Parse(text).Sections.FirstOrDefault(s => s.Tag == "input")?.Property(name) is not null ? null
            : WithSetting(text, "input", SettingKey(name), ActionValue(NewlineOf(text)));

    /// <summary>project.godot's text without a setting of a section, every line giving it, the last of which Godot reads and
    /// an earlier one once that goes, and without the section where nothing else is left and no comment is written there, as
    /// Godot's own save writes no empty one; null where it sets none.</summary>
    public static string? WithoutSetting(string text, string section, string key)
    {
        string? without = null;
        while (WithoutLastLine(without ?? text, section, key) is { } less)
        {
            without = less;
        }
        return without;
    }

    /// <summary>project.godot's text without the last line giving a setting in the first section of that tag holding it (Godot
    /// reads a tag written twice as one section), and without that section where that line was all it held.</summary>
    private static string? WithoutLastLine(string text, string section, string key)
    {
        GodotFile file = GodotFile.Parse(text);
        if (file.Sections.FirstOrDefault(s => s.Tag == section && s.Property(key) is not null) is not { } found || found.Property(key) is not { } set)
        {
            return null;
        }
        int next = file.Sections.ToList().IndexOf(found) + 1;
        int sectionEnd = next < file.Sections.Count ? file.Sections[next].Start : text.Length;
        if (found.Properties.All(p => p.Key == key) && !Commented(text, found, sectionEnd))
        {
            return sectionEnd < text.Length ? text[..found.Start] + text[sectionEnd..] : text[..found.Start].TrimEnd('\r', '\n') + NewlineOf(text);
        }
        int start = set.KeyStart == 0 ? 0 : text.LastIndexOf('\n', set.KeyStart - 1) + 1;
        int end = LineEnd(text, set.Value.End);
        end += end < text.Length && text[end] == '\r' ? 1 : 0;
        end += end < text.Length && text[end] == '\n' ? 1 : 0;
        return text[..start] + text[end..];
    }

    /// <summary>Whether a section holds anything beside its tag and entries before <paramref name="end"/>: a person's comment.</summary>
    private static bool Commented(string text, GodotSection section, int end)
    {
        int at = section.End;
        foreach (GodotEntry entry in section.Properties)
        {
            if (!text.AsSpan(at, entry.KeyStart - at).IsWhiteSpace())
            {
                return true;
            }
            at = entry.Value.End;
        }
        return !text.AsSpan(at, end - at).IsWhiteSpace();
    }

    /// <summary>project.godot's text with an event added last to an input action's events, as Godot writes them (the first
    /// after the bracket, each next on a line of its own after a comma, the bracket closed on its own line); null where the
    /// project has no such action.</summary>
    public static string? WithEvent(string text, string action, string written)
    {
        if (EventsOf(text, action) is not { } events)
        {
            return null;
        }
        List<string> items = [.. GodotDefinitions.ItemsOf(text, events).Select(i => text[i.Start..i.End]), written];
        return text[..events.Start] + Events(items, NewlineOf(text)) + text[events.End..];
    }

    /// <summary>project.godot's text with an input action's event at an index taken out; null where there is no such event.</summary>
    public static string? WithoutEvent(string text, string action, int index)
    {
        if (EventsOf(text, action) is not { } events || GodotDefinitions.ItemsOf(text, events) is not { } items || index < 0 || index >= items.Count)
        {
            return null;
        }
        List<string> kept = [.. items.Where((_, i) => i != index).Select(i => text[i.Start..i.End])];
        return text[..events.Start] + Events(kept, NewlineOf(text)) + text[events.End..];
    }

    private static GodotValue? EventsOf(string text, string action)
    {
        GodotFile file = GodotFile.Parse(text);
        return file.Sections.FirstOrDefault(s => s.Tag == "input")?.Property(action) is { } property
            && GodotDefinitions.DictionaryOf(text, property.Value).FirstOrDefault(e => e.Key == "events") is { } events ? events.Value : null;
    }

    private static string Events(IReadOnlyList<string> items, string newline) => items.Count == 0 ? "[]" : $"[{string.Join($"{newline}, ", items)}{newline}]";

    /// <summary>A key pressed by its physical place, as Godot 4's editor writes an input action's key event.</summary>
    public static string KeyEvent(long physical, long unicode) =>
        "Object(InputEventKey,\"resource_local_to_scene\":false,\"resource_name\":\"\",\"device\":-1,\"window_id\":0,\"alt_pressed\":false,\"shift_pressed\":false,"
        + "\"ctrl_pressed\":false,\"meta_pressed\":false,\"pressed\":false,\"keycode\":0,"
        + $"\"physical_keycode\":{physical.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"key_label\":0,"
        + $"\"unicode\":{unicode.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"location\":0,\"echo\":false,\"script\":null)";

    /// <summary>A mouse button pressed, as Godot 4's editor writes an input action's mouse button event.</summary>
    public static string MouseButtonEvent(int button) =>
        "Object(InputEventMouseButton,\"resource_local_to_scene\":false,\"resource_name\":\"\",\"device\":-1,\"window_id\":0,\"alt_pressed\":false,\"shift_pressed\":false,"
        + $"\"ctrl_pressed\":false,\"meta_pressed\":false,\"button_mask\":0,\"position\":Vector2(0, 0),\"global_position\":Vector2(0, 0),\"factor\":1.0,"
        + $"\"button_index\":{button.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"canceled\":false,\"pressed\":true,\"double_click\":false,\"script\":null)";

    /// <summary>project.godot's text with a layer of a kind (<see cref="GodotDefinitions.LayerKinds"/>) named, its key of
    /// <c>[layer_names]</c> written over or put among the others in Godot's order (by kind, then number), or taken out where
    /// the name is empty; null where it is so already.</summary>
    public static string? WithLayerName(string text, string kind, int number, string name)
    {
        string key = $"{kind}/layer_{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        GodotFile file = GodotFile.Parse(text);
        GodotSection? layers = file.Sections.FirstOrDefault(s => s.Tag == "layer_names");
        if (layers?.Property(key) is { } set)
        {
            return name.Length == 0 ? WithoutSetting(text, "layer_names", key) : set.Value.Text == name ? null : text[..set.Value.Start] + Quoted(name) + text[set.Value.End..];
        }
        if (name.Length == 0)
        {
            return null;
        }
        (int, int) Rank(string written) => written.Split('/') is [{ } k, { } layer] && layer.StartsWith("layer_", StringComparison.Ordinal)
            && int.TryParse(layer["layer_".Length..], System.Globalization.CultureInfo.InvariantCulture, out int n)
            ? (GodotDefinitions.LayerKinds.ToList().IndexOf(k), n) : (int.MaxValue, 0);
        if (layers?.Properties.FirstOrDefault(p => Rank(p.Key).CompareTo((GodotDefinitions.LayerKinds.ToList().IndexOf(kind), number)) > 0) is { } next)
        {
            return text[..next.KeyStart] + $"{key}={Quoted(name)}" + NewlineOf(text) + text[next.KeyStart..];
        }
        return WithSetting(text, "layer_names", key, Quoted(name));
    }

    /// <summary>A group given to the node a scene's section declares, after those its header gives (a header with none takes a
    /// <c>groups=[…]</c> before the scene it instances, as Godot writes it); null where it has the group already, or the
    /// section is no node's.</summary>
    public static string? WithGroup(string text, int section, string group)
    {
        GodotFile file = GodotFile.Parse(text);
        if (file.Sections.FirstOrDefault(s => s.Start == section) is not { Tag: "node" } node)
        {
            return null;
        }
        string quoted = Quoted(group);
        if (node.Attribute("groups") is { } groups)
        {
            if (GodotDefinitions.StringsIn(text, groups.Value).Any(g => g.Text == group))
            {
                return null;
            }
            int close = groups.Value.End - 1;
            bool empty = text[(groups.Value.Start + 1)..close].Trim().Length == 0;
            return text[..close] + (empty ? quoted : ", " + quoted) + text[close..];
        }
        GodotEntry? instance = node.Attributes.FirstOrDefault(a => a.Key is "instance" or "instance_placeholder");
        int at = instance is not null ? instance.KeyStart - 1 : node.Attributes.Count > 0 ? node.Attributes[^1].Value.End : node.End - 1;
        return text[..at] + $" groups=[{quoted}]" + text[at..];
    }

    /// <summary>The line a node's header becomes once a group is given to it (<see cref="WithGroup"/>); null where it is not given.</summary>
    public static string? GroupLine(string text, int section, string group) =>
        WithGroup(text, section, group) is { } made ? made[section..LineEnd(made, section)] : null;

    private static int LineEnd(string text, int at)
    {
        int end = text.IndexOf('\n', at);
        end = end < 0 ? text.Length : end;
        return end > at && text[end - 1] == '\r' ? end - 1 : end;
    }

    /// <summary>A GDScript with a function made last, two blank lines before it as Godot's style has them, its body
    /// <c>pass</c> indented as the script's first indented line is (a tab where none is).</summary>
    public static string WithFunction(string text, string name, IReadOnlyList<string> parameters)
    {
        string newline = NewlineOf(text);
        string indent = text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.Length > 0 && l[0] is ' ' or '\t' && l.Trim().Length > 0) is { } line
            ? line[..(line.Length - line.TrimStart().Length)] : "\t";
        string body = text.TrimEnd('\r', '\n');
        string lead = body.Length == 0 ? string.Empty : newline + newline + newline;
        return $"{body}{lead}func {name}({string.Join(", ", parameters)}) -> void:{newline}{indent}pass{newline}";
    }

    /// <summary>A string as Godot's text writes it, its quotes and backslashes escaped.</summary>
    public static string Quoted(string value) => "\"" + Escaped(value) + "\"";

    /// <summary>A string's characters as Godot's text files write them between its quotes.</summary>
    public static string Escaped(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
