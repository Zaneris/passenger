using System.Buffers.Binary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>Local images and fonts through ordinary file opening, read-only actions and the editor tab lifecycle.</summary>
public sealed class AssetPreviewTests : IDisposable
{
    private readonly string folder = TestFolders.Create("assets-");
    private readonly string settings = TestFolders.Create("asset-settings-");

    /// <inheritdoc/>
    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>An image or a font opens as what it shows in a read-only tab: a PNG, a WebP and an icon with their
    /// pixels and size, zoomed to its actual size and to fit, a local font in its own face, and the preview tabs split,
    /// move, close, reopen and come back with the workspace, an image reloaded in both splits.</summary>
    [AvaloniaFact]
    public void An_image_or_a_font_opens_as_its_picture_in_a_read_only_tab()
    {
        Flow.StepApart<AssetPreviewTests>("a PNG opens showing its pixels and size in a read-only tab", t => t.Opening_a_common_image_shows_its_pixels_and_dimensions_in_a_read_only_tab(".PNG", 64, 32));
        Flow.StepApart<AssetPreviewTests>("a WebP opens showing its pixels and size in a read-only tab", t => t.Opening_a_common_image_shows_its_pixels_and_dimensions_in_a_read_only_tab(".webp", 64, 32));
        Flow.StepApart<AssetPreviewTests>("an icon opens showing its pixels and size in a read-only tab", t => t.Opening_a_common_image_shows_its_pixels_and_dimensions_in_a_read_only_tab(".ico", 64, 32));
        Flow.StepApart<AssetPreviewTests>("an image preview zooms in to its actual size and to fit drawing its pixels as they are", t => t.An_image_preview_zooms_in_to_its_actual_size_and_to_fit_drawing_its_pixels_as_they_are());
        Flow.StepApart<AssetPreviewTests>("a local font shows its actual face letters digits and every ASCII symbol", t => t.A_local_font_shows_its_actual_face_letters_digits_and_every_ASCII_symbol(".ttf", "JetBrainsMono-Regular.ttf", FontStyle.Normal));
        Flow.StepApart<AssetPreviewTests>("preview tabs split move close reopen and return with the workspace", t => t.Preview_tabs_split_move_close_reopen_and_return_with_the_workspace());
        Flow.StepApart<AssetPreviewTests>("reloading an image updates both splits but invalid pixel data keeps the previous picture and explains why", t => t.Reloading_an_image_updates_both_splits_but_invalid_pixel_data_keeps_the_previous_picture_and_explains_why());
    }

    /// <summary>An SVG is drawn beside its text, sharp at every zoom and following its text edited, an image it names
    /// relative to it drawn from its folder but none from outside it or the web, saying why, and one nested past what
    /// is drawn says so and ends nothing.</summary>
    [AvaloniaFact]
    public void An_SVG_is_drawn_beside_its_text_from_its_own_folder_alone_and_ends_nothing()
    {
        Flow.StepApart<AssetPreviewTests>("an SVG is drawn beside its text sharp at every zoom and its picture follows its text edited", t => t.An_SVG_is_drawn_beside_its_text_sharp_at_every_zoom_and_its_picture_follows_its_text_edited());
        Flow.StepApart<AssetPreviewTests>("an SVG's image named relative to it is drawn from its folder", t => t.An_SVGs_image_named_relative_to_it_is_drawn_from_its_folder());
        Flow.StepApart<AssetPreviewTests>("an SVG draws no image from outside its folder or the web and says why", t => t.An_SVG_draws_no_image_from_outside_its_folder_or_the_web_and_says_why());
        Flow.StepApart<AssetPreviewTests>("an SVG of groups nested past what is drawn says so and ends nothing", t => t.An_SVG_nested_past_what_is_drawn_says_so_and_ends_nothing("<g>", "</g>", "<rect width=\"5\" height=\"5\"/>"));
        Flow.StepApart<AssetPreviewTests>("an SVG of SVGs nested past what is drawn says so and ends nothing", t => t.An_SVG_nested_past_what_is_drawn_says_so_and_ends_nothing("<svg>", "</svg>", ""));
        Flow.StepApart<AssetPreviewTests>("an SVG of styles nested past what is drawn says so and ends nothing", t => t.An_SVG_nested_past_what_is_drawn_says_so_and_ends_nothing("<style>", "</style>", ""));
    }

    /// <summary>A preview writes nothing and never holds the window: typing, pasting, saving and the encoding actions
    /// never write it or the text tab it replaced, nothing asks it for text it has not, invalid, empty, missing,
    /// unsupported and oversized assets fail without opening binary text, one whose decode fails before its open
    /// returns is refused at once, and a large image decodes off the window's thread.</summary>
    [AvaloniaFact]
    public async Task A_preview_writes_nothing_and_never_holds_or_hangs_the_window()
    {
        await Flow.StepApartAsync("typing pasting saving and encoding actions never write a preview or the text tab it replaced", () => new AssetPreviewTests(), t => t.Typing_pasting_saving_and_encoding_actions_never_write_a_preview_or_the_text_tab_it_replaced());
        Flow.StepApart<AssetPreviewTests>("a preview has no text for the status bar an agent find and Replace in Files or a bookmark", t => t.A_preview_has_no_text_for_the_status_bar_an_agent_find_and_replace_in_files_or_a_bookmark());
        Flow.StepApart<AssetPreviewTests>("invalid empty missing unsupported and oversized assets fail without opening binary text", t => t.Invalid_empty_missing_unsupported_and_oversized_assets_fail_without_opening_binary_text());
        Flow.StepApart<AssetPreviewTests>("an asset whose decode fails before its open returns is refused at once and never left loading", t => t.An_asset_whose_decode_fails_before_its_open_returns_is_refused_at_once_and_never_left_loading());
        Flow.StepApart<AssetPreviewTests>("a large image decodes off the UI thread its tab saying it is loading until the picture lands", t => t.A_large_image_decodes_off_the_UI_thread_its_tab_saying_it_is_loading_until_the_picture_lands());
    }

    private void Opening_a_common_image_shows_its_pixels_and_dimensions_in_a_read_only_tab(string extension, int width, int height)
    {
        string file = "picture" + extension;
        File.WriteAllBytes(Path.Combine(folder, file), ImageBytes(extension));
        MainWindow window = Open();
        Assert.Null(window.OpenFile(file));
        Ui.Settle();

        OpenDocument document = Assert.IsType<OpenDocument>(window.EditorArea.Active);
        Assert.True(document.IsReadOnly);
        Assert.False(window.EditorArea.CodeEditor.IsVisible);
        Assert.Empty(document.Text.Snapshot.ToString());
        Assert.Equal(new PixelSize(width, height), document.Asset!.Bitmap!.PixelSize);
        AssetPreviewControl preview = Preview(window.EditorArea.ActiveGroup);
        Image image = Assert.Single(preview.GetVisualDescendants().OfType<Image>());
        Assert.Same(document.Asset.Bitmap, image.Source);
        Assert.True(image.IsEffectivelyVisible && image.Bounds.Width > 0 && image.Bounds.Height > 0, "The decoded image has no visible room");
        Assert.True(preview.Picture?.Zoom == 1, $"A small image shows at {preview.Picture?.Zoom:P0}, where it is never enlarged until it is zoomed");
        Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Text!.Contains($"{width} × {height} pixels", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>An image's preview zooms as a person zooms it: the keys step it in, each pixel a square of its own colour,
    /// to its actual size and in again with the wheel and the command modifier, and Fit Zoom to Window fills the pane with
    /// it, its red square red and its blue ground blue at every zoom.</summary>
    private void An_image_preview_zooms_in_to_its_actual_size_and_to_fit_drawing_its_pixels_as_they_are()
    {
        using (SKBitmap bitmap = new(100, 75))
        {
            bitmap.Erase(SKColors.Blue);
            using SKCanvas canvas = new(bitmap);
            using SKPaint paint = new() { Color = SKColors.Red };
            canvas.DrawRect(25, 25, 25, 25, paint);
            using SKImage source = SKImage.FromBitmap(bitmap);
            using SKData encoded = source.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, "square.png"), encoded.ToArray());
        }
        MainWindow window = Open();
        Assert.Null(window.OpenFile("square.png"));
        Ui.Settle();
        AssetPreviewControl preview = Preview(window.EditorArea.ActiveGroup);
        ZoomedPicture picture = preview.Picture!;
        Image image = Assert.Single(preview.GetVisualDescendants().OfType<Image>());
        Assert.Equal(new Size(100, 75), picture.DrawnSize);
        void Drawn(string when)
        {
            double zoom = picture.Zoom;
            Color red = Ui.Pixel(window, image.TranslatePoint(new Point(37 * zoom, 37 * zoom), window)!.Value);
            Color blue = Ui.Pixel(window, image.TranslatePoint(new Point(10 * zoom, 65 * zoom), window)!.Value);
            Assert.True(red == Colors.Red && blue == Colors.Blue, $"{when}, at {zoom:P0}, the preview draws {red} in its red square and {blue} on its blue ground");
        }

        Assert.True(preview.Focus());
        string command = OperatingSystem.IsMacOS() ? "meta" : "control";
        for (int i = 0; i < 5; i++)
        {
            Ui.Press(window, $"{command} EQUALS");
        }
        Assert.Equal(new Size(400, 300), picture.DrawnSize);
        Drawn("Zoomed in five steps");
        Ui.Press(window, $"{command} SLASH");
        Assert.Equal(new Size(100, 75), picture.DrawnSize);
        window.MouseWheel(picture.TranslatePoint(new Point(picture.Bounds.Width / 2, picture.Bounds.Height / 2), window)!.Value, new Vector(0, 1),
            OperatingSystem.IsMacOS() ? Avalonia.Input.RawInputModifiers.Meta : Avalonia.Input.RawInputModifiers.Control);
        Ui.Settle();
        Assert.Equal(new Size(125, 93.75), picture.DrawnSize);

        Ui.Click(preview.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == "Fit Zoom to Window"));
        Assert.True(picture.Zoom > 2 && Math.Abs(Math.Min(picture.Bounds.Width - picture.DrawnSize.Width, picture.Bounds.Height - picture.DrawnSize.Height) - (2 * ZoomedPicture.Gap) - 2) < 1,
            $"Fit Zoom to Window drew the picture {picture.DrawnSize} in a pane of {picture.Bounds.Size}");
        Drawn("Fitted to the pane");
        window.Close();
    }

    /// <summary>Svg.Skia recurses as deep as a drawing's elements, a stylesheet's rules and a selector's brackets go, so each is
    /// refused before it reads them, with the text still there beside the problem.</summary>
    private void An_SVG_nested_past_what_is_drawn_says_so_and_ends_nothing(string open, string close, string inner)
    {
        string nested = open == "<style>"
            ? "<style>" + string.Concat(Enumerable.Repeat("@media screen {", 20_000)) + new string('}', 20_000) + "</style>"
            : string.Concat(Enumerable.Repeat(open, 20_000)) + inner + string.Concat(Enumerable.Repeat(close, 20_000));
        File.WriteAllText(Path.Combine(folder, "deep.svg"), $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">{nested}</svg>");
        MainWindow window = Open();
        Assert.Null(window.OpenFile("deep.svg"));
        SvgPreview preview = window.EditorArea.ActiveGroup.Svg ?? throw new Xunit.Sdk.XunitException("The SVG shows no picture beside its text");
        Ui.WaitFor(() => preview.Problem is not null, () => "the SVG said to be nested too deeply");
        Assert.Equal("The SVG is nested too deeply to draw", preview.Problem);
        window.Close();
    }

    /// <summary>An SVG opens as its text with its picture beside it, drawn as a drawing, so zoomed in its circle's edge is as
    /// sharp as at its size (a point half a unit outside it the ground's own colour, where an enlarged bitmap would blend
    /// them); its text edited, the picture follows; and the toggle hides and shows it.</summary>
    private void An_SVG_is_drawn_beside_its_text_sharp_at_every_zoom_and_its_picture_follows_its_text_edited()
    {
        File.WriteAllText(Path.Combine(folder, "badge.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">\n  <rect width=\"40\" height=\"40\" fill=\"#0000ff\"/>\n"
            + "  <circle cx=\"20\" cy=\"20\" r=\"20\" fill=\"#ff0000\"/>\n</svg>\n");
        MainWindow window = Open();
        Assert.Null(window.OpenFile("badge.svg"));
        Ui.Settle();
        EditorGroup group = window.EditorArea.ActiveGroup;
        SvgPreview preview = group.Svg ?? throw new Xunit.Sdk.XunitException("The SVG shows no picture beside its text");
        Assert.True(group.Editor.IsEffectivelyVisible && window.EditorArea.Active!.HoldsText, "The SVG's text should stay in the editor beside its picture");
        ZoomedPicture picture = preview.Picture;
        Ui.WaitFor(() => picture.Picture is SvgPicture, () => $"the SVG drawn ({preview.Problem})");
        Image image = Assert.Single(preview.GetVisualDescendants().OfType<Image>());
        Color At(double x, double y) => Ui.Pixel(window, image.TranslatePoint(new Point(x * picture.Zoom, y * picture.Zoom), window)!.Value);
        Assert.Equal(new Size(40, 40), picture.DrawnSize);
        Assert.True(At(20, 20) == Colors.Red && At(1, 1) == Colors.Blue, $"The picture draws {At(20, 20)} in its circle and {At(1, 1)} in its corner");

        Assert.True(preview.Focus());
        string command = OperatingSystem.IsMacOS() ? "meta" : "control";
        for (int i = 0; i < 5; i++)
        {
            Ui.Press(window, $"{command} EQUALS");
        }
        Assert.Equal(new Size(160, 160), picture.DrawnSize);
        Assert.True(At(5.5, 5.5) == Colors.Blue && At(6.1, 6.1) == Colors.Red,
            $"Zoomed to {picture.Zoom:P0}, the circle's edge blends: {At(5.5, 5.5)} half a unit outside it, {At(6.1, 6.1)} inside it");

        Typing.Replace(window, "#ff0000", "#00ff00");
        Ui.WaitFor(() => At(20, 20) == Colors.Lime, () => $"the picture to follow the circle's colour typed, not {At(20, 20)}");
        Assert.Equal(new Size(160, 160), picture.DrawnSize);

        Assert.True(group.PreviewToggle.IsVisible && group.PreviewToggle.IsChecked == true, "The tab strip's preview toggle should show the picture is on");
        Ui.Click(group.PreviewToggle);
        Assert.True(group.Svg is null && group.PreviewToggle.IsChecked == false, "The toggle left the SVG's picture beside it");
        Ui.Click(group.PreviewToggle);
        Assert.NotNull(group.Svg);
        window.Close();
    }

    /// <summary>An SVG's image named by an address relative to the SVG is read from the SVG's folder, one below it too, and
    /// drawn in its picture, which names nothing left out; its address edited to another image beside the SVG, the picture
    /// draws that one.</summary>
    private void An_SVGs_image_named_relative_to_it_is_drawn_from_its_folder()
    {
        string art = Path.Combine(folder, "art");
        Directory.CreateDirectory(Path.Combine(art, "parts"));
        File.WriteAllBytes(Path.Combine(art, "parts", "leaf.png"), ImageBytes(".png", 10, 10, SKColors.Lime));
        File.WriteAllBytes(Path.Combine(art, "sun.png"), ImageBytes(".png", 10, 10, SKColors.Yellow));
        File.WriteAllText(Path.Combine(art, "badge.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\">\n  <rect width=\"40\" height=\"20\" fill=\"#0000ff\"/>\n"
            + "  <image href=\"parts/leaf.png\" width=\"20\" height=\"20\"/>\n</svg>\n");
        MainWindow window = Open();
        Assert.Null(window.OpenFile(Path.Combine("art", "badge.svg")));
        Ui.Settle();
        SvgPreview preview = window.EditorArea.ActiveGroup.Svg ?? throw new Xunit.Sdk.XunitException("The SVG shows no picture beside its text");
        ZoomedPicture picture = preview.Picture;
        Ui.WaitFor(() => picture.Picture is SvgPicture, () => $"the SVG drawn ({preview.Problem})");
        Image image = Assert.Single(preview.GetVisualDescendants().OfType<Image>());
        Color At(double x, double y) => Ui.Pixel(window, image.TranslatePoint(new Point(x * picture.Zoom, y * picture.Zoom), window)!.Value);
        Assert.True(At(10, 10) == Colors.Lime && At(30, 10) == Colors.Blue,
            $"The picture draws {At(10, 10)} where its image beside it is, {At(30, 10)} beside the image: its relative address was not read from its folder");
        Assert.Null(preview.LeftOut);

        Typing.Replace(window, "parts/leaf.png", "sun.png");
        Ui.WaitFor(() => At(10, 10) == Colors.Yellow, () => $"the picture to draw the image the address typed names, not {At(10, 10)}");
        Assert.Null(preview.LeftOut);
        window.Close();
    }

    /// <summary>An SVG's images named outside the SVG's folder (a way up out of it, a path from the disk's root, a file's
    /// address) are not drawn, nor one on the web fetched; the rest of the picture is, and the banner over it names each
    /// left out and why.</summary>
    private void An_SVG_draws_no_image_from_outside_its_folder_or_the_web_and_says_why()
    {
        string outside = Path.Combine(PathNames.Canonical(folder), "red.png");
        File.WriteAllBytes(outside, ImageBytes(".png", 10, 10, SKColors.Red));
        string address = new Uri(outside).AbsoluteUri;
        const string Web = "https://example.invalid/red.png";
        string art = Path.Combine(folder, "art");
        Directory.CreateDirectory(art);
        File.WriteAllText(Path.Combine(art, "badge.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"20\">\n  <rect width=\"100\" height=\"20\" fill=\"#0000ff\"/>\n"
            + "  <image href=\"../red.png\" width=\"20\" height=\"20\"/>\n"
            + $"  <image href=\"{outside}\" x=\"20\" width=\"20\" height=\"20\"/>\n"
            + $"  <image href=\"{address}\" x=\"40\" width=\"20\" height=\"20\"/>\n"
            + $"  <image href=\"{Web}\" x=\"60\" width=\"20\" height=\"20\"/>\n</svg>\n");
        MainWindow window = Open();
        Assert.Null(window.OpenFile(Path.Combine("art", "badge.svg")));
        Ui.Settle();
        SvgPreview preview = window.EditorArea.ActiveGroup.Svg ?? throw new Xunit.Sdk.XunitException("The SVG shows no picture beside its text");
        ZoomedPicture picture = preview.Picture;
        Ui.WaitFor(() => picture.Picture is SvgPicture, () => $"the SVG drawn ({preview.Problem})");
        Image image = Assert.Single(preview.GetVisualDescendants().OfType<Image>());
        Color At(double x, double y) => Ui.Pixel(window, image.TranslatePoint(new Point(x * picture.Zoom, y * picture.Zoom), window)!.Value);
        Assert.True(At(90, 10) == Colors.Blue, $"The picture draws {At(90, 10)} where it names nothing but its ground");
        Assert.True(new[] { 10, 30, 50, 70 }.All(x => At(x, 10) != Colors.Red),
            $"The picture drew an image from outside the SVG's folder: {string.Join(", ", new[] { 10, 30, 50, 70 }.Select(x => At(x, 10)))}");

        string? said = preview.LeftOut;
        Assert.True(said is not null && said.Contains("../red.png", StringComparison.Ordinal) && said.Contains(outside, StringComparison.Ordinal)
            && said.Contains(address, StringComparison.Ordinal) && said.Contains("outside the SVG's folder", StringComparison.Ordinal)
            && said.Contains(Web, StringComparison.Ordinal) && said.Contains("on the web", StringComparison.Ordinal),
            $"The banner over the picture should name each image left out and why, but said: {said}");
        Assert.Contains(preview.GetVisualDescendants().OfType<Banner>(), banner => banner.IsEffectivelyVisible && banner.Said == said);
        window.Close();
    }

    private void A_local_font_shows_its_actual_face_letters_digits_and_every_ASCII_symbol(string extension, string source, FontStyle style)
    {
        string file = "uninstalled-face" + extension;
        CopyFont(file, source);
        MainWindow window = Open();
        Assert.Null(window.OpenFile(file));
        Ui.Settle();

        GlyphTypeface face = window.EditorArea.Active!.Asset!.Typeface!;
        Assert.Equal("JetBrains Mono", face.FamilyName);
        Assert.Equal(style, face.Style);
        AssetPreviewControl preview = Preview(window.EditorArea.ActiveGroup);
        string[] samples = ["ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz", "0123456789", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"];
        foreach (string sample in samples)
        {
            Control shown = Assert.Single(preview.GetVisualDescendants().OfType<Control>(), control => AutomationProperties.GetName(control) == sample);
            Assert.True(shown.IsEffectivelyVisible && shown.Bounds.Width > 100 && shown.Bounds.Height > 16, $"The font sample '{sample}' has no visible glyph area");
        }
        Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "All printable ASCII glyphs are present.");
        Assert.Equal("Font", window.FindControl<TextBlock>("LanguageText")!.Text);
        window.Close();
    }

    private async Task Typing_pasting_saving_and_encoding_actions_never_write_a_preview_or_the_text_tab_it_replaced()
    {
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "Keep these notes.\n");
        byte[] original = ImageBytes(".png");
        File.WriteAllBytes(Path.Combine(folder, "picture.png"), original);
        CopyFont("face.ttf");
        byte[] font = File.ReadAllBytes(Path.Combine(folder, "face.ttf"));
        MainWindow window = Open();
        window.OpenFile("notes.txt");
        Ui.Settle();
        window.KeyTextInput("Changed: ");
        Ui.Settle();

        foreach (string file in new[] { "picture.png", "face.ttf" })
        {
            Assert.Null(window.OpenFile(file));
            Ui.Settle();
            Assert.True(Preview(window.EditorArea.ActiveGroup).Focus());
            window.KeyTextInput("Do not write this");
            await window.Clipboard!.SetTextAsync("Do not paste this");
            Ui.Press(window, Ui.Shortcut(Passenger.Editor.EditorActions.Paste));
            Ui.Press(window, Ui.Shortcut(AppActions.Save));
            Assert.False(window.Perform(AppActions.FileEncoding), "A binary preview offered text encoding conversion");
            Assert.False(window.EditorArea.Active!.IsModified);
        }
        Ui.Run(window, AppActions.SaveAll);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(folder, "picture.png")));
        Assert.Equal(font, File.ReadAllBytes(Path.Combine(folder, "face.ttf")));
        window.OpenFile("notes.txt");
        Ui.Settle();
        Assert.True(window.EditorArea.CodeEditor.IsVisible);
        Assert.Equal("Changed: Keep these notes.\n", window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        Assert.Equal("Changed: Keep these notes.\n", File.ReadAllText(Path.Combine(folder, "notes.txt")));
        window.Close();
    }

    private void Preview_tabs_split_move_close_reopen_and_return_with_the_workspace()
    {
        File.WriteAllBytes(Path.Combine(folder, "picture.png"), ImageBytes(".png"));
        CopyFont("face.ttf");
        MainWindow first = Open();
        first.OpenFile("picture.png");
        Ui.Settle();
        EditorArea area = first.EditorArea;
        EditorGroup left = area.ActiveGroup;
        AssetPreviewControl original = Preview(left);
        Assert.True(first.Perform(AppActions.SplitRight));
        Ui.Settle();
        EditorGroup right = area.ActiveGroup;
        Assert.NotSame(original, Preview(right));
        Assert.Same(left.Active!.Asset, right.Active!.Asset);
        Ui.Run(first, AppActions.CloseTab);
        Assert.Single(area.Groups);
        Assert.Same(original, Preview(left));
        Ui.Run(first, AppActions.CloseTab);
        Assert.Empty(area.Documents);
        Assert.True(first.Perform(AppActions.ReopenClosedTab));
        Ui.Settle();
        Assert.Equal("picture.png", area.Active!.Name);
        Assert.NotSame(original, Preview(area.ActiveGroup));
        Assert.True(first.Perform(AppActions.SplitDown));
        Ui.Settle();
        first.OpenFile("face.ttf");
        Ui.Settle();
        area.Move(area.ActiveGroup, area.Active!, left);
        Ui.Settle();
        Assert.NotNull(Preview(left));
        first.Close();

        MainWindow second = Open();
        Assert.Equal(2, second.EditorArea.Groups.Count);
        Assert.Equal(2, second.EditorArea.Documents.Count);
        foreach (EditorGroup group in second.EditorArea.Groups)
        {
            Assert.NotNull(Preview(group));
            Assert.False(group.Editor.IsVisible);
        }
        Assert.Contains(second.EditorArea.Documents, document => document.Asset?.Typeface?.FamilyName == "JetBrains Mono");
        second.Close();
    }

    private void Reloading_an_image_updates_both_splits_but_invalid_pixel_data_keeps_the_previous_picture_and_explains_why()
    {
        string path = Path.Combine(folder, "picture.png");
        byte[] complete = ImageBytes(".png");
        File.WriteAllBytes(path, complete);
        MainWindow window = Open();
        window.OpenFile("picture.png");
        Ui.Settle();
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        AssetPreview good = window.EditorArea.Active!.Asset!;

        File.WriteAllBytes(path, complete[..(complete.Length / 2)]);
        window.FollowWritten([path]);
        Ui.Settle();
        Assert.Same(good, window.EditorArea.Active.Asset);
        Assert.Contains("Preview reload failed", window.EditorArea.ActiveGroup.BannerText!, StringComparison.Ordinal);
        File.WriteAllBytes(path, ImageBytes(".png", width: 16, height: 8));
        window.FollowWritten([path]);
        Ui.Settle();
        foreach (EditorGroup group in window.EditorArea.Groups)
        {
            Image shown = Assert.Single(Preview(group).GetVisualDescendants().OfType<Image>());
            Assert.Same(group.Active!.Asset!.Bitmap, shown.Source);
            Assert.Equal(new PixelSize(16, 8), group.Active.Asset.Bitmap!.PixelSize);
            Assert.Null(group.Active.Notice);
        }
        string moved = Path.Combine(folder, "renamed.png");
        Assert.Null(window.EditorArea.MoveFile(path, moved));
        Assert.Equal("renamed.png", window.EditorArea.Active.Name);
        Assert.NotNull(Preview(window.EditorArea.ActiveGroup));
        string binary = Path.Combine(folder, "renamed.bin");
        Assert.Null(window.EditorArea.MoveFile(moved, binary));
        Assert.Null(window.EditorArea.Borrow(binary));
        Assert.NotNull(Preview(window.EditorArea.ActiveGroup));
        window.Close();
    }

    private void Invalid_empty_missing_unsupported_and_oversized_assets_fail_without_opening_binary_text()
    {
        File.WriteAllBytes(Path.Combine(folder, "broken.png"), ImageBytes(".png")[..50]);
        File.WriteAllBytes(Path.Combine(folder, "broken.ttf"), "not a font"u8.ToArray());
        File.WriteAllBytes(Path.Combine(folder, "empty.otf"), []);
        File.WriteAllBytes(Path.Combine(folder, "unknown.bin"), [0, 1, 2, 3]);
        using (FileStream large = File.Create(Path.Combine(folder, "large.ttf")))
        {
            large.SetLength((32 * 1024 * 1024) + 1);
        }
        using (FileStream large = File.Create(Path.Combine(folder, "large.png")))
        {
            large.SetLength((64 * 1024 * 1024) + 1);
        }
        byte[] huge = ImageBytes(".bmp");
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(18), 10000);
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(22), 10000);
        File.WriteAllBytes(Path.Combine(folder, "huge.bmp"), huge);
        MainWindow window = Open();
        // A stat refuses at once; the decode, off the UI thread, closes the loading tab, the status bar saying why.
        (string File, string Why)[] refused =
        [
            ("broken.png", "image"), ("broken.ttf", "font"), ("empty.otf", "font must be non-empty"),
            ("missing.png", "missing.png"), ("large.ttf", "no larger than 32 MB"), ("large.png", "no larger than 64 MB"), ("huge.bmp", "32 million pixels"),
        ];
        foreach ((string file, string why) in refused)
        {
            string? error = window.OpenFile(file);
            Ui.Settle();
            Assert.Empty(window.EditorArea.Documents);
            Assert.Contains(why, error ?? window.Status ?? "", StringComparison.Ordinal);
        }
        Assert.Null(window.OpenFile("unknown.bin"));
        Ui.Settle();
        Assert.True(window.EditorArea.Active is { Asset: null, IsBinary: true, IsReadOnly: true }, "unknown.bin did not open as its bytes' text, read-only");
        window.EditorArea.Close(window.EditorArea.Active!);
        Ui.Settle();
        Passenger.App.Mcp.PassengerTools agent = new(window);
        Task opening = agent.Open("huge.bmp");
        Ui.WaitFor(() => opening.IsCompleted, "the agent's open to answer");
        Assert.True(opening.IsFaulted, "An agent was told an image whose decode failed is open");
        Assert.Contains("32 million pixels", opening.Exception!.InnerException!.Message, StringComparison.Ordinal);
        Assert.Empty(window.EditorArea.Documents);
        File.WriteAllText(Path.Combine(folder, "vector.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\" />");
        Assert.Null(window.OpenFile("vector.svg"));
        Ui.Settle();
        Assert.Null(window.EditorArea.Active!.Asset);
        Assert.True(window.EditorArea.CodeEditor.IsVisible);
        window.Close();
    }

    /// <summary>A loaded machine's thread pool can end a small file's decode before the window's thread awaits it, so the
    /// open learns of the failure before it has a tab: decodes ending at once stand in for that.</summary>
    private void An_asset_whose_decode_fails_before_its_open_returns_is_refused_at_once_and_never_left_loading()
    {
        byte[] huge = ImageBytes(".bmp");
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(18), 10000);
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(22), 10000);
        File.WriteAllBytes(Path.Combine(folder, "huge.bmp"), huge);
        File.WriteAllBytes(Path.Combine(folder, "broken.ttf"), "not a font"u8.ToArray());
        MainWindow window = Open();
        string? error;
        using (BackgroundWork.AtOnce decoding = BackgroundWork.EndingAtOnce())
        {
            error = window.OpenFile("huge.bmp");
            Assert.True(decoding.Ran > 0, "The decode never ran at once, so this test stood in for nothing");
        }
        Ui.Settle();
        Assert.True(window.EditorArea.Documents.Count == 0, "An image whose decode had failed was left open, loading for good");
        Assert.Contains("32 million pixels", error ?? "", StringComparison.Ordinal);
        Assert.Equal(error, window.Status);

        Passenger.App.Mcp.PassengerTools agent = new(window);
        Task opening;
        using (BackgroundWork.EndingAtOnce())
        {
            opening = agent.Open("broken.ttf");
            Ui.WaitFor(() => opening.IsCompleted, "the agent's open to answer");
        }
        Assert.True(opening.IsFaulted, "An agent was told a font whose decode had failed is open");
        Assert.Contains("font could not be decoded", opening.Exception!.InnerException!.Message, StringComparison.Ordinal);
        Ui.Settle();
        Assert.True(window.EditorArea.Documents.Count == 0, "A font an agent opened whose decode had failed was left open, loading for good");
        window.Close();
    }

    /// <summary>A preview's tab holds an empty stand-in for text: nothing that reads or changes a file's text may take it for
    /// the image's, or an agent's replace would claim to have changed a picture, and a bookmark would mark a line it lacks.</summary>
    private void A_preview_has_no_text_for_the_status_bar_an_agent_find_and_replace_in_files_or_a_bookmark()
    {
        string picture = Path.Combine(folder, "picture.png");
        byte[] original = ImageBytes(".png");
        File.WriteAllBytes(picture, original);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "Keep these notes.\n");
        MainWindow window = Open();
        window.OpenFile("notes.txt");
        Ui.Settle();
        Assert.True(window.FindControl<Button>("EncodingButton")!.IsVisible, "A text file's encoding is not shown");
        window.OpenFile("picture.png");
        Ui.Settle();

        Assert.False(window.FindControl<Button>("EncodingButton")!.IsVisible, "An image's preview shows a text encoding");
        Assert.False(window.FindControl<Button>("LineSeparatorButton")!.IsVisible, "An image's preview shows a line separator");
        Assert.Equal("Image", window.FindControl<TextBlock>("LanguageText")!.Text);
        Assert.False(window.Perform(AppActions.ToggleBookmark), "A bookmark was put on a line of an image");
        Assert.False(window.Perform(AppActions.ToggleBreakpoint), "A breakpoint was put on a line of an image");
        Assert.Empty(window.Bookmarks.All);

        Passenger.App.Mcp.PassengerTools agent = new(window);
        foreach (Task asked in new Task[] { agent.EditorText("picture.png"), agent.Edit("picture.png", "x", "y") })
        {
            Ui.WaitFor(() => asked.IsCompleted, "the agent's answer");
            Assert.True(asked.IsFaulted, "An agent read or edited an image's preview as text");
            Assert.Contains("picture.png is an image, shown as a read-only preview", asked.Exception!.InnerException!.Message, StringComparison.Ordinal);
        }
        // A pattern that matches an empty line finds the text file's lines, never the image's empty stand-in.
        Task<Passenger.App.Mcp.FindInFilesResult> finding = agent.FindInFiles("^", regex: true);
        Passenger.App.Mcp.FindInFilesResult found = Ui.Await(finding, "find_in_files' answer");
        Assert.DoesNotContain(found.Matches, m => m.Path == "picture.png");
        Assert.Contains(found.Matches, m => m.Path == "notes.txt");
        Task<string> replacing = agent.ReplaceInFiles("^", "// ", regex: true);
        Assert.DoesNotContain("picture.png", Ui.Await(replacing, "replace_in_files' answer"), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(picture));
        Assert.Empty(window.EditorArea.Active!.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>An image up to the limits takes a while to decode: the window goes on answering meanwhile, its tab saying the
    /// image is loading, since what the UI thread does to open one is the same for a large image as for a small.</summary>
    private void A_large_image_decodes_off_the_UI_thread_its_tab_saying_it_is_loading_until_the_picture_lands()
    {
        byte[] large = NoiseBytes(2048, 2048);
        File.WriteAllBytes(Path.Combine(folder, "large.png"), large);
        File.WriteAllBytes(Path.Combine(folder, "small.png"), ImageBytes(".png"));
        MainWindow window = Open();
        // The first open warms what every open runs, so neither measured one pays for it.
        Opening(window, "small.png", large: false);

        long small = Opening(window, "small.png", large: false);
        long big = Opening(window, "large.png", large: true);
        Assert.True(big - small < large.Length / 4,
            $"Opening a {large.Length / (1024 * 1024)} MB image allocated {big / 1024} KB on the UI thread, a small one {small / 1024} KB: the large one was decoded there");
        window.Close();
    }

    /// <summary>Opens an image, a large one's tab saying it is loading until the decode lands, and closes it again: what the UI
    /// thread allocated from the open to the picture shown.</summary>
    /// <param name="large">Whether its decode outlasts the open: a small picture's, done on the pool before the open reaches its
    /// await, may land before the open returns.</param>
    private static long Opening(MainWindow window, string file, bool large)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(window.OpenFile(file));
        OpenDocument document = window.EditorArea.Active!;
        if (large)
        {
            Assert.True(document.Asset!.IsPending, $"{file} was decoded before the open returned");
            Assert.Contains(Preview(window.EditorArea.ActiveGroup).GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Loading the image…");
        }
        Ui.Settle();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(document.Asset!.IsPending, $"{file} never finished decoding");
        Image image = Assert.Single(Preview(window.EditorArea.ActiveGroup).GetVisualDescendants().OfType<Image>());
        Assert.Same(document.Asset.Bitmap, image.Source);
        Ui.Run(window, AppActions.CloseTab);
        Assert.Empty(window.EditorArea.Documents);
        return allocated;
    }

    /// <summary>An opaque picture of random pixels, which PNG cannot compress: its file is as large as its pixels.</summary>
    private static byte[] NoiseBytes(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        new Random(330).NextBytes(pixels);
        for (int alpha = 3; alpha < pixels.Length; alpha += 4)
        {
            pixels[alpha] = 255;
        }
        using SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>The bundled face copied to a new name, so the preview cannot depend on an installed family.</summary>
    private void CopyFont(string file, string source = "JetBrainsMono-Regular.ttf") =>
        File.Copy(Path.Combine(XamlTests.Repository, "src", "Passenger.Editor", "Fonts", source), Path.Combine(folder, file));

    private static AssetPreviewControl Preview(EditorGroup group) => Assert.Single(group.GetVisualDescendants().OfType<AssetPreviewControl>());

    /// <summary>Small real raster files without external fixtures; GIF and BMP use minimal headers, ICO carries a PNG.</summary>
    private static byte[] ImageBytes(string extension, int width = 64, int height = 32, SKColor? colour = null)
    {
        if (extension == ".gif")
        {
            return Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
        }
        if (extension == ".bmp")
        {
            byte[] bytes = new byte[58];
            bytes[0] = (byte)'B';
            bytes[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
            bytes[54] = 200;
            return bytes;
        }
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(colour ?? SKColors.CornflowerBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        SKEncodedImageFormat format = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        using SKData encoded = image.Encode(format, 90);
        byte[] raster = encoded.ToArray();
        if (extension != ".ico")
        {
            return raster;
        }
        byte[] icon = new byte[22 + raster.Length];
        BinaryPrimitives.WriteInt16LittleEndian(icon.AsSpan(2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(icon.AsSpan(4), 1);
        icon[6] = (byte)width;
        icon[7] = (byte)height;
        BinaryPrimitives.WriteInt16LittleEndian(icon.AsSpan(10), 1);
        BinaryPrimitives.WriteInt16LittleEndian(icon.AsSpan(12), 32);
        BinaryPrimitives.WriteInt32LittleEndian(icon.AsSpan(14), raster.Length);
        BinaryPrimitives.WriteInt32LittleEndian(icon.AsSpan(18), 22);
        raster.CopyTo(icon, 22);
        return icon;
    }
}
