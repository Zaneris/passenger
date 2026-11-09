using System.Globalization;
using System.Text;

namespace Passenger.Workspace;

/// <summary>Godot's editor settings file as far as its script editors go: where a Godot keeps it
/// (<c>editor_settings-4.7.tres</c>, its version's, in its user folder or beside a self-contained Godot), the values that
/// make Passenger the external editor of its C# scripts (Editor Settings, Dotnet, Editor, External Editor's Custom) and of
/// its other scripts (Text Editor, External), read from and written into the file's text with every other line kept as
/// it was. The file is a text resource: its <c>[resource]</c> section's <c>key = value</c> lines, a value any of Godot's,
/// a string quoted with its escapes.</summary>
public static class GodotEditorSettings
{
    public const string ExternalEditor = "dotnet/editor/external_editor";
    public const string CustomExecPath = "dotnet/editor/custom_exec_path";
    public const string CustomExecPathArgs = "dotnet/editor/custom_exec_path_args";
    public const string UseExternalEditor = "text_editor/external/use_external_editor";
    public const string ExecPath = "text_editor/external/exec_path";
    public const string ExecFlags = "text_editor/external/exec_flags";

    /// <summary>The number of <see cref="ExternalEditor"/>'s Custom choice.</summary>
    public const long Custom = 6;

    /// <summary>Passenger's arguments for a C# script, Godot filling in its project, file, and line and column counted from 0.</summary>
    public const string ScriptArguments = "{project} --file {file} --caret {line}:{col} --zero-based";

    /// <summary>Passenger's arguments for another script, Godot filling in its line and column counted from 1.</summary>
    public const string TextArguments = "{project} --file {file} --caret {line}:{col}";

    /// <summary>The values that make Passenger, at its executable's path, the editor Godot's opens every script in.</summary>
    public static IReadOnlyList<KeyValuePair<string, object>> ForPassenger(string executable) =>
    [
        new(ExternalEditor, Custom),
        new(CustomExecPath, executable),
        new(CustomExecPathArgs, ScriptArguments),
        new(UseExternalEditor, true),
        new(ExecPath, executable),
        new(ExecFlags, TextArguments),
    ];

    /// <summary>The folder a Godot's editor keeps its settings in: <c>editor_data</c> beside a self-contained one (a
    /// <c>._sc_</c> or <c>_sc_</c> file beside its executable, or on macOS beside its app bundle), else the user's: APPDATA's
    /// Godot on Windows, Library/Application Support/Godot on macOS, XDG_CONFIG_HOME's (else ~/.config's) godot elsewhere;
    /// null where the variables that place it are unset.</summary>
    public static string? Folder(string executable, bool windows, bool macOS, Func<string, string?> variable, Func<string, bool> exists)
    {
        if (SelfContainedFolder(executable, macOS, exists) is { } own)
        {
            return own;
        }
        if (windows)
        {
            return variable("APPDATA") is { Length: > 0 } data ? Path.Combine(data, "Godot") : null;
        }
        if (macOS)
        {
            return variable("HOME") is { Length: > 0 } home ? Path.Combine(home, "Library", "Application Support", "Godot") : null;
        }
        if (variable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config))
        {
            return Path.Combine(config, "godot");
        }
        return variable("HOME") is { Length: > 0 } user ? Path.Combine(user, ".config", "godot") : null;
    }

    /// <summary>The folder a Godot keeps its data in (its export templates among them): its settings' folder on Windows,
    /// macOS and beside a self-contained one, else XDG_DATA_HOME's (else ~/.local/share's) godot; null where the variables
    /// that place it are unset.</summary>
    public static string? DataFolder(string executable, bool windows, bool macOS, Func<string, string?> variable, Func<string, bool> exists)
    {
        if (windows || macOS || SelfContainedFolder(executable, macOS, exists) is not null)
        {
            return Folder(executable, windows, macOS, variable, exists);
        }
        if (variable("XDG_DATA_HOME") is { Length: > 0 } data && Path.IsPathRooted(data))
        {
            return Path.Combine(data, "godot");
        }
        return variable("HOME") is { Length: > 0 } user ? Path.Combine(user, ".local", "share", "godot") : null;
    }

    /// <summary>The <c>editor_data</c> folder of a self-contained Godot (a <c>._sc_</c> or <c>_sc_</c> file beside its
    /// executable, or on macOS beside its app bundle), else null.</summary>
    private static string? SelfContainedFolder(string executable, bool macOS, Func<string, bool> exists)
    {
        string beside = Path.GetDirectoryName(executable) ?? string.Empty;
        if (SelfContained(beside, exists))
        {
            return Path.Combine(beside, "editor_data");
        }
        if (macOS && Path.GetFileName(beside) == "MacOS" && Path.GetFileName(Path.GetDirectoryName(beside)) == "Contents"
            && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(beside))) is { } bundleFolder && SelfContained(bundleFolder, exists))
        {
            return Path.Combine(bundleFolder, "editor_data");
        }
        return null;

        static bool SelfContained(string folder, Func<string, bool> exists) =>
            exists(Path.Combine(folder, "._sc_")) || exists(Path.Combine(folder, "_sc_"));
    }

    /// <summary>The settings file a Godot of a version reads, its own version's or else the newest older one's
    /// (<c>editor_settings-4.tres</c> before 4.3), null where none is; and the one it writes, its own version's.</summary>
    public static (string? Read, string Written) Files(string folder, int major, int minor, Func<string, bool> exists)
    {
        string written = Path.Combine(folder, $"editor_settings-{major}.{minor}.tres");
        for (int older = minor; older >= 0; older--)
        {
            string name = major == 4 && older < 3 ? $"editor_settings-{major}.tres" : $"editor_settings-{major}.{older}.tres";
            if (exists(Path.Combine(folder, name)))
            {
                return (Path.Combine(folder, name), written);
            }
            if (major == 4 && older < 3)
            {
                break;
            }
        }
        return (null, written);
    }

    /// <summary>The values the text's <c>[resource]</c> section gives the keys named: a string, a whole number, true or
    /// false, or the text of any other value; a key set twice keeps its last, as Godot reads it.</summary>
    public static IReadOnlyDictionary<string, object> Read(string text, IEnumerable<string> keys)
    {
        HashSet<string> wanted = [.. keys];
        Dictionary<string, object> found = new(StringComparer.Ordinal);
        GodotFile file = GodotFile.Parse(text);
        foreach (GodotEntry entry in file.Resource?.Properties ?? [])
        {
            if (wanted.Contains(entry.Key))
            {
                found[entry.Key] = ValueOf(file, entry.Value);
            }
        }
        return found;
    }

    /// <summary>The text with each value set: the line that gives a key its value replaced (its last, which Godot reads, where
    /// the key is set twice), else the key added at the end of the <c>[resource]</c> section, every other line as it was and in
    /// its line endings; a text with no such section (no file yet) made the one a new settings file begins as.</summary>
    public static string With(string text, IReadOnlyList<KeyValuePair<string, object>> values)
    {
        string ending = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        GodotFile file = GodotFile.Parse(text);
        if (file.Resource is null)
        {
            text = (text.Trim().Length == 0 ? $"[gd_resource type=\"EditorSettings\" format=3]{ending}" : text.TrimEnd() + ending) + $"{ending}[resource]{ending}";
            file = GodotFile.Parse(text);
        }
        Dictionary<string, object> left = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        Dictionary<string, GodotEntry> lasts = new(StringComparer.Ordinal);
        foreach (GodotEntry entry in file.Resource?.Properties.Where(e => left.ContainsKey(e.Key)) ?? [])
        {
            lasts[entry.Key] = entry;
        }
        StringBuilder written = new();
        int at = 0;
        foreach (GodotEntry entry in lasts.Values.OrderBy(e => e.KeyStart))
        {
            written.Append(text, at, entry.KeyStart - at).Append(entry.Key).Append(" = ").Append(Written(left[entry.Key]));
            left.Remove(entry.Key);
            at = entry.Value.End;
        }
        written.Append(text, at, text.Length - at);
        if (left.Count > 0)
        {
            if (written.Length > 0 && written[^1] != '\n')
            {
                written.Append(ending);
            }
            foreach (KeyValuePair<string, object> value in values.Where(v => left.ContainsKey(v.Key)))
            {
                written.Append(value.Key).Append(" = ").Append(Written(value.Value)).Append(ending);
            }
        }
        return written.ToString();
    }

    /// <summary>A value as the file writes it: a string in quotes with its backslashes and quotes escaped, true or false, a
    /// number as it is.</summary>
    public static string Written(object value) => value switch
    {
        string text => GodotWrites.Quoted(text),
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Godot's settings take no {value.GetType().Name} from Passenger", nameof(value)),
    };

    private static object ValueOf(GodotFile file, GodotValue value)
    {
        if (value is { Call: null, Text: { } said })
        {
            return said;
        }
        string raw = file.Written(value).Trim();
        return raw switch
        {
            "true" => true,
            "false" => false,
            _ when long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) => number,
            _ => raw,
        };
    }
}
