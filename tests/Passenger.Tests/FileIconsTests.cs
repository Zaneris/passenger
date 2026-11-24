using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Passenger's file icons: a shape and colour of its own for each language, tool and kind of file, drawn in the
/// Explorer, the editor's tabs and the file lists, a plain folder as a folder.</summary>
public sealed class FileIconsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("file-icons-");

    public FileIconsTests()
    {
        Write("Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("LicenseChecker.cs", "class LicenseChecker;\n");
        Write("ReadmeView.cs", "class ReadmeView;\n");
        Write("Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("package.json", "{}\n");
        Write("Dockerfile", "FROM scratch\n");
        Write(".gitignore", "bin/\n");
        Write("README.md", "# Shop\n");
        Write("LICENSE-MIT", "MIT\n");
        Write("appsettings.json", "{}\n");
        Write("build.ps1", "Write-Host build\n");
        Write("run.sh", "echo run\n");
        Write("main.go", "package main\n");
        Write("logo.png", "");
        Write("app.test.ts", "");
        Write("notes.unknown", "");
        Write(".github/dependabot.yml", "version: 2\n");
        Write("src/Order.cs", "class Order;\n");
        Write("assets/logo.txt", "Shop\n");
        TestGit.Init(folder);
        // A change the Commit window lists.
        Write("package.json", "{ \"name\": \"shop\" }\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Each kind of file is drawn in a shape and colour of its own in the Explorer, the tabs and the Commit
    /// window, a row's icon pixel for pixel as a PathIcon draws it, as one flow, each step the check one test made in
    /// an instance of its own.</summary>
    [AvaloniaFact]
    public void Each_kind_of_file_is_drawn_in_its_own_icon_as_a_PathIcon_draws_it()
    {
        Flow.StepApart<FileIconsTests>("each kind of file is drawn in a shape and colour of its own in the Explorer the tabs and the Commit window", t => t.Each_kind_of_file_is_drawn_in_a_shape_and_colour_of_its_own_in_the_Explorer_the_tabs_and_the_Commit_window());
        Flow.StepApart<FileIconsTests>("a rows icon draws every icon of the theme pixel for pixel as a PathIcon draws it", t => t.A_rows_icon_draws_every_icon_of_the_theme_pixel_for_pixel_as_a_PathIcon_draws_it());
        Flow.StepApart<FileIconsTests>("a themes icon saturation brightness and tint reach every icon", t => t.A_themes_icon_saturation_brightness_and_tint_reach_every_icon());
        Flow.StepApart<FileIconsTests>("a themes own icons replace those it names and leave the rest", t => t.A_themes_own_icons_replace_those_it_names_and_leave_the_rest());
        Flow.StepApart<FileIconsTests>("a plain folder shows its chevron alone while Settings says so", t => t.A_plain_folder_shows_its_chevron_alone_while_Settings_says_so());
        Flow.StepApart<FileIconsTests>("a themes file icon theme draws the files and folders it names and leaves the rest to Passenger", t => t.A_themes_file_icon_theme_draws_the_files_and_folders_it_names_and_leaves_the_rest_to_Passenger());
    }

    /// <summary>A theme's own icons, SVGs kept beside its file, replace in the Explorer those it names as it is chosen, each in
    /// its own colours or, where it says currentColor, in the colour Passenger draws that icon in; the icons it names none for
    /// stay Passenger's, and choosing Passenger's own theme brings Passenger's back.</summary>
    private void A_themes_own_icons_replace_those_it_names_and_leave_the_rest()
    {
        string name = $"Own icons {Guid.NewGuid():N}";
        byte[] green = System.Text.Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><rect width="16" height="16" fill="#00C000"/></svg>""");
        byte[] inked = System.Text.Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect width="24" height="24" fill="currentColor"/></svg>""");
        string json = "{\"format\": 1, \"name\": \"" + name + "\", \"icons\": {\"FileIcon.CSharp\": \"icons/cs.svg\", \"FileIcon.Npm\": {\"dark\": \"icons/ink.svg\", \"light\": \"icons/ink.svg\"}}}";
        Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, System.Text.Encoding.UTF8.GetBytes(json),
            new Dictionary<string, byte[]> { ["icons/cs.svg"] = green, ["icons/ink.svg"] = inked }));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Geometry passengers = (Geometry)Resource("FileIcon.CSharp");
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            Assert.True(Themes.Said is null, $"The theme of own icons was not laid: {Themes.Said}");
            Assert.True(Drawn(window, "Program.cs").Data is IconPicture { Name: "icons/cs.svg" }, "The theme's own C# icon is not drawn on Program.cs's row");
            Assert.True(Drawn(window, "Dockerfile").Data is { } docker && docker is not IconPicture, "An icon the theme gives none for is no longer Passenger's");
            Color red = ((ISolidColorBrush)Resource(TokenKeys.FileIcon_Red)).Color;
            using (WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window drew no frame"))
            {
                foreach ((string file, Color expected) in ((string, Color)[])[("Program.cs", Color.Parse("#00C000")), ("package.json", red)])
                {
                    byte[] drawn = Pixels(frame, IconMiddle(window, file), window.RenderScaling, 1);
                    Assert.True(Math.Abs(drawn[0] - expected.R) <= 1 && Math.Abs(drawn[1] - expected.G) <= 1 && Math.Abs(drawn[2] - expected.B) <= 1,
                        $"{file}'s row draws #{drawn[0]:X2}{drawn[1]:X2}{drawn[2]:X2} in its icon's middle, where the theme's own icon draws {expected}");
                }
            }
            window.ApplySettings(window.Settings with { Look = null });
            Ui.Settle();
            Assert.True(ReferenceEquals(passengers, Drawn(window, "Program.cs").Data), "Passenger's own C# icon did not come back with Passenger's own theme");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A theme's VS Code file icon theme, its JSON and SVGs kept beside it, draws in the Explorer and the tabs each
    /// file and folder it names, by its name, an extension or its language, and the plain files and folders in its own file
    /// and folder icons, while one Passenger draws apart that it names nothing for keeps Passenger's; with plain folders'
    /// icons off, the folder it names keeps its icon and a plain one draws none; Passenger's own theme brings Passenger's back.</summary>
    private void A_themes_file_icon_theme_draws_the_files_and_folders_it_names_and_leaves_the_rest_to_Passenger()
    {
        string name = $"Icon theme {Guid.NewGuid():N}";
        static string Square(string colour) => $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><rect width="16" height="16" fill="{colour}"/></svg>""";
        Dictionary<string, string> beside = new(StringComparer.Ordinal)
        {
            ["icons/theme.json"] = """
                {
                  "iconDefinitions": {
                    "file": {"iconPath": "./svg/file.svg"}, "folder": {"iconPath": "./svg/folder.svg"}, "open": {"iconPath": "./svg/open.svg"},
                    "csharp": {"iconPath": "./svg/csharp.svg"}, "npm": {"iconPath": "./svg/npm.svg"}, "markdown": {"iconPath": "./svg/markdown.svg"},
                    "src": {"iconPath": "./svg/src.svg"}
                  },
                  "file": "file", "folder": "folder", "folderExpanded": "open",
                  "fileNames": {"package.json": "npm"}, "fileExtensions": {"cs": "csharp"}, "languageIds": {"markdown": "markdown"},
                  "folderNames": {"src": "src"}
                }
                """,
        };
        foreach ((string svg, string colour) in ((string, string)[])[("file", "#808080"), ("folder", "#A0A040"), ("open", "#A0A060"), ("csharp", "#00C000"), ("npm", "#C00000"),
            ("markdown", "#4040C0"), ("src", "#40C0C0")])
        {
            beside[$"icons/svg/{svg}.svg"] = Square(colour);
        }
        byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"format\": 1, \"name\": \"" + name + "\", \"iconTheme\": {\"file\": \"icons/theme.json\"}}");
        ThemeFile theme = ThemeFiles.ReadTheme(name, json, ThemeResources.Kinds).Read ?? throw new InvalidOperationException("The theme naming its file icon theme is refused");
        (Dictionary<string, byte[]> files, string? unread) = Ui.Await(VsCodeIconSets.BesideAsync(theme, path =>
            Task.FromResult<(byte[]?, string?)>(beside.TryGetValue(path, out string? text) ? (System.Text.Encoding.UTF8.GetBytes(text), null) : (null, $"no {path}"))), "the icon theme read beside its theme");
        Assert.True(unread is null, unread);
        Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, json, files));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(folder, "Program.cs"));
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            Assert.True(Themes.Said is null, $"The theme of a file icon theme was not laid: {Themes.Said}");
            foreach ((string row, string? svg) in ((string, string?)[])[("Program.cs", "csharp"), ("package.json", "npm"), ("README.md", "markdown"), ("notes.unknown", "file"),
                ("src", "src"), ("assets", "folder"), ("Dockerfile", null), (".github", null)])
            {
                Geometry? drawn = Drawn(window, row).Data;
                Assert.True(svg is null ? drawn is not null and not IconPicture : drawn is IconPicture { Name: var shown } && shown == $"icons/svg/{svg}.svg",
                    $"The Explorer draws {row} with {(drawn as IconPicture)?.Name ?? "Passenger's own icon"}, where {(svg is null ? "Passenger's own" : $"the icon theme's {svg}")} was its icon");
            }
            Assert.True(Icons(window.EditorArea).Any(i => i.DataKey is { } key && window.TryFindResource(key, window.ActualThemeVariant, out object? icon) && icon is IconPicture { Name: "icons/svg/csharp.svg" }),
                "The open C# file's tab is not drawn with the icon theme's C# icon");
            window.ApplySettings(window.Settings with { PlainFolderIcons = false });
            Ui.Settle();
            Assert.True(Drawn(window, "src").Data is IconPicture, "src, which the icon theme draws by its name, lost its icon with plain folders' icons off");
            Assert.True(Inked(window, "assets") == 0, $"The plain folder assets still draws {Inked(window, "assets")} pixels of the icon theme's folder with plain folders' icons off");
            window.ApplySettings(window.Settings with { Look = null, PlainFolderIcons = true });
            Ui.Settle();
            Assert.True(ReferenceEquals(Resource("FileIcon.CSharp"), Drawn(window, "Program.cs").Data), "Passenger's own C# icon did not come back with Passenger's own theme");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>With Settings' Show icons on plain folders off, a plain folder's row draws its chevron alone, its icon's place
    /// left empty so its name stays in line with its files', while a folder Passenger draws apart keeps its icon; on again,
    /// the plain folder's icon comes back.</summary>
    private void A_plain_folder_shows_its_chevron_alone_while_Settings_says_so()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Assert.True(Inked(window, "src") > 0 && Inked(window, ".github") > 0, "A folder's row draws no icon with Settings' plain folders' icons on");
            window.ApplySettings(window.Settings with { PlainFolderIcons = false });
            Ui.Settle();
            Assert.True(Inked(window, "src") == 0, $"The plain folder src still draws {Inked(window, "src")} pixels of an icon with Settings' plain folders' icons off");
            Assert.True(Inked(window, ".github") > 0, ".github, which Passenger draws apart, lost its icon with plain folders' icons off");
            window.ApplySettings(window.Settings with { PlainFolderIcons = true });
            Ui.Settle();
            Assert.True(Inked(window, "src") > 0, "The plain folder src did not draw its icon again once Settings showed plain folders' icons");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>How many pixels of the lower half of a row's icon's place differ from the row's ground at its far end.</summary>
    private static int Inked(MainWindow window, string name)
    {
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window drew no frame");
        Point middle = IconMiddle(window, name);
        double scale = window.RenderScaling;
        Point end = window.ExplorerView.TranslatePoint(new Point(window.ExplorerView.Bounds.Width - 24, 0), window) ?? throw new InvalidOperationException("The Explorer is not laid out");
        byte[] ground = Pixels(frame, new Point(end.X, middle.Y), scale, 1);
        byte[] place = Pixels(frame, middle - new Point(RowIcon.Size / 2, 0), scale, RowIcon.Size);
        int width = PixelSize.FromSize(new Size(RowIcon.Size, Cell), scale).Width;
        int rows = (int)(RowIcon.Size / 2 * scale);
        return Enumerable.Range(0, width * rows).Count(i => !place.AsSpan(i * 4, 3).SequenceEqual(ground.AsSpan(0, 3)));
    }

    /// <summary>The middle of the icon a row of the Explorer draws, in the window.</summary>
    private static Point IconMiddle(MainWindow window, string name)
    {
        ExplorerNode row = window.ExplorerView.Tree.Rows.First(r => r.Name == name);
        RowGlyphs glyphs = window.ExplorerView.GetVisualDescendants().OfType<RowGlyphs>().First(i => ReferenceEquals(i.DataContext, row));
        return glyphs.TranslatePoint(new Point(RowExpander.Column + (RowIcon.Size / 2), ListRow.Height / 2), window) ?? throw new InvalidOperationException("The Explorer's rows are not laid out");
    }

    /// <summary>A theme's icon saturation, brightness and tint reach every icon however it is drawn: a row's icon in a file's
    /// colour, a tree row's chevron and icon, and a PathIcon in a button, each in its colour as the look draws it, dark and
    /// light; and Passenger's own theme draws each as it is.</summary>
    private void A_themes_icon_saturation_brightness_and_tint_reach_every_icon()
    {
        Geometry square = Geometry.Parse("M0,0 H16 V16 H0 Z");
        RowIcon row = new() { Data = square, ForegroundKey = TokenKeys.FileIcon_Red };
        RowGlyphs glyphs = new() { HasChevron = false, Icon = square, IconBrush = (IBrush)Resource(TokenKeys.FileIcon_Blue) };
        PathIcon button = new() { Data = square };
        StackPanel icons = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8), Children = { row, glyphs, new Button { Content = button } } };
        Dictionary<string, TokenValue> look = new(StringComparer.Ordinal)
        {
            [IconLook.SaturationToken] = new NumberValue(0.3),
            [IconLook.BrightnessToken] = new NumberValue(0.8),
            [IconLook.TintToken] = new ColourValue(0xFFE0B070, 0.25),
        };
        IconLook muted = new(0.3, 0.8, 0x40E0B070);
        foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
        {
            Window window = new() { Width = 200, Height = 60, Content = icons, RequestedThemeVariant = variant };
            window.Show();
            try
            {
                foreach (bool worn in (bool[])[false, true])
                {
                    Themes.Wear(Application.Current!, worn ? new ThemeFile("Muted icons", string.Empty, look, new Dictionary<string, TokenValue>(), new Dictionary<string, TokenValue>()) : null, null);
                    Ui.Settle();
                    using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window drew no frame");
                    foreach ((Control icon, string key) in ((Control, string)[])[(row, TokenKeys.FileIcon_Red), (glyphs, TokenKeys.FileIcon_Blue), (button, TokenKeys.Icon)])
                    {
                        Color colour = icon is RowGlyphs { IconBrush: ISolidColorBrush given } ? given.Color
                            : Application.Current!.TryGetResource(key, variant, out object? found) && found is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException($"The theme has no {key}");
                        Color expected = worn ? muted.Apply(colour) : colour;
                        Point middle = icon is RowGlyphs ? new Point(RowExpander.Column + 7, 12) : new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2);
                        Point at = icon.TranslatePoint(middle, window) ?? throw new InvalidOperationException("The icons are not laid out");
                        byte[] drawn = Pixels(frame, at, 1, 1);
                        Assert.True(Math.Abs(drawn[0] - expected.R) <= 1 && Math.Abs(drawn[1] - expected.G) <= 1 && Math.Abs(drawn[2] - expected.B) <= 1,
                            $"A {icon.GetType().Name} in {key} is drawn #{drawn[0]:X2}{drawn[1]:X2}{drawn[2]:X2} under {variant} {(worn ? "with the theme's muted icons" : "in Passenger's own theme")}, where {expected} was the colour");
                    }
                }
            }
            finally
            {
                Themes.Wear(Application.Current!, null, null);
                window.Content = null;
                window.Close();
            }
        }
    }

    private void Each_kind_of_file_is_drawn_in_a_shape_and_colour_of_its_own_in_the_Explorer_the_tabs_and_the_Commit_window()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(folder, "Program.cs"));
            Ui.Settle();
            (string Name, string Shape, string? Colour)[] expected =
            [
                ("Program.cs", "CSharp", "Blue"),
                ("LicenseChecker.cs", "CSharp", "Blue"),
                ("ReadmeView.cs", "CSharp", "Blue"),
                ("Shop.csproj", "Stack", "Blue"),
                ("package.json", "Npm", "Red"),
                ("Dockerfile", "Docker", "Blue"),
                (".gitignore", "Git", "Red"),
                ("README.md", "Book", "Blue"),
                ("LICENSE-MIT", "Law", "Blue"),
                ("appsettings.json", "Json", "Cyan"),
                ("build.ps1", "PowerShell", "Blue"),
                ("run.sh", "Terminal", "Purple"),
                ("main.go", "Go", "Blue"),
                ("logo.png", "Image", "Orange"),
                ("app.test.ts", "TestTypeScript", "Blue"),
                ("notes.unknown", "File", null),
                (".github", "GitHub", null),
            ];
            foreach ((string name, string shape, string? colour) in expected)
            {
                (Geometry? Data, IBrush? Foreground) icon = Drawn(window, name);
                Assert.True(ReferenceEquals(Resource($"FileIcon.{shape}"), icon.Data), $"The Explorer does not draw {name} as its {shape}");
                Assert.True(ReferenceEquals(Resource(colour is null ? TokenKeys.File_Plain : $"Passenger.FileIcon.{colour}"), icon.Foreground),
                    $"The Explorer does not draw {name} in the {colour ?? "plain"} file colour");
            }
            Assert.True(ReferenceEquals(Resource(IconKeys.Explorer), Drawn(window, "src").Data), "A plain folder lost the folder icon");
            Assert.True(Icons(window.EditorArea).Any(i => i.DataKey == FileIcons.FileKey(Path.Combine(folder, "Program.cs")) && ReferenceEquals(Resource(i.DataKey), Resource("FileIcon.CSharp"))
                && i.ForegroundKey == TokenKeys.FileIcon_Blue), "The open C# file's tab is not drawn with the C# icon");

            Ui.Landed(() => window.Vcs is { } vcs && vcs.Changes.Any(c => c.Name == "package.json"), "package.json listed as changed");
            window.DockHost.Show(ToolWindowIds.Commit);
            Ui.Landed(() => Icons(window.CommitView).Any(i => i.DataKey == FileIcons.FileKey(Path.Combine(folder, "package.json")) && ReferenceEquals(Resource(i.DataKey), Resource("FileIcon.Npm"))
                && i.ForegroundKey == TokenKeys.FileIcon_Red),
                "the Commit window's package.json drawn with npm's icon");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A row draws its icons with RowIcon, one control where a PathIcon is five: every icon of the theme and every
    /// file icon, at 16 and 14 and as a 10-square chevron turned and not, draws pixel for pixel as a PathIcon in its place
    /// draws it, at scale 1, 1.5 and 2, dark and light, in the theme's icon colour and in a brush given; and the Explorer's
    /// RowGlyphs as its chevron's column and icon did, each a PathIcon.</summary>
    private void A_rows_icon_draws_every_icon_of_the_theme_pixel_for_pixel_as_a_PathIcon_draws_it()
    {
        Application app = Application.Current!;
        List<(string Key, Geometry Icon)> icons = [];
        foreach (string key in typeof(IconKeys).GetFields().Select(f => (string)f.GetValue(null)!).Concat(FileIconKeys(app.Resources)).Distinct())
        {
            if (app.TryFindResource(key, ThemeVariant.Dark, out object? value) && value is Geometry icon)
            {
                icons.Add((key, icon));
            }
        }
        Assert.True(icons.Count > 200, $"Only {icons.Count} icons were found in the theme: the search no longer reads it");
        List<string> differ = [];
        IBrush given = new SolidColorBrush(Color.FromRgb(200, 120, 60));
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                // Each icon in the five Ways along its line, each way's PathIcon and RowIcon in one place, shown in turn.
                Canvas canvas = new() { Width = (7 * Cell) + 8, Height = icons.Count * Cell };
                List<(Control Path, Control Row)> drawn = [];
                for (int i = 0; i < icons.Count; i++)
                {
                    for (int way = 0; way < Ways.Length; way++)
                    {
                        Point at = Where(i, way);
                        drawn.Add((Place(Pathed(icons[i].Icon, way, given), at), Place(Rowed(icons[i].Icon, way, given), at)));
                        canvas.Children.Add(drawn[^1].Path);
                        canvas.Children.Add(drawn[^1].Row);
                    }
                }
                ScrollViewer scroller = new() { Content = canvas };
                Window window = new() { Width = (7 * Cell) + 60, Height = 20 * Cell, Content = scroller, RequestedThemeVariant = theme };
                window.Show();
                window.SetRenderScaling(scale);
                for (int first = 0; first < icons.Count; first += 20)
                {
                    scroller.Offset = new Vector(0, first * Cell);
                    int last = Math.Min(icons.Count, first + 20);
                    List<byte[]> paths = Frame(drawn, rowsShown: false, first, last);
                    List<byte[]> rows = Frame(drawn, rowsShown: true, first, last);
                    for (int cell = 0; cell < paths.Count; cell++)
                    {
                        int most = paths[cell].Zip(rows[cell], (a, b) => Math.Abs(a - b)).Max();
                        if (most > 0)
                        {
                            (int i, int way) = Math.DivRem((first * Ways.Length) + cell, Ways.Length);
                            differ.Add($"{icons[i].Key} ({Ways[way]}) at scale {scale}, {theme}: {paths[cell].Zip(rows[cell]).Count(p => p.First != p.Second)} bytes differ, by up to {most}");
                        }
                    }
                }
                window.Close();

                // Each way's cell of the icons from first to last, the PathIcons shown or the RowIcons.
                List<byte[]> Frame(List<(Control Path, Control Row)> all, bool rowsShown, int first, int last)
                {
                    foreach ((Control path, Control row) in all)
                    {
                        path.IsVisible = !rowsShown;
                        row.IsVisible = rowsShown;
                    }
                    Ui.Settle();
                    foreach (Visual visual in window.GetVisualDescendants())
                    {
                        visual.InvalidateVisual();
                    }
                    Ui.Settle();
                    using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window drew no frame");
                    List<byte[]> cells = [];
                    for (int i = first; i < last; i++)
                    {
                        for (int way = 0; way < Ways.Length; way++)
                        {
                            Point at = canvas.TranslatePoint(Where(i, way), window) ?? throw new InvalidOperationException("The icons are not laid out");
                            cells.Add(Pixels(frame, at, scale, way == 5 ? 2 * Cell : Cell));
                        }
                    }
                    return cells;
                }
            }
        }
        Assert.True(differ.Count == 0, $"{differ.Count} icons a row draws differ from the PathIcon they stand for:\n{string.Join("\n", differ.Take(40))}");

        static Point Where(int icon, int way) => new((way * Cell) + (way == 4 ? 0.5 : 0) + (way == 5 ? 4 : 2), icon * Cell);

        static Control Place(Control icon, Point at)
        {
            Canvas.SetLeft(icon, at.X);
            Canvas.SetTop(icon, at.Y);
            return icon;
        }

        static Control Pathed(Geometry icon, int way, IBrush given)
        {
            if (way == 5)
            {
                return new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Height = 24,
                    Children =
                    {
                        new Border { Width = RowExpander.Column, Height = 24, Background = Brushes.Transparent, Child = Fluent(null, RowIcon.GlyphSize, null, turned: icon.Bounds.Width > 15) },
                        Fluent(icon, RowIcon.Size, null, margin: new Thickness(0, 0, 6, 0)),
                    },
                };
            }
            return way switch
            {
                1 => Fluent(icon, 14, given),
                2 or 3 => Fluent(icon, RowIcon.GlyphSize, null, turned: way == 3),
                _ => Fluent(icon, 16, null),
            };
        }

        // Avalonia's own PathIcon whole, its template a Path in a Viewbox in a Border, in the theme's icon colour where no brush is given.
        static Control Fluent(Geometry? icon, double size, IBrush? given, bool turned = false, Thickness margin = default)
        {
            Avalonia.Controls.Shapes.Path path = new() { Data = icon, Stretch = Stretch.Uniform };
            if (icon is null)
            {
                path.Bind(Avalonia.Controls.Shapes.Path.DataProperty, path.GetResourceObservable(IconKeys.ChevronRight));
            }
            if (given is null)
            {
                path.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, path.GetResourceObservable(TokenKeys.Icon));
            }
            else
            {
                path.Fill = given;
            }
            Border drawn = new() { Width = size, Height = size, Margin = margin, Child = new Viewbox { Width = size, Height = size, Child = path } };
            if (turned)
            {
                drawn.RenderTransformOrigin = RelativePoint.Center;
                drawn.RenderTransform = new RotateTransform(90);
            }
            return drawn;
        }

        static Control Rowed(Geometry icon, int way, IBrush given)
        {
            if (way == 5)
            {
                return new RowGlyphs { HasChevron = true, Turned = icon.Bounds.Width > 15, Icon = icon };
            }
            RowIcon drawn = way is 2 or 3 ? RowIcon.Chevron() : new() { Width = 16, Height = 16 };
            drawn.Data = icon;
            if (way == 1)
            {
                drawn.Width = drawn.Height = 14;
                drawn.Foreground = given;
            }
            drawn.Turned = way == 3;
            return drawn;
        }
    }

    /// <summary>How each icon is drawn: 16 in the icon colour, 14 in a brush given, a chevron, a chevron turned down, 16 half
    /// a unit along, and after a tree row's chevron (turned for the wider icons).</summary>
    private static readonly string[] Ways = ["16", "14 in a brush given", "chevron", "chevron turned", "16 at half a unit", "in a tree row"];

    /// <summary>The keys of the file icons the theme holds.</summary>
    private static IEnumerable<string> FileIconKeys(IResourceDictionary resources) =>
        resources.Keys.OfType<string>().Where(k => k.StartsWith("FileIcon.", StringComparison.Ordinal))
            .Concat(resources.MergedDictionaries.OfType<IResourceDictionary>().SelectMany(FileIconKeys))
            .Concat(resources.MergedDictionaries.OfType<Avalonia.Markup.Xaml.Styling.ResourceInclude>().Select(i => i.Loaded).OfType<IResourceDictionary>().SelectMany(FileIconKeys));

    /// <summary>A cell's pixels at a place in logical units.</summary>
    private static byte[] Pixels(WriteableBitmap frame, Point at, double scale, double width)
    {
        PixelRect area = new(PixelPoint.FromPoint(at, scale), PixelSize.FromSize(new Size(width, Cell), scale));
        byte[] pixels = new byte[area.Width * area.Height * 4];
        System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(area, pinned.AddrOfPinnedObject(), pixels.Length, area.Width * 4);
        }
        finally
        {
            pinned.Free();
        }
        return pixels;
    }

    private const double Cell = 24;

    private static IEnumerable<RowIcon> Icons(Visual area) => area.GetVisualDescendants().OfType<RowIcon>();

    /// <summary>The icon the Explorer draws on a row, and its brush.</summary>
    private static (Geometry? Data, IBrush? Foreground) Drawn(MainWindow window, string name)
    {
        ExplorerNode row = window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Name == name)
            ?? throw new InvalidOperationException($"The Explorer shows no {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
        RowGlyphs glyphs = window.ExplorerView.GetVisualDescendants().OfType<RowGlyphs>().FirstOrDefault(i => ReferenceEquals(i.DataContext, row))
            ?? throw new InvalidOperationException($"The Explorer draws no icon on {name}'s row");
        return (glyphs.Icon ?? (glyphs.IconKey is { } key ? Resource(key) as Geometry : null), glyphs.IconBrush ?? (glyphs.IconBrushKey is { } tint ? Resource(tint) as IBrush : null));
    }

    private static object Resource(string key)
    {
        Application app = Application.Current!;
        return app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is not null ? value : throw new InvalidOperationException($"The theme has no {key}");
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
