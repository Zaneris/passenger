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
        TestGit.Init(folder);
        // A change the Commit window lists.
        Write("package.json", "{ \"name\": \"shop\" }\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Each_kind_of_file_is_drawn_in_a_shape_and_colour_of_its_own_in_the_Explorer_the_tabs_and_the_Commit_window()
    {
        MainWindow window = Ui.OpenFolder(folder);
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
            Assert.True(Icons(window.EditorArea).Any(i => ReferenceEquals(i.Data, Resource("FileIcon.CSharp")) && ReferenceEquals(i.Foreground, Resource(TokenKeys.FileIcon_Blue))),
                "The open C# file's tab is not drawn with the C# icon");

            Ui.Landed(() => window.Vcs is { } vcs && vcs.Changes.Any(c => c.Name == "package.json"), "package.json listed as changed");
            window.DockHost.Show(ToolWindowIds.Commit);
            Ui.Landed(() => Icons(window.CommitView).Any(i => ReferenceEquals(i.Data, Resource("FileIcon.Npm")) && ReferenceEquals(i.Foreground, Resource(TokenKeys.FileIcon_Red))),
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
    [AvaloniaFact]
    public void A_rows_icon_draws_every_icon_of_the_theme_pixel_for_pixel_as_a_PathIcon_draws_it()
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
                PathIcon chevron = new() { Classes = { StyleClasses.Chevron }, RenderTransformOrigin = RelativePoint.Center, RenderTransform = new RotateTransform(icon.Bounds.Width > 15 ? 90 : 0) };
                chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(IconKeys.ChevronRight));
                return new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Height = 24,
                    Children = { new Border { Width = 18, Height = 24, Background = Brushes.Transparent, Child = chevron }, new PathIcon { Data = icon, Margin = new Thickness(0, 0, 6, 0) } },
                };
            }
            PathIcon drawn = way is 2 or 3 ? new() { Classes = { StyleClasses.Chevron } } : new();
            drawn.Data = icon;
            if (way == 1)
            {
                drawn.Width = drawn.Height = 14;
                drawn.Foreground = given;
            }
            if (way == 3)
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
            RowIcon drawn = way is 2 or 3 ? RowIcon.Chevron() : new();
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
        return (glyphs.Icon, glyphs.IconBrush);
    }

    private static object Resource(string key)
    {
        Application app = Application.Current!;
        return app.TryFindResource(key, app.ActualThemeVariant, out object? value) && value is not null ? value : throw new InvalidOperationException($"The theme has no {key}");
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
