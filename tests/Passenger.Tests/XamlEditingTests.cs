using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Text;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>The XAML editing tools through the window, each in Avalonia's XAML (an app <c>dotnet new avalonia.app</c>
/// makes) and WPF's (one <c>dotnet new wpf</c> makes): Reformat Code by its XAML code style and the file's .editorconfig,
/// Extract Resource and Extract Style, the fixes that create a binding's property or command in its view model (the MVVM
/// Toolkit's way where the project has it, stood in by its attributes' names) and a missing converter's class and
/// resource, and Surround With's elements; and a generated resource class's strings read through <c>{x:Static}</c>, known
/// as C# knows them: each language's value on the card, Go to Declaration to each file's entry, the keys completed, and a
/// key a language lacks warned of with the fix that adds it, for the window and an agent alike.</summary>
public sealed class XamlEditingTests : IClassFixture<XamlEditingTests.AvaloniaGallery>, IClassFixture<XamlEditingTests.WpfGallery>
{
    private readonly AvaloniaGallery avalonia;
    private readonly WpfGallery wpf;

    public XamlEditingTests(AvaloniaGallery avalonia, WpfGallery wpf)
    {
        this.avalonia = avalonia;
        this.wpf = wpf;
        avalonia.Reset();
        wpf.Reset();
    }

    /// <summary>The window's StackPanel written badly: indents astray, attributes over lines, blank lines, a comment, a
    /// character reference, single quotes and an empty element.</summary>
    private const string Untidy = "<StackPanel   Margin=\"12\">\n<!-- the\n     greeting -->\n      <TextBlock Text='Fish &amp; chips' />\n"
        + "<Button Grid.Row=\"1\" Content=\"Go\"\n  x:Name=\"Pay\"\n     HorizontalAlignment=\"Center\"/>\n\n\n\n  <Border></Border>\n</StackPanel >";

    /// <summary>Every tool in one window on the dialect's app, which its language server loads once, the main file put back
    /// as written before each: Surround With, Extract Resource and Extract Style, the fixes that create what a binding
    /// lacks, in Avalonia's XAML a partial property and the places below line breaks only Roslyn counts, and Reformat Code
    /// last, since its settings' change would lay out what the others write.</summary>
    [AvaloniaTheory]
    [InlineData("Avalonia")]
    [InlineData("WPF")]
    public void Each_xaml_editing_tool_makes_its_edit_in_one_window_on_the_apps_files(string dialect)
    {
        SharedProject app = App(dialect);
        MainWindow window = app.Open(app.PathOf(MainFile(dialect)));
        string written = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        SurroundWith(window, dialect);
        PutBack(window, app, dialect, written);
        ExtractResourceAndStyle(window, app, dialect);
        PutBack(window, app, dialect, written);
        CreateWhatABindingLacks(window, app, dialect);
        if (dialect == "Avalonia")
        {
            CreatePropertyAfterPartialProperties(window);
            PutBack(window, app, dialect, written);
            PlacesBelowLineBreaksOnlyRoslynCounts(window);
        }
        PutBack(window, app, dialect, written);
        Reformat(window, app, dialect);
        window.Close();
    }

    /// <summary>A Designer class over a neutral Resources.resx and a French one lacking Thanks, read by a view through
    /// <c>{x:Static p:Resources.…}</c>: Thanks alone is warned of, in the XAML and the code-behind's C#; Welcome's card says
    /// its neutral and French values; Go to Declaration offers the property and each language's entry; completion offers the
    /// keys with their neutral text; the fix asks the French value and adds the entry, one Undo taking it out again. An
    /// agent's diagnostics, code_actions, apply_code_action and definition say and do the same.</summary>
    [AvaloniaTheory]
    [InlineData("Avalonia")]
    [InlineData("WPF")]
    public void A_generated_resource_class_s_strings_in_xaml_have_their_languages_and_a_missing_one_is_added(string dialect)
    {
        SharedProject app = App(dialect);
        string view = dialect == "Avalonia" ? "GreetingView.axaml" : "GreetingView.xaml";
        string french = app.PathOf("Properties/Resources.fr.resx");
        string written = File.ReadAllText(french);
        MainWindow window = app.Open(app.PathOf(view));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => Untranslated(window).SetEquals(["'Thanks' has no French translation (Resources.fr.resx)"]),
                () => $"the key French lacks, and only it, to be warned of: {string.Join("; ", Untranslated(window))}");

            Ui.PlaceAfter(editor, "Resources.Wel");
            Ui.Run(window, AppActions.QuickDocumentation);
            Ui.WaitFor(() => window.Assist.HoverText?.Contains("fr: ", StringComparison.Ordinal) == true, () => $"Welcome's card to say its languages: {window.Assist.HoverText}");
            Assert.Equal("string Resources.Welcome\n'Welcome' (Resources):\nneutral: Welcome!\nfr: Bienvenue", window.Assist.HoverText);
            Ui.Press(window, "ESCAPE");

            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.Assist.Offered.Count == 3, () => $"the property and each file's entry to be offered ({window.Status}): {string.Join("; ", window.Assist.Offered)}");
            Assert.Equal(["Resources.Welcome", "neutral: Resources.resx", "fr: Resources.fr.resx"], window.Assist.Offered);
            Ui.Press(window, "DOWN");
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Resources.fr.resx", "the French entry to open");
            CodeEditor opened = window.EditorArea.CodeEditor;
            Assert.Contains("<data name=\"Welcome\"", opened.Document.Snapshot.GetLineText(opened.CaretPosition.Line), StringComparison.Ordinal);
            // Closed, so the fix below writes the file rather than editing its tab.
            Ui.Run(window, AppActions.CloseTab);

            window.OpenFile(app.PathOf(view));
            Ui.Settle();
            editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "{x:Static p:Resources.");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Welcome"), "the class's keys to be offered after p:Resources.");
            Assert.Contains(("Thanks", "Thank you"), window.Assist.CompletionDetails);
            Assert.Contains(("Welcome", "Welcome!"), window.Assist.CompletionDetails);
            Assert.DoesNotContain("resourceMan", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(editor, "Resources.Tha");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Add 'Thanks' to Resources.fr.resx…"), () => $"the fix to be offered: {string.Join("; ", window.Assist.Offered)}");
            Assert.Equal("Add 'Thanks' to Resources.fr.resx…", window.Assist.Offered[0]);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the fix to ask for the French value");
            ConfirmDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>()));
            dialog.KeyTextInput("Merci");
            Ui.Press(dialog, "ENTER");
            Ui.Settle();
            string entry = "  <data name=\"Thanks\" xml:space=\"preserve\">\n    <value>Merci</value>\n  </data>\n";
            Ui.WaitFor(() => File.ReadAllText(french) == written.Replace("</root>", entry + "</root>", StringComparison.Ordinal),
                () => $"the entry to be added after the file's last, as it writes them: {File.ReadAllText(french)}");
            Ui.WaitFor(() => Untranslated(window).Count == 0, () => $"the warning to go once French gives the key: {string.Join("; ", Untranslated(window))}");
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(french) == written, () => $"undo to take the entry back out ({window.Status}): {File.ReadAllText(french)}");
            Ui.WaitFor(() => Untranslated(window).Count == 1, "the warning to come back once undo took the entry away");

            window.OpenFile(app.PathOf(view + ".cs"));
            Ui.WaitFor(() => Untranslated(window).SetEquals(["'Thanks' has no French translation (Resources.fr.resx)"]),
                () => $"the code-behind's read of Thanks to be warned of as the view's is: {string.Join("; ", Untranslated(window))}");
            window.OpenFile(app.PathOf(view));
            Ui.Settle();

            Passenger.App.Mcp.PassengerTools agent = new(window);
            IReadOnlyList<Passenger.App.Mcp.Finding> findings = (IReadOnlyList<Passenger.App.Mcp.Finding>)Ui.Await(agent.Diagnostics(view), "an agent's diagnostics of the view");
            Passenger.App.Mcp.Finding missing = Assert.Single(findings, f => f.Message.Contains("translation", StringComparison.Ordinal));
            Assert.Equal(("warning", "'Thanks' has no French translation (Resources.fr.resx)"), (missing.Severity, missing.Message));
            IReadOnlyList<string> actions = Ui.Await(agent.CodeActions(view, missing.Line, missing.Column + 2), "the code actions an agent asks for");
            Assert.Equal("Add 'Thanks' to Resources.fr.resx… [quickfix]", actions[0]);
            string applied = Ui.Await(agent.ApplyCodeAction(view, missing.Line, missing.Column + 2, "Add 'Thanks' to Resources.fr.resx…", name: "Merci beaucoup"),
                "the agent's fix made");
            Assert.Equal("Added 'Thanks' to Resources.fr.resx", applied);
            Assert.Contains("<value>Merci beaucoup</value>", File.ReadAllText(french), StringComparison.Ordinal);
            Ui.WaitFor(() => Untranslated(window).Count == 0, () => $"the warning to go once the agent gave the key: {string.Join("; ", Untranslated(window))}");

            // Welcome read positionally and as x:Static's Member.
            string text = File.ReadAllText(app.PathOf(view));
            foreach (string read in (string[])["{x:Static p:Resources.Welcome}", "{x:Static Member=p:Resources.Welcome}"])
            {
                int at = text.IndexOf(read, StringComparison.Ordinal) + read.IndexOf(".Welcome", StringComparison.Ordinal) + 2;
                int line = text[..at].Count(c => c == '\n') + 1;
                IReadOnlyList<Passenger.App.Mcp.Place> places = Ui.Await(agent.Definition(view, line, at - text.LastIndexOf('\n', at - 1)), $"an agent's definition of Welcome in {read}");
                // An agent's paths are the folder's, in the system's own separators.
                Assert.Equal([Path.Combine("Properties", "Resources.Designer.cs"), Path.Combine("Properties", "Resources.resx"), Path.Combine("Properties", "Resources.fr.resx")],
                    [.. places.Select(p => p.Path)]);
                Assert.Contains("static string Welcome", places[0].Text, StringComparison.Ordinal);
                Assert.All(places.Skip(1), p => Assert.Contains("<data name=\"Welcome\"", p.Text, StringComparison.Ordinal));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The warnings of the shown file that a language lacks a localized key.</summary>
    private static HashSet<string> Untranslated(MainWindow window) => [.. window.EditorArea.Active!.Markers
        .Where(m => m.Kind == MarkerKind.Warning && m.Message.Contains(" translat", StringComparison.Ordinal)).Select(m => m.Message)];

    /// <summary>A Designer class over a neutral Resources.resx and a French one lacking a key, and a view reading two of its
    /// strings through <c>{x:Static}</c>: WPF's class public, as PublicResXFileCodeGenerator writes it, Avalonia's internal.</summary>
    private static void Localize(SharedProject app, string dialect)
    {
        Directory.CreateDirectory(app.PathOf("Properties"));
        string designer = LocalizationTests.Project.Designer.Replace("Shop.", "Gallery.", StringComparison.Ordinal);
        File.WriteAllText(app.PathOf("Properties/Resources.Designer.cs"), dialect == "Avalonia" ? designer
            : designer.Replace("internal class Resources", "public class Resources", StringComparison.Ordinal).Replace("internal static", "public static", StringComparison.Ordinal));
        File.WriteAllText(app.PathOf("Properties/Resources.resx"), LocalizationTests.Project.Resx(oneLine: false, ("Thanks", "Thank you"), ("Welcome", "Welcome!")));
        File.WriteAllText(app.PathOf("Properties/Resources.fr.resx"), LocalizationTests.Project.Resx(oneLine: false, ("Welcome", "Bienvenue")));
        if (dialect == "Avalonia")
        {
            File.WriteAllText(app.PathOf("GreetingView.axaml"), """
                <UserControl xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:p="using:Gallery.Properties"
                             x:Class="Gallery.GreetingView">
                    <StackPanel>
                        <TextBlock Text="{x:Static p:Resources.Welcome}" />
                        <TextBlock Text="{x:Static p:Resources.Thanks}" />
                        <TextBlock Text="{x:Static Member=p:Resources.Welcome}" />
                    </StackPanel>
                </UserControl>

                """);
            File.WriteAllText(app.PathOf("GreetingView.axaml.cs"), "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class GreetingView : UserControl\n{\n    public static string Said => Properties.Resources.Thanks;\n}\n");
            return;
        }
        File.WriteAllText(app.PathOf("GreetingView.xaml"), """
            <UserControl x:Class="Gallery.GreetingView"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:p="clr-namespace:Gallery.Properties">
                <StackPanel>
                    <TextBlock Text="{x:Static p:Resources.Welcome}" />
                    <TextBlock Text="{x:Static p:Resources.Thanks}" />
                    <TextBlock Text="{x:Static Member=p:Resources.Welcome}" />
                </StackPanel>
            </UserControl>

            """);
        File.WriteAllText(app.PathOf("GreetingView.xaml.cs"), "using System.Windows.Controls;\n\nnamespace Gallery;\n\npublic partial class GreetingView : UserControl\n{\n    public static string Said => Properties.Resources.Thanks;\n}\n");
    }

    /// <summary>The main file shown again with its text as written, in one edit, and the keys in it.</summary>
    private static void PutBack(MainWindow window, SharedProject app, string dialect, string written)
    {
        window.OpenFile(app.PathOf(MainFile(dialect)));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.ApplyEdits([new TextEdit(0, editor.Document.Snapshot.Length, written)]);
        editor.Selections = SelectionSet.Single(0);
        editor.Focus();
        Ui.Settle();
        Assert.Equal(written, editor.Document.Snapshot.ToString());
    }

    /// <summary>Reformat Code lays XAML out by its code style and the file's .editorconfig, changing nothing but whitespace.</summary>
    private static void Reformat(MainWindow window, SharedProject app, string dialect)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Run(window, AppActions.Reformat);
        Ui.Settle();
        Assert.True(window.Status == "Already formatted", $"The template's window, written as Rider lays it out, was reformatted: {window.Status}");

        string before = editor.Document.Snapshot.ToString();
        int start = before.IndexOf("<StackPanel", StringComparison.Ordinal);
        int end = before.IndexOf("</StackPanel>", StringComparison.Ordinal) + "</StackPanel>".Length;
        editor.ApplyEdits([new TextEdit(start, end - start, Untidy)]);
        string untidy = editor.Document.Snapshot.ToString();
        Ui.Run(window, AppActions.Reformat);
        Ui.Settle();
        Assert.Equal("Reformatted", window.Status);
        // Rider's defaults: attributes aligned as broken, each child on a line, at most two blank lines, values as written.
        Assert.Equal(before[..start] + "<StackPanel Margin=\"12\">\n        <!-- the\n             greeting -->\n        <TextBlock Text='Fish &amp; chips' />\n"
            + "        <Button Grid.Row=\"1\" Content=\"Go\"\n                x:Name=\"Pay\"\n                HorizontalAlignment=\"Center\" />\n\n\n        <Border></Border>\n    </StackPanel>"
            + before[end..], editor.Document.Snapshot.ToString());
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString() == untidy, "One Undo did not take the whole reformat back");

        // Format the Button's lines alone: the comment above them keeps its column.
        int button = untidy.IndexOf("<Button", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(button, untidy.IndexOf("/>", button, StringComparison.Ordinal)));
        Ui.Run(window, AppActions.Reformat);
        Ui.Settle();
        string selected = editor.Document.Snapshot.ToString();
        Assert.Contains("    <StackPanel   Margin=\"12\">\n<!-- the\n", selected, StringComparison.Ordinal);
        Assert.Contains("\n        <Button Grid.Row=\"1\" Content=\"Go\"\n                x:Name=\"Pay\"\n                HorizontalAlignment=\"Center\" />\n", selected, StringComparison.Ordinal);
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();

        // The .editorconfig, by Rider's keys and XAML Styler's: each attribute on a line, in XAML Styler's order.
        File.WriteAllText(app.PathOf(".editorconfig"), "root = true\n\n[*.{axaml,xaml}]\nxml_attribute_style = on_different_lines\nxml_attribute_indent = single_indent\n"
            + "xaml_enable_attribute_reordering = true\nxaml_remove_ending_tag_of_empty_element = true\n");
        editor.Selections = SelectionSet.Single(0);
        Ui.Run(window, AppActions.Reformat);
        Ui.Settle();
        string configured = editor.Document.Snapshot.ToString();
        Assert.Equal("<StackPanel Margin=\"12\">\n        <!-- the\n             greeting -->\n        <TextBlock Text='Fish &amp; chips' />\n"
            + "        <Button\n            x:Name=\"Pay\"\n            Grid.Row=\"1\"\n            HorizontalAlignment=\"Center\"\n            Content=\"Go\" />\n\n\n        <Border />\n    </StackPanel>"
            + before[end..], configured[configured.IndexOf("<StackPanel", StringComparison.Ordinal)..]);
        Assert.StartsWith("<Window\n    x:Class=\"Gallery.MainWindow\"\n    xmlns=\"", configured, StringComparison.Ordinal);
        File.Delete(app.PathOf(".editorconfig"));

        // Settings' own page, which Reformat follows where no .editorconfig says otherwise.
        SettingsDialog settings = window.ShowSettings();
        settings.Page = SettingsDialog.XamlCodeStylePage;
        Ui.Settle();
        Assert.Contains("Attributes format", settings.Shown);
        Assert.Contains("Attributes indenting", settings.Shown);
        Assert.Contains("Reorder attributes", settings.Shown);
        Assert.Contains("Remove ending tag of empty element", settings.Shown);
        settings.Change(s => s with { XamlFormat = s.XamlFormat with { AttributeStyle = Passenger.Xaml.Features.XamlAttributeStyle.FirstAttributeOnSingleLine } });
        settings.Ok();
        Ui.Settle();
        editor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Ui.Run(window, AppActions.Reformat);
        Ui.Settle();
        Assert.Contains("\n        <Button Grid.Row=\"1\"\n                Content=\"Go\"\n                x:Name=\"Pay\"\n                HorizontalAlignment=\"Center\" />\n",
            editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Extract Resource moves a value into this file's or the application's resources, and Extract Style a
    /// button's properties into a style.</summary>
    private static void ExtractResourceAndStyle(MainWindow window, SharedProject app, string dialect)
    {
        bool avaloniaXaml = dialect == "Avalonia";
        string main = MainFile(dialect);
        string appFile = avaloniaXaml ? "App.axaml" : "App.xaml";
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        List<string> asked = Answer(window);
        string button = avaloniaXaml ? "<Button Name=\"Go\" Content=\"Go\"" : "<Button x:Name=\"Go\" Content=\"Go\" Click=\"OnGo\"";
        Insert(editor, button, " Margin=\"12\"");

        Ui.PlaceAfter(editor, "<StackPanel Margin=\"1");
        Ui.Run(window, AppActions.RefactorThis);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Refactor This's list on the panel's margin");
        Assert.Equal(["Extract Resource…", "Extract Style…"], window.Assist.Offered);
        Ui.Press(window, "ESCAPE");
        Assert.True(window.RunFromMenu(AppActions.ExtractResource), window.Status);
        ExtractResourceDialog resource = Shown<ExtractResourceDialog>(window, "Extract Resource");
        Assert.Equal("StackPanelMargin", resource.NameBox.Text);
        Assert.Equal([$"{main} <Window.Resources>", $"{appFile} <Application.Resources>", avaloniaXaml ? "Palette.axaml <ResourceDictionary>" : "Brushes.xaml <ResourceDictionary>"],
            resource.Places);
        Assert.True(resource.EveryUse.IsVisible && resource.EveryUse.IsChecked == true, "The value's other use was not offered to replace too");
        resource.NameBox.Text = "Accent";
        Ui.Settle();
        Assert.Equal("Resource with the same name already exists", resource.Problem);
        Assert.False(resource.NextButton.IsEnabled, "Next took a key a resource the window reaches already has");
        resource.NameBox.Text = "PanelMargin";
        Ui.Settle();
        Next(resource);
        string text = editor.Document.Snapshot.ToString();
        Assert.Contains(avaloniaXaml
            ? "Title=\"Gallery\">\n    <Window.Resources>\n        <Thickness x:Key=\"PanelMargin\">12</Thickness>\n    </Window.Resources>\n    <StackPanel Margin=\"{StaticResource PanelMargin}\""
            : "        </Style>\n        <Thickness x:Key=\"PanelMargin\">12</Thickness>\n    </Window.Resources>\n    <StackPanel Margin=\"{StaticResource PanelMargin}\">", text, StringComparison.Ordinal);
        Assert.Contains($"{button} Margin=\"{{StaticResource PanelMargin}}\"", text, StringComparison.Ordinal);
        Assert.Equal($"Extracted resource 'PanelMargin' to {main} <Window.Resources>, used in 2 places", window.Status);
        Ui.WaitFor(() => document.Markers.Count == 0, "the file, its new resource resolved, marked with nothing");
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.DoesNotContain("PanelMargin", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        string appText = File.ReadAllText(app.PathOf(appFile));
        Insert(editor, avaloniaXaml ? "<TextBlock Name=\"Title\"" : "<TextBlock Style=\"{StaticResource Heading}\"", " Foreground=\"#E2A33F\"");
        Ui.PlaceAfter(editor, "Foreground=\"#E2");
        Assert.True(window.RunFromMenu(AppActions.ExtractResource), window.Status);
        resource = Shown<ExtractResourceDialog>(window, "Extract Resource");
        Assert.Equal("ForegroundBrush", resource.NameBox.Text);
        resource.PlaceBox.SelectedIndex = 1;
        Next(resource);
        Assert.Contains(avaloniaXaml
            ? "            </ResourceDictionary.MergedDictionaries>\n            <SolidColorBrush x:Key=\"ForegroundBrush\" Color=\"#E2A33F\" />\n        </ResourceDictionary>"
            : "            </Style>\n            <SolidColorBrush x:Key=\"ForegroundBrush\" Color=\"#E2A33F\" />\n        </ResourceDictionary>",
            File.ReadAllText(app.PathOf(appFile)).ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{StaticResource ForegroundBrush}\"", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.WaitFor(() => document.Markers.Count == 0, "the brush, now the application's, resolved");
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.Equal([$"Undo Extract Resource? It changes {main} and {appFile}."], asked);
        Assert.Equal(appText, File.ReadAllText(app.PathOf(appFile)));
        Assert.DoesNotContain("ForegroundBrush", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "<Butt");
        Assert.True(window.RunFromMenu(AppActions.ExtractStyle), window.Status);
        ExtractStyleDialog style = Shown<ExtractStyleDialog>(window, "Extract Style");
        Assert.Equal(avaloniaXaml ? "buttonStyle" : "ButtonStyle", style.NameBox.Text);
        Assert.Equal(["Content = \"Go\"", "Margin = \"12\"", "Background = \"{StaticResource Accent}\""], style.Properties.Select(p => p.Content as string));
        foreach (CheckBox box in style.Properties)
        {
            box.IsChecked = false;
        }
        Ui.Settle();
        Assert.Equal("At least one property must be selected", style.Problem);
        style.Properties[1].IsChecked = true;
        Ui.Click(style.Properties[2]);
        Next(style);
        text = editor.Document.Snapshot.ToString();
        if (avaloniaXaml)
        {
            Assert.Contains("Title=\"Gallery\">\n    <Window.Styles>\n        <Style Selector=\"Button.buttonStyle\">\n            <Setter Property=\"Margin\" Value=\"12\" />\n"
                + "            <Setter Property=\"Background\" Value=\"{StaticResource Accent}\" />\n        </Style>\n    </Window.Styles>\n", text, StringComparison.Ordinal);
            Assert.Contains("<Button Name=\"Go\" Content=\"Go\" Classes=\"buttonStyle\" />", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("        </Style>\n        <Style x:Key=\"ButtonStyle\" TargetType=\"Button\">\n            <Setter Property=\"Margin\" Value=\"12\" />\n"
                + "            <Setter Property=\"Background\" Value=\"{StaticResource Accent}\" />\n        </Style>\n    </Window.Resources>", text, StringComparison.Ordinal);
            Assert.Contains("<Button x:Name=\"Go\" Content=\"Go\" Click=\"OnGo\" Style=\"{StaticResource ButtonStyle}\" />", text, StringComparison.Ordinal);
        }
        Ui.WaitFor(() => document.Markers.Count == 0, "the style and the button that takes it marked with nothing");
    }

    /// <summary>A binding path and a converter nothing has offer to create them: a property or command in the view model,
    /// the MVVM Toolkit's way where the project has it, and a converter's class and resource.</summary>
    private static void CreateWhatABindingLacks(MainWindow window, SharedProject app, string dialect)
    {
        bool avaloniaXaml = dialect == "Avalonia";
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        List<string> asked = Answer(window);
        string model = app.PathOf("ViewModels/MainViewModel.cs");
        string last = avaloniaXaml ? "public int Count { get; set; }" : "public bool IsBusy { get; set; }";

        Replace(editor, "{Binding Greeting}", "{Binding Subtitle}");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("'Subtitle'", StringComparison.Ordinal)), "the binding path the view model lacks marked");
        Ui.PlaceAfter(editor, "{Binding Subt");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the binding's line");
        Assert.Equal("Create property 'Subtitle'", window.Assist.Offered[0]);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains($"    {last}\n\n    public string? Subtitle {{ get; set; }}\n}}", File.ReadAllText(model).ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Ui.WaitFor(() => !document.Markers.Any(m => m.Message.Contains("'Subtitle'", StringComparison.Ordinal)), "the binding resolved once the property is there");

        Insert(editor, avaloniaXaml ? "<Button Name=\"Go\" Content=\"Go\"" : "<Button x:Name=\"Go\" Content=\"Go\" Click=\"OnGo\"", " Command=\"{Binding SaveCommand}\"");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("'SaveCommand'", StringComparison.Ordinal)), "the command the view model lacks marked");
        Ui.PlaceAfter(editor, "{Binding Save");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the command's line");
        Assert.Equal("Create property 'SaveCommand'", window.Assist.Offered[0]);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        string written = File.ReadAllText(model).ReplaceLineEndings("\n");
        Assert.Contains("    public string? Subtitle { get; set; }\n\n    public ICommand? SaveCommand { get; set; }\n}", written, StringComparison.Ordinal);
        Assert.Contains(avaloniaXaml ? "using System.Windows.Input;\n\nnamespace Gallery.ViewModels;" : "using System.Collections.ObjectModel;\nusing System.Windows.Input;\n", written, StringComparison.Ordinal);
        Ui.WaitFor(() => !document.Markers.Any(m => m.Message.Contains("'SaveCommand'", StringComparison.Ordinal)), "the command resolved once it is there");

        Insert(editor, avaloniaXaml ? "<TextBlock Text=\"{Binding #Go.Content}\" />" : "<TextBlock Text=\"{Binding Content, ElementName=Go}\" />",
            "\n        <TextBlock Text=\"{Binding Greeting, Converter={StaticResource ShoutConverter}}\" />");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message == "Resource 'ShoutConverter' is not found"), "the converter nothing defines marked");
        Ui.PlaceAfter(editor, "{StaticResource Shout");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the converter's line");
        Assert.Equal("Create converter 'ShoutConverter'", window.Assist.Offered[0]);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        string converter = app.PathOf("ShoutConverter.cs");
        Assert.True(File.Exists(converter), $"No ShoutConverter.cs was made: {window.Status}");
        string made = File.ReadAllText(converter).ReplaceLineEndings("\n");
        Assert.Contains("namespace Gallery;\n\npublic class ShoutConverter : IValueConverter\n{\n", made, StringComparison.Ordinal);
        Assert.Contains(avaloniaXaml ? "using Avalonia.Data.Converters;" : "using System.Windows.Data;", made, StringComparison.Ordinal);
        Assert.Contains(avaloniaXaml
            ? "    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)\n    {\n        throw new NotImplementedException();\n    }\n"
            : "    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)\n    {\n        throw new NotImplementedException();\n    }\n", made, StringComparison.Ordinal);
        string view = editor.Document.Snapshot.ToString();
        Assert.Contains(avaloniaXaml ? "xmlns:vm=\"using:Gallery.ViewModels\"\n        xmlns:local=\"using:Gallery\"" : "xmlns:vm=\"clr-namespace:Gallery.ViewModels\"\n        xmlns:local=\"clr-namespace:Gallery\"",
            view, StringComparison.Ordinal);
        Assert.Contains("<local:ShoutConverter x:Key=\"ShoutConverter\" />", view, StringComparison.Ordinal);
        Ui.WaitFor(() => document.Markers.Count == 0, "the converter, its class read, resolved and the window marked with nothing");
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.Equal([$"Undo Create converter 'ShoutConverter'? It changes {MainFile(dialect)} and ShoutConverter.cs."], asked);
        Assert.False(File.Exists(converter), "Undo left the converter's file it made");
        Assert.DoesNotContain("ShoutConverter x:Key", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        window.OpenFile(app.PathOf(avaloniaXaml ? "ShopView.axaml" : "ShopView.xaml"));
        Ui.Settle();
        CodeEditor shop = window.EditorArea.CodeEditor;
        OpenDocument shopView = window.EditorArea.Active!;
        Insert(shop, "<TextBlock Text=\"{Binding Name}\" />", "\n        <TextBlock Text=\"{Binding Title}\" />\n        <Button Command=\"{Binding BuyCommand}\" />");
        Ui.WaitFor(() => shopView.Markers.Count == 2, "the property and the command the shop's view model lacks marked");
        Ui.PlaceAfter(shop, "{Binding Tit");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the property's line");
        Assert.Equal("Create property 'Title'", window.Assist.Offered[0]);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Ui.PlaceAfter(shop, "{Binding Buy");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the command's line");
        Assert.Equal("Create property 'BuyCommand'", window.Assist.Offered[0]);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal("using CommunityToolkit.Mvvm.ComponentModel;\nusing CommunityToolkit.Mvvm.Input;\n\nnamespace Gallery.ViewModels;\n\npublic partial class ShopViewModel : ObservableObject\n{\n"
            + "    [ObservableProperty]\n    private string? _name;\n\n    [ObservableProperty]\n    private string? _title;\n\n    [RelayCommand]\n    private void Buy()\n    {\n    }\n}\n",
            File.ReadAllText(app.PathOf("ViewModels/ShopViewModel.cs")).ReplaceLineEndings("\n"));
        Ui.WaitFor(() => shopView.Markers.Count == 0, "the generated property and command resolved");
    }

    /// <summary>In a view model written as Avalonia's MVVM template writes it, in toolkit 8.4's partial properties (which
    /// flags a field marked [ObservableProperty], MVVMTK0042), the property a binding lacks is made another partial property
    /// after the view model's own.</summary>
    private void CreatePropertyAfterPartialProperties(MainWindow window)
    {
        window.OpenFile(avalonia.PathOf("TillView.axaml"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Insert(editor, "<TextBlock Text=\"{Binding Name}\" />", "\n        <TextBlock Text=\"{Binding Price}\" />");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("'Price'", StringComparison.Ordinal)), "the binding path the view model lacks marked");
        Ui.PlaceAfter(editor, "{Binding Pri");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the binding's line");
        Assert.Equal("Create property 'Price'", window.Assist.Offered[0]);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        string made = File.ReadAllText(avalonia.PathOf("ViewModels/TillViewModel.cs")).ReplaceLineEndings("\n");
        Assert.True(made.Contains("    [ObservableProperty]\n    public partial string Name { get; set; } = \"Tea\";\n\n    [ObservableProperty]\n    public partial string? Price { get; set; }\n\n    public void Clear()", StringComparison.Ordinal),
            $"Create property did not make a partial property after the view model's own, as its others are written:\n{made}");
        Ui.WaitFor(() => document.Markers.Count == 0, "the binding resolved once the property is there");
    }

    /// <summary>Surround With wraps the element at the caret, or the elements chosen, in a layout element.</summary>
    private static void SurroundWith(MainWindow window, string dialect)
    {
        bool avaloniaXaml = dialect == "Avalonia";
        CodeEditor editor = window.EditorArea.CodeEditor;
        string button = avaloniaXaml ? "<Button Name=\"Go\" Content=\"Go\" Background=\"{StaticResource Accent}\" />"
            : "<Button x:Name=\"Go\" Content=\"Go\" Click=\"OnGo\" Background=\"{StaticResource Accent}\" />";
        string title = avaloniaXaml ? "<TextBlock Name=\"Title\" Text=\"{Binding Greeting}\" />" : "<TextBlock Style=\"{StaticResource Heading}\" Text=\"{Binding Greeting}\" />";
        Ui.PlaceAfter(editor, "<Butt");
        Ui.Run(window, AppActions.SurroundWith);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Surround With's templates");
        Assert.Equal(avaloniaXaml ? ["tag", "Grid", "StackPanel", "Border", "ScrollViewer", "DockPanel", "WrapPanel", "Canvas", "Viewbox", "Panel"]
            : ["tag", "Grid", "StackPanel", "Border", "ScrollViewer", "DockPanel", "WrapPanel", "Canvas", "Viewbox"], window.Assist.Offered);
        // Its digit chooses it: 4 is Border, the caret then in its start tag.
        Ui.Press(window, "4");
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        Assert.Contains($"        <Border>\n            {button}\n        </Border>\n", text, StringComparison.Ordinal);
        Assert.Equal(text.IndexOf("<Border>", StringComparison.Ordinal) + "<Border".Length, editor.Selections.Primary.Caret);
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();

        text = editor.Document.Snapshot.ToString();
        int from = text.IndexOf(title, StringComparison.Ordinal) + 3;
        editor.Selections = SelectionSet.Single(new Selection(from, text.IndexOf(button, StringComparison.Ordinal) + 4));
        Ui.Run(window, AppActions.SurroundWith);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Surround With's templates for two elements");
        Assert.DoesNotContain("Border", window.Assist.Offered);
        Assert.DoesNotContain("ScrollViewer", window.Assist.Offered);
        Ui.Press(window, "1");
        Ui.Settle();
        window.KeyTextInput("DockPanel");
        Ui.Settle();
        Assert.Contains($"        <DockPanel>\n            {title}\n            {button}\n        </DockPanel>\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A lone '\r', U+0085, U+2028 and U+2029 end a line for Roslyn and the language servers, not for the editor:
    /// XAML's usages, a binding's declaration in its view model and Extract Resource's edits are read and made in each
    /// file's own lines, never a line away for each such break above them.</summary>
    private void PlacesBelowLineBreaksOnlyRoslynCounts(MainWindow window)
    {
        string pasted = $"<!-- Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd -->";
        CodeEditor editor = window.EditorArea.CodeEditor;
        Insert(editor, "Title=\"Gallery\">", $"\n    {pasted}");
        Ui.PlaceAfter(editor, "<Button Name=\"G");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => window.Find.Rows.Any(r => r.Usage is not null), "the usages in the Find window");
        List<Usage> found = [.. window.Find.Rows.Select(r => r.Usage).OfType<Usage>().Where(u => u.Path.EndsWith("MainWindow.axaml", StringComparison.Ordinal))];
        Assert.Equal(2, found.Count);
        TextSnapshot text = editor.Document.Snapshot;
        Assert.All(found, usage => Assert.True(text.GetText(text.GetOffset(new TextPosition(usage.Line, usage.Column)), 2) == "Go",
            $"A usage of Go was found at line {usage.Line + 1}, column {usage.Column + 1}: '{text.GetLineText(usage.Line)}'"));

        window.OpenFile(avalonia.PathOf("ViewModels/MainViewModel.cs"));
        Ui.Settle();
        Insert(window.EditorArea.CodeEditor, "namespace Gallery.ViewModels;\n", $"\n/* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\n");
        Ui.Settle();
        window.OpenFile(avalonia.PathOf("MainWindow.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "{Binding Gree");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Equal("    public string |Greeting { get; set; } = \"Hello\";", CaretLine(window.EditorArea.CodeEditor));

        window.OpenFile(avalonia.PathOf("MainWindow.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"1");
        Assert.True(window.RunFromMenu(AppActions.ExtractResource), window.Status);
        ExtractResourceDialog resource = Shown<ExtractResourceDialog>(window, "Extract Resource");
        resource.NameBox.Text = "PanelMargin";
        Ui.Settle();
        Next(resource);
        Assert.Contains($"Title=\"Gallery\">\n    <Window.Resources>\n        <Thickness x:Key=\"PanelMargin\">12</Thickness>\n    </Window.Resources>\n    {pasted}\n"
            + "    <StackPanel Margin=\"{StaticResource PanelMargin}\" Spacing=\"8\">\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
    }

    private SharedProject App(string dialect) => dialect == "Avalonia" ? avalonia : wpf;

    private static string MainFile(string dialect) => dialect == "Avalonia" ? "MainWindow.axaml" : "MainWindow.xaml";

    /// <summary>Text put in after a stretch of the file, as one edit.</summary>
    private static void Insert(CodeEditor editor, string after, string text)
    {
        int at = editor.Document.Snapshot.ToString().IndexOf(after, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{after}' is not in the file");
        editor.ApplyEdits([new TextEdit(at + after.Length, 0, text)]);
    }

    private static T Shown<T>(MainWindow window, string what) where T : RefactoringDialog
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<T>().Any() || window.Status?.Contains(what, StringComparison.Ordinal) == true, $"{what}'s dialog to open");
        return window.OwnedWindows.OfType<T>().FirstOrDefault() ?? throw new Xunit.Sdk.XunitException($"{what} opened no dialog: {window.Status}");
    }

    /// <summary>Next clicked, and the dialog waited out: closed once the refactoring is made, failing with what it says
    /// when it stays open.</summary>
    private static void Next(RefactoringDialog dialog)
    {
        Assert.True(dialog.NextButton.IsEnabled, $"Next is disabled: {dialog.Problem}");
        Ui.Click(dialog.NextButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem.Length > 0, "the refactoring to be made");
        Assert.False(dialog.IsVisible, $"The dialog stayed open: {dialog.Problem}");
    }

    /// <summary>Agrees to every question the window asks, keeping each one's text in order.</summary>
    private static List<string> Answer(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        return asked;
    }

    /// <summary>The MVVM Toolkit's names, stood in so no package is reached: Passenger knows the toolkit by its attributes'
    /// full names, and its generators are not needed to read the XAML.</summary>
    private const string Mvvm = """
        using System.ComponentModel;
        using System.Runtime.CompilerServices;

        namespace CommunityToolkit.Mvvm.ComponentModel
        {
            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property)]
            public sealed class ObservablePropertyAttribute : System.Attribute
            {
            }

            public class ObservableObject : INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;

                protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
                {
                    field = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                    return true;
                }
            }
        }

        namespace CommunityToolkit.Mvvm.Input
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class RelayCommandAttribute : System.Attribute
            {
            }
        }

        """;

    private const string ShopViewModel = """
        using CommunityToolkit.Mvvm.ComponentModel;

        namespace Gallery.ViewModels;

        public class ShopViewModel : ObservableObject
        {
            [ObservableProperty]
            private string? _name;
        }

        """;

    /// <summary>A view model written as Avalonia's MVVM template writes it, in toolkit 8.4's partial properties.</summary>
    private const string TillViewModel = """
        using CommunityToolkit.Mvvm.ComponentModel;

        namespace Gallery.ViewModels;

        public partial class TillViewModel : ObservableObject
        {
            [ObservableProperty]
            public partial string Name { get; set; } = "Tea";

            public void Clear()
            {
            }
        }

        """;

    /// <summary><see cref="XamlTests.Gallery"/>'s app, with a shop view whose view model uses the MVVM Toolkit's fields, a till
    /// view whose view model uses its partial properties and a greeting view of a Designer class's strings: never built, since
    /// without the toolkit's generators Avalonia's compiler finds none of the properties they make, so XamlTests' own gallery,
    /// which it builds, has neither.</summary>
    public sealed class AvaloniaGallery : SharedProject
    {
        public AvaloniaGallery()
            : base("xaml-editing-")
        {
        }

        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(async () =>
            {
                await XamlTests.Gallery.MakeAsync(Folder);
                File.WriteAllText(PathOf("ViewModels/ShopViewModel.cs"), ShopViewModel);
                File.WriteAllText(PathOf("ViewModels/TillViewModel.cs"), TillViewModel);
                File.WriteAllText(PathOf("Mvvm.cs"), Mvvm);
                File.WriteAllText(PathOf("TillView.axaml"), """
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 xmlns:vm="using:Gallery.ViewModels"
                                 x:Class="Gallery.TillView"
                                 x:DataType="vm:TillViewModel">
                        <StackPanel>
                            <TextBlock Text="{Binding Name}" />
                        </StackPanel>
                    </UserControl>

                    """);
                File.WriteAllText(PathOf("TillView.axaml.cs"), "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class TillView : UserControl\n{\n}\n");
                File.WriteAllText(PathOf("ShopView.axaml"), """
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 xmlns:vm="using:Gallery.ViewModels"
                                 x:Class="Gallery.ShopView"
                                 x:DataType="vm:ShopViewModel">
                        <StackPanel>
                            <TextBlock Text="{Binding Name}" />
                        </StackPanel>
                    </UserControl>

                    """);
                File.WriteAllText(PathOf("ShopView.axaml.cs"), "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class ShopView : UserControl\n{\n}\n");
                Localize(this, "Avalonia");
            }, Folder);
            await PrepareAsync();
        }
    }

    /// <summary><see cref="WpfXamlTests.Gallery"/>'s app, with a shop view whose view model uses the MVVM Toolkit, and a greeting
    /// view of a Designer class's strings.</summary>
    public sealed class WpfGallery : SharedProject
    {
        public WpfGallery()
            : base("wpf-editing-")
        {
        }

        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(async () =>
            {
                await WpfXamlTests.Gallery.MakeAsync(Folder);
                File.WriteAllText(PathOf("ViewModels/ShopViewModel.cs"), ShopViewModel);
                File.WriteAllText(PathOf("Mvvm.cs"), Mvvm);
                File.WriteAllText(PathOf("ShopView.xaml"), """
                    <UserControl x:Class="Gallery.ShopView"
                                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                                 xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                                 xmlns:vm="clr-namespace:Gallery.ViewModels"
                                 mc:Ignorable="d"
                                 d:DataContext="{d:DesignInstance Type=vm:ShopViewModel}">
                        <StackPanel>
                            <TextBlock Text="{Binding Name}" />
                        </StackPanel>
                    </UserControl>

                    """);
                File.WriteAllText(PathOf("ShopView.xaml.cs"), "using System.Windows.Controls;\n\nnamespace Gallery;\n\npublic partial class ShopView : UserControl\n{\n}\n");
                Localize(this, "WPF");
            }, Folder);
            await PrepareAsync();
        }
    }
}
