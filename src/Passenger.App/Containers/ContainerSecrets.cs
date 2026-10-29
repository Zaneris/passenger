using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>Passenger's one rule for what is a secret, and its one mask: a value whose name says it is one
/// (<see cref="Secret(string)"/>) shows as the same dots whatever its length; in any other value, a URL's password
/// (<c>postgres://app:••••••@db</c>) and a connection string's part so named (<c>Password=••••••</c>); on a command line,
/// the word after a flag so named (<c>--password ••••••</c>, <see cref="SecretFlag"/>). An empty value hides nothing and
/// shows as it is.</summary>
public static partial class ContainerSecrets
{
    public const string Masked = "••••••";

    /// <summary>What makes a name a secret's anywhere in it, its case, underscores, dashes and dots aside (DBPASSWORD,
    /// SECRETKEY, ConnectionStrings__Shop).</summary>
    private static readonly SearchValues<string> Anywhere =
        SearchValues.Create(["PASSWORD", "PASSWD", "SECRET", "TOKEN", "CREDENTIAL", "CONNECTIONSTRING"], StringComparison.Ordinal);

    /// <summary>What makes a name a secret's as a word of it (DB_PASS, MYSQL_PW, DB_PWD, API_KEY), or its plural.</summary>
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Whole =
        new HashSet<string>(StringComparer.Ordinal) { "PASS", "PW", "PWD", "KEY", "AUTHORIZATION" }.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The words that end in KEY and name no key (a word ending in KEY otherwise is one: APIKEY, ACCESSKEY).</summary>
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Ordinary = new HashSet<string>(StringComparer.Ordinal)
    {
        "MONKEY", "DONKEY", "TURKEY", "JOCKEY", "HOCKEY", "WHISKEY", "LACKEY", "FLUNKEY", "MALARKEY", "HOTKEY", "TURNKEY",
    }.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The shell's working folders, paths a compose file's volumes are written with, whose PWD is no password's.</summary>
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Folders =
        new HashSet<string>(StringComparer.Ordinal) { "PWD", "OLDPWD" }.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>What every mask but a Bearer's or Basic's credentials and a flag's word needs in a line to find anything
    /// there.</summary>
    private static readonly SearchValues<char> Marks = SearchValues.Create("=:@");

    /// <summary>What a flag's name is written in (<c>--api-key</c>, <c>--ConnectionStrings:Shop</c>).</summary>
    private static readonly SearchValues<char> FlagName =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.:");

    /// <summary>What a flag in what a program said begins after, as <see cref="FlagValue"/> reads it.</summary>
    private static readonly SearchValues<char> BeforeFlag = SearchValues.Create(" \t\"'`([{");

    /// <summary>The longest name read on the stack; a longer one is read in a rented buffer.</summary>
    private const int OnStack = 128;

    /// <summary>Whether a name's value is a secret: it holds one of <see cref="Anywhere"/>, or a word of it (between
    /// underscores, dashes or dots, or where its case turns, as apiKey's Key) is one of <see cref="Whole"/> or its plural,
    /// or ends in KEY but for <see cref="Ordinary"/>'s words; so DBPASSWORD, APIKEY and DB_PASS are and MONKEY is not,
    /// nor the shell's own PWD and OLDPWD (<see cref="Folders"/>).</summary>
    public static bool Secret(string name) => Secret(name.AsSpan());

    /// <inheritdoc cref="Secret(string)"/>
    public static bool Secret(ReadOnlySpan<char> name)
    {
        if (Folders.Contains(name))
        {
            return false;
        }
        char[]? rented = null;
        // The name in capitals, then again without its underscores, dashes and dots.
        Span<char> read = name.Length <= OnStack ? stackalloc char[2 * OnStack] : (rented = ArrayPool<char>.Shared.Rent(2 * name.Length));
        try
        {
            Span<char> upper = read[..name.Length];
            name.ToUpperInvariant(upper);
            Span<char> squeezed = read[name.Length..];
            int length = 0;
            foreach (char c in upper)
            {
                if (c is not ('_' or '-' or '.'))
                {
                    squeezed[length++] = c;
                }
            }
            return squeezed[..length].ContainsAny(Anywhere) || AnyWord(name, upper);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Whether a word of the name is a secret's (<see cref="Secret(string)"/>): its words end at a run of
    /// underscores, dashes and dots, before a capital after a small letter or digit, and before a capital that begins a
    /// word after capitals (APIKey's Key); each is read in <paramref name="upper"/>, the name in capitals.</summary>
    private static bool AnyWord(ReadOnlySpan<char> name, ReadOnlySpan<char> upper)
    {
        int start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            bool apart = i < name.Length && name[i] is '_' or '-' or '.';
            if (!apart && i < name.Length && !Turns(name, i))
            {
                continue;
            }
            if (i > start && Names(upper[start..i]))
            {
                return true;
            }
            start = apart ? i + 1 : i;
        }
        return false;
    }

    /// <summary>Whether a word begins at a place where the name's case turns: a capital after a small letter or digit,
    /// or a capital with a small letter after it following a capital (ASCII alone).</summary>
    private static bool Turns(ReadOnlySpan<char> name, int at) =>
        at > 0 && char.IsAsciiLetterUpper(name[at])
            && (char.IsAsciiLetterLower(name[at - 1]) || char.IsAsciiDigit(name[at - 1])
                || (char.IsAsciiLetterUpper(name[at - 1]) && at + 1 < name.Length && char.IsAsciiLetterLower(name[at + 1])));

    /// <summary>Whether a word, in capitals, names a secret: it or its singular is one of <see cref="Whole"/>, or ends
    /// in KEY but for <see cref="Ordinary"/>'s words.</summary>
    private static bool Names(ReadOnlySpan<char> word)
    {
        ReadOnlySpan<char> single = word.Length > 1 && word[^1] == 'S' ? word[..^1] : word;
        return Whole.Contains(word) || Whole.Contains(single)
            || (word.EndsWith("KEY", StringComparison.Ordinal) && !Ordinary.Contains(word))
            || (single.EndsWith("KEY", StringComparison.Ordinal) && !Ordinary.Contains(single));
    }

    /// <summary>Whether a word of a command line is a flag naming a secret, so the word after it is one (<c>--password</c>,
    /// <c>--api-key</c>, <c>-pwd</c>): one or two dashes before a name of two characters or more that is a secret's and does
    /// not say it takes no value (<c>--no-password</c>, <c>--password-stdin</c>). One letter names nothing this rule can
    /// read: <c>-p</c> is a port to one program, a Compose project to another and a password to a third.</summary>
    private static bool SecretFlag(ReadOnlySpan<char> word)
    {
        ReadOnlySpan<char> name = word.StartsWith("--", StringComparison.Ordinal) ? word[2..] : word.StartsWith('-') ? word[1..] : [];
        return name.Length > 1 && char.IsAsciiLetter(name[0]) && !name.ContainsAnyExcept(FlagName)
            && !name.StartsWith("no-", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-stdin", StringComparison.OrdinalIgnoreCase)
            && Secret(name);
    }

    /// <summary>A word of a command line given on its own, masked: the dots for one after a flag naming a secret
    /// (<see cref="SecretFlag"/>) but an empty one, else as <paramref name="masking"/> masks it.</summary>
    private static string Argument(string word, string? before, Func<string, string> masking) =>
        word.Length > 0 && SecretFlag(before) ? Masked : masking(word);

    /// <summary>A program's arguments given one by one (an Aspire resource's), each masked as
    /// <see cref="CommandLine(System.Diagnostics.ProcessStartInfo)"/> masks a child's.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> words)
    {
        Func<string, string> masking = Masking();
        return [.. words.Select((word, i) => Argument(word, i == 0 ? null : words[i - 1], masking))];
    }

    /// <summary>A variable's value as it is shown.</summary>
    public static string Shown(string name, string value) => value.Length > 0 && Secret(name) ? Masked : InValue(value);

    /// <summary>A value whose name says nothing, as it is shown: a URL's password and a connection string's part named
    /// as a secret's masked, the rest as it is (the value itself when nothing is).</summary>
    public static string InValue(string value)
    {
        if (value.Length == 0 || !MasksInValue(value))
        {
            return value;
        }
        string bare = AuthorizationCredentials().Replace(value, $"${{scheme}} {Masked}");
        bare = UrlPassword().Replace(bare, $"${{head}}{Masked}@");
        return ConnectionPart().Replace(bare, part => Secret(part.Groups["name"].Value.Trim()) && part.Groups["value"].Length > 0
            ? $"{part.Groups["head"].Value}{Masked}" : part.Value);
    }

    /// <summary>Whether <see cref="InValue"/> may mask something in a value; when not, it gives the value back as it is.</summary>
    public static bool InValueMasks(ReadOnlySpan<char> value) => value.Length > 0 && MasksInValue(value);

    /// <summary>Whether <see cref="InValue"/> may mask something in a value: a Bearer's or Basic's credentials, a URL's
    /// password, or a connection string's part whose name is a secret's and whose value is not empty.</summary>
    private static bool MasksInValue(ReadOnlySpan<char> value)
    {
        if (AuthorizationCredentials().IsMatch(value) || UrlPassword().IsMatch(value))
        {
            return true;
        }
        foreach (ValueMatch part in ConnectionPart().EnumerateMatches(value))
        {
            // The part's name after the semicolon that begins it, to its '=', and its value after.
            ReadOnlySpan<char> found = value.Slice(part.Index, part.Length);
            int from = found[0] == ';' ? 1 : 0;
            int equals = found[from..].IndexOf('=') + from;
            if (equals + 1 < found.Length && Secret(found[from..equals].Trim()))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The shortest value a secret's name holds that <see cref="InSaid"/> masks wherever it stands: a shorter one
    /// would mask words and numbers that are no secret (a line's number, "true").</summary>
    private const int ShortestKnown = 4;

    /// <summary>What a program said of its failure (the docker CLI's and Compose's errors, the aspire CLI's, a resource
    /// service's status), masked wherever it is shown or logged: each value it knows to be a secret's (<paramref name="known"/>,
    /// this process's environment's variables whose names are a secret's, and those of the .env in <paramref name="folder"/>)
    /// as the dots wherever it stands; then on each line a <c>NAME=value</c>, <c>"NAME": "value"</c> or <c>--NAME value</c>
    /// whose name is a secret's, an Authorization header, a URL's credentials and a connection string's secret part.</summary>
    public static string InSaid(string said, string? folder = null, IEnumerable<string>? known = null) => Masking(folder, known)(said);

    /// <summary>A command line as Passenger logs or shows it (<see cref="ChildProcesses.Shown"/>): each word masked on its own
    /// as <see cref="InSaid"/> masks what a program said (so a <c>--password=…</c> masks its value, not the words after it),
    /// and one after a flag naming a secret as the dots (<see cref="SecretFlag"/>), knowing too the values of the child's own
    /// environment whose names are a secret's.</summary>
    public static string CommandLine(System.Diagnostics.ProcessStartInfo start)
    {
        Func<string, string> masking = Masking(known: [.. start.Environment.Where(v => v.Value is { Length: > 0 } && Secret(v.Key)).Select(v => v.Value!)]);
        return ChildProcesses.Display(start, (word, before) => Argument(word, before, masking));
    }

    /// <summary>Command lines as the system gives them (a running program's, as Attach to Process and debug_state list it),
    /// shown with each word masked on its own as <see cref="CommandLine(System.Diagnostics.ProcessStartInfo)"/> masks a
    /// child's, a word in double quotes inside them, and quoted still when it was. A line comes back as it was when nothing
    /// in it is masked; the secrets known are read once, for many lines.</summary>
    public static Func<string, string> CommandLines()
    {
        Func<string, string> masking = Masking();
        return line => InWords(line, masking);
    }

    /// <summary>A command line with each of its words masked: words are apart at white space outside double quotes, a quote
    /// after an odd run of backslashes being the word's own; copied only from the first word masked.</summary>
    private static string InWords(string line, Func<string, string> masking)
    {
        StringBuilder? shown = null;
        // The word before, inside its double quotes when it is quoted whole.
        ReadOnlySpan<char> before = [];
        int at = 0;
        while (at < line.Length)
        {
            int start = at;
            if (char.IsWhiteSpace(line[at]))
            {
                while (at < line.Length && char.IsWhiteSpace(line[at]))
                {
                    at++;
                }
                shown?.Append(line, start, at - start);
                continue;
            }
            bool quoted = false;
            while (at < line.Length && (quoted || !char.IsWhiteSpace(line[at])))
            {
                int backslashes = 0;
                while (at < line.Length && line[at] == '\\')
                {
                    backslashes++;
                    at++;
                }
                if (at < line.Length && line[at] == '"' && backslashes % 2 == 0)
                {
                    quoted = !quoted;
                }
                if (backslashes == 0 || (at < line.Length && line[at] == '"'))
                {
                    at++;
                }
            }
            string word = line[start..at];
            bool whole = word.Length > 1 && word[0] == '"' && word[^1] == '"';
            string bare = !SecretFlag(before) ? InWord(word, whole, masking)
                : whole ? (word.Length == 2 ? word : $"\"{Masked}\"") : Masked;
            if (shown is null && !string.Equals(bare, word, StringComparison.Ordinal))
            {
                shown = new StringBuilder(line.Length).Append(line, 0, start);
            }
            shown?.Append(bare);
            before = whole ? line.AsSpan(start + 1, at - start - 2) : line.AsSpan(start, at - start);
        }
        return shown?.ToString() ?? line;
    }

    /// <summary>A word of a command line masked: one in double quotes whole is masked inside them, its escaped quotes read
    /// as quotes, and quoted again only when something in it was masked.</summary>
    private static string InWord(string word, bool whole, Func<string, string> masking)
    {
        if (!whole)
        {
            return masking(word);
        }
        string inside = word[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        string bare = masking(inside);
        return string.Equals(bare, inside, StringComparison.Ordinal) ? word : ChildProcesses.Quote(bare);
    }

    /// <summary><see cref="InSaid"/> for much that is said a piece at a time (a CLI's output streamed into a console),
    /// the secrets it knows read once. What it is given comes back as it was when nothing in it is masked.</summary>
    public static Func<string, string> Masking(string? folder = null, IEnumerable<string>? known = null)
    {
        string[] secrets = [.. Known(folder, known).Where(s => s.Length >= ShortestKnown).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length)];
        return said =>
        {
            foreach (string secret in secrets)
            {
                said = said.Replace(secret, Masked, StringComparison.Ordinal);
            }
            return InLines(said);
        };
    }

    /// <summary>What was said with each of its lines masked (<see cref="InLine"/>); copied only from the first line
    /// masked, the text itself when none is.</summary>
    private static string InLines(string said)
    {
        StringBuilder? shown = null;
        int start = 0;
        while (true)
        {
            int end = said.IndexOf('\n', start);
            int stop = end < 0 ? said.Length : end;
            ReadOnlySpan<char> line = said.AsSpan(start, stop - start);
            if (MasksLine(line))
            {
                string read = line.ToString();
                string bare = InLine(read);
                if (shown is null && !string.Equals(bare, read, StringComparison.Ordinal))
                {
                    shown = new StringBuilder(said.Length).Append(said, 0, start);
                }
                shown?.Append(bare);
            }
            else
            {
                shown?.Append(line);
            }
            if (end < 0)
            {
                return shown?.ToString() ?? said;
            }
            shown?.Append('\n');
            start = end + 1;
        }
    }

    /// <summary>A line of what was said, masked as <see cref="InSaid"/> says.</summary>
    private static string InLine(string line)
    {
        // A Bearer's token first, which a NAME=value would otherwise leave after the scheme it masked.
        string bare = InValue(line);
        bare = Assignment().Replace(bare, pair => Secret(pair.Groups["name"].Value) ? $"{pair.Groups["head"].Value}{Quoted(pair.Groups["value"].Value)}" : pair.Value);
        bare = Member().Replace(bare, pair => Secret(pair.Groups["name"].Value) ? $"{pair.Groups["head"].Value}{Quoted(pair.Groups["value"].Value)}" : pair.Value);
        bare = FlagValue().Replace(bare, value => SecretFlag(value.Groups["flag"].ValueSpan) && value.Value is not ("\"\"" or "''") ? Quoted(value.Value) : value.Value);
        bare = AuthorizationHeader().Replace(bare, $"${{head}}{Masked}");
        return UrlUser().Replace(bare, $"${{scheme}}{Masked}@");

        static string Quoted(string value) => value.Length > 1 && value[0] is '"' or '\'' && value[^1] == value[0] ? $"{value[0]}{Masked}{value[0]}" : Masked;
    }

    /// <summary>Whether <see cref="InLine"/> may mask something in a line: one with none of <see cref="Marks"/> only a
    /// Bearer's or Basic's credentials or a flag's word, any other only where one of its masks finds what it masks, a name
    /// a secret's where it asks.</summary>
    private static bool MasksLine(ReadOnlySpan<char> line)
    {
        if (line.Contains('-'))
        {
            foreach (ValueMatch value in FlagValue().EnumerateMatches(line))
            {
                // Its flag is the word before the spaces before it.
                ReadOnlySpan<char> flag = line[..value.Index].TrimEnd(" \t");
                if (SecretFlag(flag[(flag.LastIndexOfAny(BeforeFlag) + 1)..]))
                {
                    return true;
                }
            }
        }
        if (!line.ContainsAny(Marks))
        {
            return AuthorizationCredentials().IsMatch(line);
        }
        if (MasksInValue(line) || AuthorizationHeader().IsMatch(line) || UrlUser().IsMatch(line))
        {
            return true;
        }
        foreach (ValueMatch pair in Assignment().EnumerateMatches(line))
        {
            // Its name runs from where it begins to the space or '=' after it.
            ReadOnlySpan<char> found = line.Slice(pair.Index, pair.Length);
            int end = 0;
            while (found[end] != '=' && !char.IsWhiteSpace(found[end]))
            {
                end++;
            }
            if (Secret(found[..end]))
            {
                return true;
            }
        }
        foreach (ValueMatch pair in Member().EnumerateMatches(line))
        {
            // Its name is between the quotes it begins with.
            ReadOnlySpan<char> found = line.Slice(pair.Index, pair.Length);
            if (Secret(found[1..(found[1..].IndexOf(found[0]) + 1)]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The values a failure may echo that are known to be secrets: those given, this process's environment's whose
    /// names are a secret's, and those a folder's .env gives such names (<c>KEY=value</c>, quoted or not, after an
    /// <c>export</c> or not; a line it cannot read passed over).</summary>
    private static IEnumerable<string> Known(string? folder, IEnumerable<string>? known)
    {
        foreach (string given in known ?? [])
        {
            yield return given;
        }
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && Secret(name) && variable.Value is string value)
            {
                yield return value;
            }
        }
        string[] lines = [];
        try
        {
            if (folder is not null && File.Exists(Path.Combine(folder, ".env")))
            {
                lines = File.ReadAllLines(Path.Combine(folder, ".env"));
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            // One that cannot be read now gave Compose nothing either.
        }
        foreach (string line in lines)
        {
            if (EnvLine().Match(line) is { Success: true } entry && Secret(entry.Groups["name"].Value))
            {
                string value = entry.Groups["value"].Value.Trim();
                yield return value.Length > 1 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1]
                    : value.IndexOf(" #", StringComparison.Ordinal) is int comment and >= 0 ? value[..comment].TrimEnd() : value;
            }
        }
    }

    /// <summary>A public navigable HTTP URL, without user information, secret query values, or fragments.</summary>
    public static string? PublicHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || value.Any(char.IsControl))
        {
            return null;
        }
        UriBuilder safe = new(uri) { UserName = string.Empty, Password = string.Empty, Fragment = string.Empty };
        List<string> query = [];
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            string name = Uri.UnescapeDataString((equals < 0 ? part : part[..equals]).Replace('+', ' '));
            if (Secret(name))
            {
                continue;
            }
            if (equals < 0)
            {
                query.Add(part);
                continue;
            }
            string original = Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
            string shown = InValue(original);
            query.Add(shown == original ? part : $"{part[..(equals + 1)]}{Uri.EscapeDataString(shown)}");
        }
        safe.Query = string.Join('&', query);
        return safe.Uri.AbsoluteUri;
    }

    /// <summary>Whether what a line has before and after a place makes it a URL's password (<c>postgres://user:</c> before,
    /// <c>@db</c> after), as a compose file's <c>${PGPASS}</c> there is.</summary>
    public static bool AtUrlPassword(string before, string after) => UserBefore().IsMatch(before) && HostAfter().IsMatch(after);

    /// <summary>An environment entry as the engine lists it ("NAME=value"), its secrets masked.</summary>
    public static string Entry(string entry)
    {
        int equals = entry.IndexOf('=', StringComparison.Ordinal);
        return equals <= 0 ? entry : $"{entry[..(equals + 1)]}{Shown(entry[..equals], entry[(equals + 1)..])}";
    }

    /// <summary>Masks, in place, every environment list in what <c>docker inspect</c> said: each <c>Env</c> array of
    /// "NAME=value" strings anywhere in it (a container's and an image's Config, an older image's ContainerConfig).</summary>
    public static void Mask(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject holder:
                foreach ((string name, JsonNode? value) in holder.ToList())
                {
                    if (name == "Env" && value is JsonArray entries)
                    {
                        for (int i = 0; i < entries.Count; i++)
                        {
                            if (entries[i] is JsonValue one && one.TryGetValue(out string? entry))
                            {
                                entries[i] = Entry(entry);
                            }
                        }
                    }
                    else
                    {
                        Mask(value);
                    }
                }
                break;
            case JsonArray items:
                foreach (JsonNode? item in items)
                {
                    Mask(item);
                }
                break;
        }
    }

    /// <summary>Where Inspect writes its copies, under a state folder.</summary>
    public static string InspectCopies(string stateFolder) => Path.Combine(stateFolder, "cache", "containers", "inspect");

    /// <summary>The state folders whose Inspect copies this process has looked through.</summary>
    private static readonly HashSet<string> Looked = new(StringComparer.Ordinal);

    /// <summary>Inspect's copies an earlier Passenger wrote before it masked them, deleted, once a process for each state
    /// folder: each copy whose masking would change it (a secret written whole), so a copy written masked, which masking
    /// leaves as it is, stays. Reads the disk, so off the UI thread. The paths deleted, canonical; none the second time.</summary>
    public static IReadOnlyList<string> ForgetUnmasked(string stateFolder)
    {
        lock (Looked)
        {
            if (!Looked.Add(PathNames.Canonical(stateFolder)))
            {
                return [];
            }
        }
        List<string> deleted = [];
        string folder = InspectCopies(stateFolder);
        if (!Directory.Exists(folder))
        {
            return deleted;
        }
        string[] copies;
        try
        {
            copies = Directory.GetFiles(folder, "*.json");
        }
        catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
        {
            return deleted;
        }
        foreach (string copy in copies)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(copy)) is not { } read)
                {
                    continue;
                }
                string written = read.ToJsonString();
                Mask(read);
                if (read.ToJsonString() != written)
                {
                    string path = PathNames.Canonical(copy);
                    File.Delete(copy);
                    deleted.Add(path);
                }
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or JsonException)
            {
                // A copy that cannot be read or deleted now is looked at again by the next process.
                IdeErrors.Write($"Inspect's copy {copy} could not be looked through for unmasked secrets: {unread.Message}");
            }
        }
        return deleted;
    }

    [GeneratedRegex(@"[_.\-]+|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex Words();

    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)[ \t]+[A-Za-z0-9+/_~.=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationCredentials();

    [GeneratedRegex(@"(?<head>[A-Za-z][A-Za-z0-9+.\-]*://[^\s/@:]*:)[^\s@]*@")]
    private static partial Regex UrlPassword();

    /// <summary>A connection string's part, "name=value" between semicolons.</summary>
    [GeneratedRegex(@"(?<head>(?:^|;)(?<name>[^=;]+)=)(?<value>[^;]*)")]
    private static partial Regex ConnectionPart();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s/@]*:[^\s/@]*$")]
    private static partial Regex UserBefore();

    /// <summary>A <c>NAME=value</c> in what a program said (an option's <c>--password=</c>, a query's <c>token=</c>, a
    /// connection string's part), its value quoted or up to a space, comma, semicolon, ampersand or closing bracket.</summary>
    [GeneratedRegex("""(?<head>(?<![\w.])(?<name>[A-Za-z_][\w.\-]*)\s*=\s*)(?<value>"[^"]*"|'[^']*'|[^\s"',;&}\]]+)""")]
    private static partial Regex Assignment();

    /// <summary>A word after a flag (<c>--name</c> or <c>-name</c> where a word begins, after <see cref="BeforeFlag"/>'s
    /// characters) in what a program said, the flag its group, the word quoted or up to what ends an
    /// <see cref="Assignment"/>'s value.</summary>
    [GeneratedRegex("""(?<=(?:^|[ \t"'`(\[{])(?<flag>--?[A-Za-z][A-Za-z0-9_.:\-]*)[ \t]+)(?<value>"[^"]*"|'[^']*'|[^\s"',;&}\]]+)""")]
    private static partial Regex FlagValue();

    /// <summary>A JSON or YAML member whose name is quoted (<c>"apiKey": "…"</c>), its value quoted or up to a comma or bracket.</summary>
    [GeneratedRegex("""(?<head>(?<q>["'])(?<name>[A-Za-z_][\w.\-]*)\k<q>\s*:\s*)(?<value>"[^"]*"|'[^']*'|[^\s,}\]]+)""")]
    private static partial Regex Member();

    [GeneratedRegex(@"(?<head>\bAuthorization\s*[:=]\s*)\S.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    /// <summary>A URL's user information with no password (<c>https://token@host</c>), which can be a token.</summary>
    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.\-]*://)[^\s/@:]+@")]
    private static partial Regex UrlUser();

    /// <summary>A .env file's line: <c>KEY=value</c>, after an <c>export</c> or not.</summary>
    [GeneratedRegex(@"^\s*(?:export\s+)?(?<name>[A-Za-z_][A-Za-z0-9_.\-]*)\s*=(?<value>.*)$")]
    private static partial Regex EnvLine();

    [GeneratedRegex(@"^[^\s/@]*@")]
    private static partial Regex HostAfter();
}
