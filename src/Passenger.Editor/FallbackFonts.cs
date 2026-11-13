using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting.Unicode;

namespace Passenger.Editor;

/// <summary>Passenger's own font collection, through which every family it draws code or words in is named (<see cref="Carry"/>,
/// <see cref="Of"/>): a character the family lacks is drawn in the face the platform's own fallback names for it (DirectWrite,
/// CoreText, fontconfig: <see cref="Platform"/>), in the run's culture or else the UI's (an emoji's in emoji presentation's,
/// <see cref="LanguageOf"/>), as the system's own apps draw it, never in whichever face the process happened to load first.
/// An answer is kept for its <see cref="Bucket"/>, language, family and style, the platform asked again only for a character
/// that face lacks; a face that lacks the character or can't shape its
/// script, or none, leaves the character to Avalonia's own search. The faces it finds are served by a collection of their own
/// (<see cref="Found"/>), so none takes a family's name. Avalonia asks a collection for a character only through
/// <see cref="IFontCollection"/> where the collection doesn't derive from <see cref="FontCollectionBase"/>.</summary>
public sealed class FallbackFonts : IFontCollection
{
    private const string Scheme = "fonts:Passenger";

    private static readonly Uri OwnKey = new(Scheme);
    private static readonly OpenTypeTag Morx = OpenTypeTag.Parse("morx");
    private static readonly Lock Installing = new();
    private static readonly ConcurrentDictionary<string, FontFamily> Carried = new(StringComparer.Ordinal);
    private static FontManager? installedIn;

    private readonly Platform platform;
    private readonly Found found = new();

    /// <summary>Each family's face at each style, as its own collection gives it: null for none.</summary>
    private readonly ConcurrentDictionary<(string Family, FontCollectionKey Style), GlyphTypeface?> named = new();

    /// <summary>Each fallback asked for: by its bucket (<see cref="Asked.Codepoint"/> -1), else by the one character its
    /// bucket's face lacks; null where the platform named no face that serves.</summary>
    private readonly ConcurrentDictionary<Asked, Answer?> answers = new();

    private FallbackFonts(Platform platform) => this.platform = platform;

    /// <summary>The platform's own fallback: the installed face it names for a character in a language (a BCP 47 tag), asked
    /// by a family at a style, as the system's collection gives that face; null for none.</summary>
    public delegate GlyphTypeface? Platform(int codepoint, FontCollectionKey style, string? familyName, string language);

    /// <summary>BCP 47's tag for text in emoji presentation (the script Zsye), which fontconfig answers with a colour emoji
    /// face where one is installed (<c>fc-match :lang=und-zsye</c>).</summary>
    public const string EmojiLanguage = "und-Zsye";

    /// <summary>Puts the collection in the font manager in force, asking the platform's fallback through
    /// <paramref name="platform"/>: once for each font manager, before anything draws in a family it names.</summary>
    public static void Install(Platform platform)
    {
        FontManager manager = FontManager.Current;
        lock (Installing)
        {
            if (ReferenceEquals(installedIn, manager))
            {
                return;
            }
            FallbackFonts fonts = new(platform);
            manager.AddFontCollection(fonts);
            manager.AddFontCollection(fonts.found);
            installedIn = manager;
        }
    }

    /// <summary>A family Passenger carries (an <c>avares:</c> one), named through the collection.</summary>
    public static FontFamily Carry(FontFamily embedded)
    {
        Carried[embedded.Name] = embedded;
        return Of(embedded.Name);
    }

    /// <summary>A carried or installed family by its name, named through the collection; a name its form can't hold (a comma
    /// or a '#') is the plain family, left to Avalonia's own fallback.</summary>
    public static FontFamily Of(string name) =>
        name.AsSpan().IndexOfAny(',', '#') >= 0 ? new FontFamily(name) : new FontFamily($"{Scheme}#{name}");

    /// <summary>The language a character's fallback is asked in: emoji presentation's for one presented as an emoji by
    /// default, so the face named has the colour glyphs and the modifiers, joiners and flags an emoji's cluster goes on with,
    /// where a culture's may name a text face with its first code point alone; else the culture's.</summary>
    public static string LanguageOf(int codepoint, CultureInfo culture) =>
        new Codepoint((uint)codepoint).HasEmojiPresentation ? EmojiLanguage : culture.Name;

    /// <summary>What a character's fallback is kept under with others': its script, or for one common to scripts or of none
    /// (symbols, emoji, a script's punctuation), its page of 256 characters.</summary>
    public static int Bucket(int codepoint)
    {
        Script script = new Codepoint((uint)codepoint).Script;
        return script is Script.Common or Script.Inherited or Script.Unknown ? -1 - (codepoint >> 8) : (int)script;
    }

    public Uri Key => OwnKey;

    public int Count => Carried.Count;

    public FontFamily this[int index] => Of(Carried.Keys.Order(StringComparer.Ordinal).ElementAt(index));

    public IEnumerator<FontFamily> GetEnumerator() => Carried.Keys.Order(StringComparer.Ordinal).Select(Of).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        (string, FontCollectionKey) key = (familyName, new FontCollectionKey(style, weight, stretch));
        if (!named.TryGetValue(key, out glyphTypeface))
        {
            // A carried family from Passenger's own files, else the installed one as its plain name gives it.
            Typeface typeface = new(Carried.TryGetValue(familyName, out FontFamily? embedded) ? embedded : new FontFamily(familyName), style, weight, stretch);
            glyphTypeface = named.GetOrAdd(key, FontManager.Current.TryGetGlyphTypeface(typeface, out GlyphTypeface? face) ? face : null);
        }
        return glyphTypeface is not null;
    }

    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) => TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
    {
        IReadOnlyList<Typeface>? own = Carried.TryGetValue(familyName, out FontFamily? embedded) ? embedded.FamilyTypefaces
            : FontManager.Current.SystemFonts.TryGetFamilyTypefaces(familyName, out IReadOnlyList<Typeface>? installed) ? installed : null;
        familyTypefaces = own is { Count: > 0 } ? [.. own.Select(face => new Typeface(Of(familyName), face.Style, face.Weight, face.Stretch))] : null;
        return familyTypefaces is not null;
    }

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        syntheticGlyphTypeface = null;
        return false;
    }

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch, string? familyName,
        CultureInfo? culture, out Typeface typeface)
    {
        string language = LanguageOf(codepoint, culture ?? CultureInfo.CurrentUICulture);
        Asked bucket = new(Bucket(codepoint), -1, language, familyName, new FontCollectionKey(fontStyle, fontWeight, fontStretch));
        if (answers.TryGetValue(bucket, out Answer? kept) && kept is not null && kept.Covers(codepoint))
        {
            typeface = kept.Typeface;
            return true;
        }
        Asked alone = bucket with { Codepoint = codepoint };
        if (!answers.TryGetValue(alone, out Answer? answer))
        {
            answer = Ask(codepoint, bucket.Style, familyName, language);
            // The bucket keeps the first face that serves it; a character that face lacks keeps its own answer.
            if (answer is null || kept is not null || !answers.TryAdd(bucket, answer))
            {
                answer = answers.GetOrAdd(alone, answer);
            }
        }
        typeface = answer?.Typeface ?? default;
        return answer is not null;
    }

    /// <summary>Lets go of nothing: every face it gives is another collection's.</summary>
    public void Dispose()
    {
    }

    /// <summary>The face the platform's fallback names for a character, where it has the character and can shape its script.</summary>
    private Answer? Ask(int codepoint, FontCollectionKey style, string? familyName, string language)
    {
        if (platform(codepoint, style, familyName, language) is not { } glyphs || !glyphs.CharacterToGlyphMap.TryGetGlyph(codepoint, out _)
            || !Shapes(glyphs, new Codepoint((uint)codepoint).Script))
        {
            return null;
        }
        return new Answer(glyphs, new Typeface(found.Serve(glyphs), style.Style, style.Weight, style.Stretch));
    }

    /// <summary>Whether a face can shape a script: by its OpenType tables' word, or by Apple's own shaping table, 'morx', which
    /// the shaper reads as well (macOS's Arabic, Geeza Pro, declares no OpenType script).</summary>
    private static bool Shapes(GlyphTypeface glyphs, Script script) =>
        glyphs.CanShapeScript(script) || glyphs.PlatformTypeface.TryGetTable(Morx, out ReadOnlyMemory<byte> table) && table.Length > 0;

    /// <summary>A fallback asked for: its bucket, the one character where it is that character's own, the language, the
    /// family asking and its style.</summary>
    private readonly record struct Asked(int Bucket, int Codepoint, string Language, string? Family, FontCollectionKey Style);

    /// <summary>A face the platform named and the typeface that draws it.</summary>
    private sealed record Answer(GlyphTypeface Glyphs, Typeface Typeface)
    {
        public bool Covers(int codepoint) => Glyphs.CharacterToGlyphMap.TryGetGlyph(codepoint, out _);
    }

    /// <summary>The faces the platform's fallback named, each served under a name of its own, its family's and a number, so no
    /// family's name and no other face's leads to it.</summary>
    private sealed class Found : IFontCollection
    {
        private const string FoundScheme = "fonts:Passenger.Fallback";

        private static readonly Uri FoundKey = new(FoundScheme);

        private readonly Lock adding = new();
        private readonly ConcurrentDictionary<GlyphTypeface, FontFamily> families = new(ReferenceEqualityComparer.Instance);
        private readonly ConcurrentDictionary<string, GlyphTypeface> served = new(StringComparer.Ordinal);

        public Uri Key => FoundKey;

        public int Count => served.Count;

        public FontFamily this[int index] => Family(served.Keys.Order(StringComparer.Ordinal).ElementAt(index));

        /// <summary>The family serving a face, named once.</summary>
        public FontFamily Serve(GlyphTypeface glyphs)
        {
            if (families.TryGetValue(glyphs, out FontFamily? family))
            {
                return family;
            }
            lock (adding)
            {
                if (!families.TryGetValue(glyphs, out family))
                {
                    string name = $"{glyphs.FamilyName.Replace(',', ' ').Replace('#', ' ').Trim()} {served.Count}";
                    served[name] = glyphs;
                    families[glyphs] = family = Family(name);
                }
                return family;
            }
        }

        public IEnumerator<FontFamily> GetEnumerator() => served.Keys.Order(StringComparer.Ordinal).Select(Family).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) => served.TryGetValue(familyName, out glyphTypeface);

        public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) => served.TryGetValue(familyName, out glyphTypeface);

        public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            familyTypefaces = served.TryGetValue(familyName, out GlyphTypeface? glyphs)
                ? [new Typeface(Family(familyName), glyphs.Style, glyphs.Weight, glyphs.Stretch)]
                : null;
            return familyTypefaces is not null;
        }

        public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
        {
            syntheticGlyphTypeface = null;
            return false;
        }

        public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch, string? familyName,
            CultureInfo? culture, out Typeface typeface)
        {
            typeface = default;
            return false;
        }

        /// <summary>Lets go of nothing: every face it serves is the system's collection's.</summary>
        public void Dispose()
        {
        }

        private static FontFamily Family(string name) => new($"{FoundScheme}#{name}");
    }
}
