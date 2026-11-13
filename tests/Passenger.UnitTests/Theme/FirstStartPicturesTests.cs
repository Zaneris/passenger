using System.Buffers.Binary;
using System.Text.Json;
using Passenger.App;
using Passenger.App.Welcome;
using Passenger.Core;

namespace Passenger.UnitTests.Theme;

/// <summary>The first start's pictures as the app carries them: of this version of Passenger, so a version moved without
/// taking them again (<c>just first-start-shots</c>) fails here; one for Passenger's own theme and for every theme it
/// carries, in dark and in light; and each lossless, of one size, with the pixels to stay sharp at the widest a tile draws
/// it on a screen of the largest scale.</summary>
public sealed class FirstStartPicturesTests
{
    [Fact]
    public void The_first_starts_pictures_are_of_this_version()
    {
        using JsonDocument kept = JsonDocument.Parse(Resource(FirstStartPictures.VersionFile));
        string? version = JsonMembers.Text(kept.RootElement, "version");
        Assert.True(version == Product.Version,
            $"The first start's tiles show Passenger {version ?? "of no version"}, where this build is {Product.Version}: a person's first start would "
            + "show an earlier Passenger. Take them again on macOS with just first-start-shots, and commit src/Passenger.App/Assets/FirstStart.");
    }

    [Fact]
    public void Every_theme_offered_has_a_lossless_picture_sharp_at_the_largest_scale_in_dark_and_light()
    {
        string[] themes = ["Passenger", .. CarriedThemes()];
        Assert.Contains("Cubicle", themes);
        List<string> wrong = [];
        HashSet<(int, int)> sizes = [];
        foreach (string theme in themes)
        {
            foreach (bool dark in (bool[])[true, false])
            {
                string file = FirstStartPictures.FileOf(theme, dark);
                if (!Exists(file))
                {
                    wrong.Add($"{file} is not carried, so {theme} is offered at the first start with no picture: take them with just first-start-shots");
                    continue;
                }
                byte[] bytes = Resource(file);
                if (bytes.Length < 25 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 8).SequenceEqual("WEBPVP8L"u8))
                {
                    wrong.Add($"{file} is no lossless WebP, so its words would blur");
                    continue;
                }
                // VP8L's header: a signature byte, then the width and height less one in 14 bits each.
                uint packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21, 4));
                (int width, int height) = ((int)(packed & 0x3FFF) + 1, (int)((packed >> 14) & 0x3FFF) + 1);
                sizes.Add((width, height));
                if (width < FirstStartPictures.Widest * FirstStartPictures.Sharpest)
                {
                    wrong.Add($"{file} is {width} pixels wide, too few for a tile {FirstStartPictures.Widest} wide on a screen of {FirstStartPictures.Sharpest} times");
                }
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.True(sizes.Count == 1, $"The pictures differ in size, so the tiles would too: {string.Join(", ", sizes.Select(s => $"{s.Item1}x{s.Item2}"))}");
    }

    /// <summary>The names of the themes the built-in source lists.</summary>
    private static IEnumerable<string> CarriedThemes()
    {
        ThemeRead<ThemeIndex> index = ThemeFiles.ReadIndex(ThemeFiles.IndexFile, Built(ThemeFiles.IndexFile));
        Assert.True(index.Read is not null, $"The built-in source's index does not read: {index.Refused}");
        foreach (string file in index.Read!.Themes)
        {
            ThemeRead<ThemeFile> theme = ThemeFiles.ReadTheme(file, Built(file), new Dictionary<string, ThemeTokenKind>());
            Assert.True(theme.Read is not null, $"{file} does not read: {theme.Refused}");
            yield return theme.Read!.Name;
        }
    }

    private static bool Exists(string file) => typeof(FirstStartPicturesTests).Assembly.GetManifestResourceInfo("first-start/" + file) is not null;

    private static byte[] Resource(string file) => Read("first-start/" + file);

    private static byte[] Built(string file) => Read("built-in/" + file);

    private static byte[] Read(string name)
    {
        using Stream stream = typeof(FirstStartPicturesTests).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"The tests carry no {name}");
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
