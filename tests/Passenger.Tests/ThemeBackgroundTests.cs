using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A theme's background picture as a person sees it: in each place it names (the editor, a tool window, the frame,
/// the Welcome screen) each ground pixel is the ground mixed with the picture at the theme's opacity, its tint washed over
/// the picture first, and nowhere else; and a key in a large file allocates on the UI thread what it does with no picture.</summary>
public sealed class ThemeBackgroundTests : IDisposable
{
    private const double Opacity = 0.3;
    private static readonly Color Picture = Color.FromRgb(200, 40, 40);
    private readonly string folder = TestFolders.Create("theme-background-");
    private readonly string settings = TestFolders.Create("theme-background-settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    [AvaloniaFact]
    public void A_picture_shows_over_each_place_it_names_at_the_themes_opacity_and_tint_and_nowhere_else()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow welcome = Open(null);
        MainWindow window = Open(folder);
        try
        {
            window.OpenFile(Path.Combine(folder, "One.cs"));
            Ui.Settle();
            Dictionary<string, Point> places = Places(window);
            places["the Welcome screen"] = Inside(welcome, BackgroundPlaces.Welcome);
            Dictionary<string, Color> own = Read(welcome, window, places);

            Wear(window, $"Over everything {Guid.NewGuid():N}", """{"image": "red.png", "opacity": 0.3, "places": ["editor", "toolWindows", "frame", "welcome"]}""");
            Expect(own, Read(welcome, window, places), Picture, "with no tint");

            Wear(window, $"Tinted {Guid.NewGuid():N}", """{"image": "red.png", "opacity": 0.3, "tint": {"colour": "#0000FF", "strength": 0.5}, "places": ["editor", "toolWindows", "frame", "welcome"]}""");
            Expect(own, Read(welcome, window, places), Color.FromRgb(100, 20, 148), "tinted half blue");

            Wear(window, $"Editor alone {Guid.NewGuid():N}", """{"image": "red.png", "opacity": 0.3}""");
            Dictionary<string, Color> alone = Read(welcome, window, places);
            Expect(new() { ["the editor"] = own["the editor"] }, new() { ["the editor"] = alone["the editor"] }, Picture, "named only for the editor");
            foreach (string place in places.Keys.Where(p => p != "the editor"))
            {
                Assert.True(Near(own[place], alone[place], 2), $"A picture named only for the editor drew over {place}: {own[place]} became {alone[place]}");
            }
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            welcome.Close();
        }
    }

    [AvaloniaFact]
    public void A_key_in_a_large_file_with_a_picture_shown_allocates_what_one_in_a_small_file_does_and_never_draws_the_picture_again()
    {
        StringBuilder text = new();
        for (int i = 0; i < 20_000; i++)
        {
            text.Append("    public int Value").Append(i).Append(" { get; set; } // the ").Append(i).Append("th of them\n");
        }
        string large = Path.Combine(folder, "Large.txt");
        string small = Path.Combine(folder, "Small.txt");
        File.WriteAllText(large, "class Large\n{\n" + text + "}\n");
        File.WriteAllText(small, "class Small\n{\n" + string.Concat(text.ToString().Split('\n').Take(200).Select(l => l + "\n")) + "}\n");
        MainWindow window = Open(folder);
        try
        {
            string pictured = $"Pictured {Guid.NewGuid():N}";
            Wear(window, pictured, """{"image": "red.png", "opacity": 0.3, "places": ["editor", "toolWindows", "frame", "welcome"]}""");
            (long Keyed, long Framed)[] least = [.. Enumerable.Repeat((long.MaxValue, long.MaxValue), 4)];
            // In turns, so what the process takes on meanwhile weighs on each alike.
            for (int round = 0; round < 3; round++)
            {
                window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, pictured) });
                least[0] = Least(least[0], Keys(window, small));
                least[1] = Least(least[1], Keys(window, large));
                window.ApplySettings(window.Settings with { Look = null });
                least[2] = Least(least[2], Keys(window, small));
                least[3] = Least(least[3], Keys(window, large));
            }
            (long smallWith, long largeWith, long smallWithout, long largeWithout) = (least[0].Framed, least[1].Framed, least[2].Framed, least[3].Framed);
            TestContext.Current.TestOutputHelper?.WriteLine($"A key and its frame allocated on the UI thread {smallWith:N0} bytes in a file of 200 lines with a picture, "
                + $"{largeWith:N0} in one of 20,000, and {smallWithout:N0} and {largeWithout:N0} with none");
            Assert.True(largeWith - largeWithout <= smallWith - smallWithout + 512,
                $"A picture added {largeWith - largeWithout:N0} bytes to a key in a file of 20,000 lines and {smallWith - smallWithout:N0} to one of 200: it costs by the file's size");
            // The compositor's share of a picture over what a key changes is a constant of its own.
            Assert.True(largeWith <= largeWithout + 2048,
                $"A key and its frame in a large file allocated {largeWith:N0} bytes on the UI thread with a picture shown, against {largeWithout:N0} with none");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>The least a key allocated on the UI thread in a file, of twelve, before its frame is drawn and with it;
    /// fails if a key drew a picture again, where nothing of it changed.</summary>
    private static (long Keyed, long Framed) Keys(MainWindow window, string file)
    {
        window.OpenFile(file);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.Length / 2);
        editor.Focus();
        Ui.Settle();
        ThemeBackdrop[] backdrops = [.. window.GetVisualDescendants().OfType<ThemeBackdrop>()];
        long drawn = backdrops.Sum(b => b.Draws);
        GC.Collect(1, GCCollectionMode.Forced, blocking: true);
        (long Keyed, long Framed) least = (long.MaxValue, long.MaxValue);
        for (int i = 0; i < 12; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput(i % 2 == 0 ? "x" : "y");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            long keyed = GC.GetAllocatedBytesForCurrentThread() - before;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            least = Least(least, (keyed, GC.GetAllocatedBytesForCurrentThread() - before));
            Ui.Settle();
        }
        Assert.True(backdrops.Sum(b => b.Draws) == drawn, $"A key in {Path.GetFileName(file)} drew the picture again, where nothing of it changed");
        return least;
    }

    private static (long, long) Least((long Keyed, long Framed) a, (long Keyed, long Framed) b) => (Math.Min(a.Keyed, b.Keyed), Math.Min(a.Framed, b.Framed));

    private MainWindow Open(string? open)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        if (open is not null)
        {
            window.OpenFolder(open);
        }
        Ui.Settle();
        return window;
    }

    /// <summary>A theme of a red picture and a background, kept as a file added is, put in force.</summary>
    private static void Wear(MainWindow window, string name, string background)
    {
        string json = $$"""{"format": 1, "name": "{{name}}", "background": {{background}}}""";
        Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, Encoding.UTF8.GetBytes(json),
            new Dictionary<string, byte[]> { ["red.png"] = TestPictures.Png(40, 30, Picture.R, Picture.G, Picture.B) }));
        window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
        Ui.Settle();
        Assert.NotNull(ThemeBackdrops.Of(window.ActualThemeVariant));
    }

    /// <summary>A point of plain ground in each place of a window with a folder and a short file open.</summary>
    private static Dictionary<string, Point> Places(MainWindow window)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        ThemeBackdrop[] frame = [.. window.GetVisualDescendants().OfType<ThemeBackdrop>().Where(b => b.Place == BackgroundPlaces.Frame && b.IsEffectivelyVisible && b.Bounds.Width > 0)];
        ThemeBackdrop status = frame.MaxBy(b => b.TranslatePoint(default, window)!.Value.Y)!;
        return new Dictionary<string, Point>
        {
            ["the editor"] = editor.TranslatePoint(new Point(editor.Bounds.Width * 0.6, editor.Bounds.Height - 30), window)!.Value,
            ["a tool window"] = Inside(window, BackgroundPlaces.ToolWindows),
            ["the frame's status bar"] = status.TranslatePoint(new Point(status.Bounds.Width * 0.55, status.Bounds.Height / 2), window)!.Value,
        };
    }

    /// <summary>A point low in the middle of a place's picture, away from what the place lists at its top.</summary>
    private static Point Inside(MainWindow window, BackgroundPlaces place)
    {
        ThemeBackdrop over = window.GetVisualDescendants().OfType<ThemeBackdrop>().First(b => b.Place == place && b.IsEffectivelyVisible && b.Bounds.Height > 0);
        return over.TranslatePoint(new Point(over.Bounds.Width / 2, over.Bounds.Height - 20), window)!.Value;
    }

    private static Dictionary<string, Color> Read(MainWindow welcome, MainWindow window, Dictionary<string, Point> places)
    {
        Ui.Settle();
        return places.ToDictionary(p => p.Key, p => Ui.Pixel(p.Key == "the Welcome screen" ? welcome : window, p.Value));
    }

    /// <summary>Fails unless each place's pixel is its own ground's mixed with a colour at the theme's opacity.</summary>
    private static void Expect(Dictionary<string, Color> own, Dictionary<string, Color> drawn, Color over, string how)
    {
        foreach ((string place, Color ground) in own)
        {
            Color expected = Color.FromRgb(Mix(ground.R, over.R), Mix(ground.G, over.G), Mix(ground.B, over.B));
            Assert.True(Near(expected, drawn[place], 3), $"The picture {how} over {place} drew {drawn[place]} on the ground {ground}, where at {Opacity} it is {expected}");
        }

        static byte Mix(byte ground, byte picture) => (byte)Math.Round((ground * (1 - Opacity)) + (picture * Opacity));
    }

    private static bool Near(Color a, Color b, int by) => Math.Abs(a.R - b.R) <= by && Math.Abs(a.G - b.G) <= by && Math.Abs(a.B - b.B) <= by;
}
