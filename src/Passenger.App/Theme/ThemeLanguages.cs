using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Passenger.App;

/// <summary>The code's colours a theme and a code colour scheme give one language, put on each editor showing a file of it:
/// a dictionary of resources in the editor's own, which the editor finds before the application's, so a token the language
/// gives wins there and every other stays the file's own. Laid in order, the theme's general tokens, its language's, the
/// scheme's general tokens, then its language's, the last laid winning. An editor of a language neither gives holds none.
/// UI thread only.</summary>
public static class ThemeLanguages
{
    private static readonly ConditionalWeakTable<Control, Followed> followed = [];
    private static Dictionary<string, (Dictionary<string, TokenValue> Dark, Dictionary<string, TokenValue> Light)> laid = new(StringComparer.Ordinal);
    private static (ThemeFile? Theme, ThemeFile? Scheme) from;

    /// <summary>What an editor shows and the resources put on it for that.</summary>
    private sealed class Followed
    {
        public string? Language;
        public ResourceDictionary? Put;
    }

    /// <summary>An editor shows a file of a language, by its id; null for none.</summary>
    public static void Follow(Control editor, string? language)
    {
        Followed shown = followed.GetOrCreateValue(editor);
        if (shown.Language == language && (shown.Put is not null) == (language is not null && laid.ContainsKey(language)))
        {
            return;
        }
        shown.Language = language;
        Put(editor, shown);
    }

    /// <summary>The languages' colours of a theme and a scheme in force, put on every editor showing one.</summary>
    internal static void Use(ThemeFile? theme, ThemeFile? scheme)
    {
        if (ReferenceEquals(from.Theme, theme) && ReferenceEquals(from.Scheme, scheme))
        {
            return;
        }
        from = (theme, scheme);
        Dictionary<string, (Dictionary<string, TokenValue>, Dictionary<string, TokenValue>)> next = new(StringComparer.Ordinal);
        IEnumerable<string> languages = (theme?.Languages.Keys ?? []).Concat(scheme?.Languages.Keys ?? []).Distinct(StringComparer.Ordinal);
        foreach (string language in languages)
        {
            next[language] = (Variant(theme, scheme, language, dark: true), Variant(theme, scheme, language, dark: false));
        }
        if (next.Count == 0 && laid.Count == 0)
        {
            return;
        }
        laid = next;
        foreach ((Control editor, Followed shown) in followed)
        {
            Put(editor, shown);
        }
    }

    /// <summary>A language's tokens under one variant, laid in order.</summary>
    private static Dictionary<string, TokenValue> Variant(ThemeFile? theme, ThemeFile? scheme, string language, bool dark)
    {
        Dictionary<string, TokenValue> tokens = new(StringComparer.Ordinal);
        Over(tokens, theme?.Languages.GetValueOrDefault(language), dark);
        if (scheme is not null)
        {
            // The scheme's own tokens are laid over the theme's language's.
            foreach (string token in scheme.Both.Keys.Concat((dark ? scheme.Dark : scheme.Light).Keys))
            {
                tokens.Remove(token);
            }
        }
        Over(tokens, scheme?.Languages.GetValueOrDefault(language), dark);
        return tokens;

        static void Over(Dictionary<string, TokenValue> tokens, LanguageColours? colours, bool dark)
        {
            if (colours is null)
            {
                return;
            }
            foreach ((string token, TokenValue value) in colours.Both.Concat(dark ? colours.Dark : colours.Light))
            {
                tokens[token] = value;
            }
        }
    }

    private static void Put(Control editor, Followed shown)
    {
        ResourceDictionary? next = null;
        if (shown.Language is { } language && laid.TryGetValue(language, out (Dictionary<string, TokenValue> Dark, Dictionary<string, TokenValue> Light) tokens))
        {
            next = new ResourceDictionary();
            next.ThemeDictionaries[ThemeVariant.Dark] = ThemeResources.Filled(tokens.Dark);
            next.ThemeDictionaries[ThemeVariant.Light] = ThemeResources.Filled(tokens.Light);
        }
        if (shown.Put is { } before)
        {
            editor.Resources.MergedDictionaries.Remove(before);
        }
        shown.Put = next;
        if (next is not null)
        {
            editor.Resources.MergedDictionaries.Add(next);
        }
    }
}
