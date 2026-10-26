using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Localized strings known from the code that reads them: an <c>IStringLocalizer&lt;T&gt;</c>'s keys in C# and
/// in a Razor component's <c>@inject</c>ed localizer, found in the .resx files ASP.NET Core reads for T (one a
/// language, beside the type, no neutral one); a generated resource class's properties (a Designer file's, its neutral
/// .resx and a satellite) and one whose translations are XLIFF files in xlf/ as Arcade's XliffTasks keeps them. A key a
/// language lacks is warned of with a fix that asks the value and adds it as the file writes its entries, one undo
/// step; a key's card says each language's value, Go to Declaration goes to each file's entry, completion in a
/// localizer's string offers the keys, and an agent's diagnostics, code_actions, apply_code_action and definition give
/// the same. A component whose files are named for its file rather than its type, which its localizer never reads, is
/// warned of.</summary>
public sealed class LocalizationTests : IClassFixture<LocalizationTests.Project>
{
    private readonly Project project;

    public LocalizationTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>A C# localizer's key Spanish lacks is warned of, and only it; its fix asks the value and adds the entry to
    /// Greeter.es.resx as that file writes its entries, the warning going, and Undo takes it out again. Completion in the
    /// localizer's string offers the resource's keys and replaces the key.</summary>
    [AvaloniaFact]
    public void A_key_a_language_lacks_is_warned_of_and_its_fix_adds_it_as_the_file_writes_its_entries()
    {
        string spanish = project.PathOf("Shop/Services/Greeter.es.resx");
        string written = File.ReadAllText(spanish);
        MainWindow window = project.Open(project.PathOf("Shop/Services/Greeter.cs"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        try
        {
            Ui.WaitFor(() => Missing(window).SetEquals(["'Bye' has no Spanish translation (Greeter.es.resx)"]),
                $"the key Spanish lacks, and only it, to be warned of: {string.Join("; ", Missing(window))}");

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "GetString(\"By");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Add 'Bye' to Greeter.es.resx…"), "the fix to be offered");
            Assert.Equal("Add 'Bye' to Greeter.es.resx…", window.Assist.Offered[0]);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the fix to ask for the value");
            ConfirmDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>()));
            Assert.Equal(default, Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>()).PasswordChar);
            dialog.KeyTextInput("Adiós & <hasta luego>");
            Ui.Press(dialog, "ENTER");
            Ui.Settle();
            string entry = "  <data name=\"Bye\" xml:space=\"preserve\">\n    <value>Adiós &amp; &lt;hasta luego&gt;</value>\n  </data>\n";
            Ui.WaitFor(() => File.ReadAllText(spanish) == written.Replace("</root>", entry + "</root>", StringComparison.Ordinal),
                $"the entry to be added after the file's last, as it writes them: {File.ReadAllText(spanish)}");
            Ui.WaitFor(() => Missing(window).Count == 0, $"the warning to go once Spanish gives the key: {string.Join("; ", Missing(window))}");
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(spanish) == written, () => $"undo to take the entry back out ({window.Status}): {File.ReadAllText(spanish)}");
            Ui.WaitFor(() => Missing(window).Count == 1, "the warning to come back once undo took the entry away");

            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.Click(editor);
            Ui.PlaceAfter(editor, "localizer[\"");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Bye"), "the resource's keys to be offered in the localizer's string");
            Assert.Equal(["Bye", "Hello"], [.. window.Assist.CompletionLabels.Take(2).Order(StringComparer.Ordinal)]);
            window.KeyTextInput("By");
            Ui.Settle();
            Assert.Equal("Bye", window.Assist.CompletionLabels.FirstOrDefault());
            Ui.Press(window, "ENTER");
            Ui.Settle();
            Assert.Contains("public string Hello => localizer[\"Bye\"];", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>In a Razor component, the key read through its <c>@inject</c>ed localizer: its card says each language's value and
    /// that Spanish has none of Goodbye, which is warned of; Go to Declaration lists each language's entry of Welcome, and
    /// completion after <c>@L["</c> offers the component's keys. An agent's diagnostics lists the warning, its code_actions
    /// the fix first, and apply_code_action adds the value it names; its definition goes to both entries. A component whose
    /// .resx files are named for its file (Sign-In) rather than its type (Sign_In) is told its localizer reads none of them.</summary>
    [AvaloniaFact]
    public void A_razor_component_s_keys_have_their_languages_on_the_card_go_to_their_entries_and_an_agent_adds_one()
    {
        string spanish = project.PathOf("Shop/Pages/Home.es.resx");
        MainWindow window = project.Open(project.PathOf("Shop/Pages/Home.razor"));
        try
        {
            Ui.WaitFor(() => Missing(window).SetEquals(["'Goodbye' has no Spanish translation (Home.es.resx)"]),
                $"the component's key Spanish lacks to be warned of: {string.Join("; ", Missing(window))}");
            string card = Hover(window, "Welcome\"]");
            Assert.True(card.Contains("fr: Bienvenue", StringComparison.Ordinal) && card.Contains("es: Bienvenido", StringComparison.Ordinal), $"Welcome's card: {card}");
            card = Hover(window, "Goodbye\"]");
            Assert.True(card.Contains("fr: Au revoir", StringComparison.Ordinal) && card.Contains("es: none (Home.es.resx)", StringComparison.Ordinal), $"Goodbye's card: {card}");
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "@L[\"Wel");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.Assist.Offered.Count == 2, $"each language's entry to be offered ({window.Status})");
            Assert.Equal(["es: Home.es.resx", "fr: Home.fr.resx"], window.Assist.Offered);
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Home.fr.resx", "the French entry to open");
            CodeEditor opened = window.EditorArea.CodeEditor;
            Assert.Contains("<data name=\"Welcome\"", opened.Document.Snapshot.GetLineText(opened.CaretPosition.Line), StringComparison.Ordinal);

            window.OpenFile(project.PathOf("Shop/Pages/Home.razor"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "<p>@L[\"");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Welcome"), "the component's keys to be offered after @L[\"");
            Assert.Contains("Goodbye", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE");

            Passenger.App.Mcp.PassengerTools agent = new(window);
            IReadOnlyList<Passenger.App.Mcp.Finding> findings = (IReadOnlyList<Passenger.App.Mcp.Finding>)Ui.Await(agent.Diagnostics("Shop/Pages/Home.razor"), "an agent's diagnostics of the component");
            Passenger.App.Mcp.Finding missing = Assert.Single(findings, f => f.Message.Contains("translation", StringComparison.Ordinal));
            Assert.Equal(("warning", "'Goodbye' has no Spanish translation (Home.es.resx)"), (missing.Severity, missing.Message));
            IReadOnlyList<string> actions = Ui.Await(agent.CodeActions("Shop/Pages/Home.razor", missing.Line, missing.Column + 2), "the code actions an agent asks for");
            Assert.Equal("Add 'Goodbye' to Home.es.resx… [quickfix]", actions[0]);
            string applied = Ui.Await(agent.ApplyCodeAction("Shop/Pages/Home.razor", missing.Line, missing.Column + 2, "Add 'Goodbye' to Home.es.resx…", name: "Adiós"),
                "the agent's fix made");
            Assert.Equal("Added 'Goodbye' to Home.es.resx", applied);
            Assert.Contains("<data name=\"Goodbye\" xml:space=\"preserve\"><value>Adiós</value></data>", File.ReadAllText(spanish), StringComparison.Ordinal);

            string text = File.ReadAllText(project.PathOf("Shop/Pages/Home.razor"));
            int at = text.IndexOf("Welcome", StringComparison.Ordinal) + 2;
            int line = text[..at].Count(c => c == '\n') + 1;
            IReadOnlyList<Passenger.App.Mcp.Place> places = Ui.Await(agent.Definition("Shop/Pages/Home.razor", line, at - text.LastIndexOf('\n', at - 1)),
                "an agent's definition of Welcome");
            Assert.Equal(2, places.Count);
            Assert.All(places, p => Assert.Contains("<data name=\"Welcome\"", p.Text, StringComparison.Ordinal));

            window.OpenFile(project.PathOf("Shop/Pages/Sign-In.razor"));
            Ui.WaitFor(() => window.EditorArea.Active!.Markers.Any(m => m.Message.StartsWith("No .resx gives", StringComparison.Ordinal)),
                "the files its localizer never reads to be warned of");
            Assert.Equal("No .resx gives Sign_In's strings, so each shows as written: Sign-In.fr.resx is named for Sign-In, not Sign_In",
                Assert.Single(window.EditorArea.Active!.Markers, m => m.Kind == MarkerKind.Warning).Message);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A generated resource class: its neutral .resx gives each key, so a satellite lacking one is warned of; its card says
    /// the neutral value and each language's, and Go to Declaration offers the Designer's property beside each file's entry. A
    /// class whose translations are XLIFF files: a key with no unit and one still new are warned of in their own words, and
    /// their fixes add the unit as XliffTasks writes it, in order of the ids, and fill in the new one's target, made translated.</summary>
    [AvaloniaFact]
    public void A_generated_resource_class_s_keys_and_xliff_translations_are_warned_of_and_translated()
    {
        string french = project.PathOf("Shop/xlf/Strings.fr.xlf");
        MainWindow window = project.Open(project.PathOf("Shop/Program.cs"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        try
        {
            Ui.WaitFor(() => Missing(window).SetEquals(["'Thanks' has no French translation (Resources.fr.resx)", "'Hello' has no French translation (Strings.fr.xlf)",
                "'Later' is not translated to French yet (Strings.fr.xlf)"]), $"the keys French lacks to be warned of: {string.Join("; ", Missing(window))}");
            string card = Hover(window, "Welcome +");
            Assert.True(card.Contains("neutral: Welcome!", StringComparison.Ordinal) && card.Contains("fr: Bienvenue", StringComparison.Ordinal), $"Welcome's card: {card}");
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Resources.Wel");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.Assist.Offered.Count == 3, $"the property and each file's entry to be offered ({window.Status}): {string.Join("; ", window.Assist.Offered)}");
            Assert.Equal(["Resources.Welcome", "neutral: Resources.resx", "fr: Resources.fr.resx"], window.Assist.Offered);
            Ui.Press(window, "ESCAPE");

            Translate(window, "Strings.Lat", "Translate 'Later' in Strings.fr.xlf…", "À plus tard");
            Ui.WaitFor(() => File.ReadAllText(french).Contains("<target state=\"translated\">À plus tard</target>", StringComparison.Ordinal),
                $"the new unit's target to be filled in and made translated: {File.ReadAllText(french)}");
            Translate(window, "Strings.Hel", "Add 'Hello' to Strings.fr.xlf…", "Bonjour");
            Ui.WaitFor(() => File.ReadAllText(french) == Project.Xliff
                .Replace("<target state=\"new\">See you later</target>", "<target state=\"translated\">À plus tard</target>", StringComparison.Ordinal)
                .Replace("      <trans-unit id=\"Later\">", "      <trans-unit id=\"Hello\">\n        <source>Hello there</source>\n"
                    + "        <target state=\"translated\">Bonjour</target>\n        <note />\n      </trans-unit>\n      <trans-unit id=\"Later\">", StringComparison.Ordinal),
                $"the unit to be added as XliffTasks writes it, among the others by id: {File.ReadAllText(french)}");
            Ui.WaitFor(() => Missing(window).SetEquals(["'Thanks' has no French translation (Resources.fr.resx)"]),
                $"the XLIFF file's warnings to go once it translates the keys: {string.Join("; ", Missing(window))}");
        }
        finally
        {
            window.Close();
        }

        static void Translate(MainWindow window, string after, string fix, string value)
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains(fix), $"{fix} to be offered: {string.Join("; ", window.Assist.Offered)}");
            Assert.Equal(fix, window.Assist.Offered[0]);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), $"{fix} to ask for the value");
            ConfirmDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>()));
            dialog.KeyTextInput(value);
            Ui.Press(dialog, "ENTER");
            Ui.Settle();
        }
    }

    /// <summary>Under the ResourcesPath the app's localization options set, a localizer's files are those in that folder, its
    /// type's name less the root namespace as a path, as ASP.NET Core's factory reads them; those beside the type no longer count.</summary>
    [AvaloniaFact]
    public void Under_the_app_s_ResourcesPath_a_localizer_s_files_are_those_in_that_folder()
    {
        Directory.CreateDirectory(project.PathOf("Shop/Resources/Services"));
        project.Write("Shop/Resources/Services/Greeter.de.resx", Project.Resx(oneLine: false, ("Hello", "Hallo")));
        project.Rewrite("Shop/Program.cs", Project.Program.Replace("AddLocalization()", "AddLocalization(options => options.ResourcesPath = \"Resources\")", StringComparison.Ordinal));
        MainWindow window = project.Open(project.PathOf("Shop/Services/Greeter.cs"));
        try
        {
            Ui.WaitFor(() => Missing(window).SetEquals(["'Bye' has no German translation (Greeter.de.resx)"]),
                $"the key the Resources folder's German lacks, and only it, to be warned of: {string.Join("; ", Missing(window))}");
            string card = Hover(window, "Hello\"]", "de: ");
            Assert.True(card.Contains("de: Hallo", StringComparison.Ordinal) && !card.Contains("fr: ", StringComparison.Ordinal), $"Hello's card: {card}");
        }
        finally
        {
            window.Close();
        }
    }

    private static HashSet<string> Missing(MainWindow window) => [.. window.EditorArea.Active!.Markers
        .Where(m => m.Kind == MarkerKind.Warning && m.Message.Contains(" translat", StringComparison.Ordinal)).Select(m => m.Message)];

    /// <summary>The card shown with the pointer resting on some text of the editor's.</summary>
    private static string Hover(MainWindow window, string text, string until = "fr: ")
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Press(window, "ESCAPE");
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText?.Contains(until, StringComparison.Ordinal) == true, $"the card on {text}: {window.Assist.HoverText}");
        return window.Assist.HoverText!;
    }

    /// <summary>A web app localized three ways: a component and a service read through IStringLocalizer&lt;T&gt;, their
    /// languages' .resx beside them; a Designer class over a neutral .resx and a French one; and a class whose French is an
    /// XLIFF file.</summary>
    public sealed class Project : SharedProject
    {
        internal const string Program = """
            using Shop;
            using Shop.Properties;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddLocalization();
            var app = builder.Build();
            app.MapGet("/", () => Resources.Welcome + Resources.Thanks + Strings.Farewell + Strings.Hello + Strings.Later);
            app.Run();

            """;

        internal const string Greeter = """
            using Microsoft.Extensions.Localization;

            namespace Shop.Services;

            public sealed class Greeter(IStringLocalizer<Greeter> localizer)
            {
                public string Hello => localizer["Hello"];

                public string Bye => localizer.GetString("Bye");
            }

            """;

        internal const string Home = """
            @using Microsoft.Extensions.Localization
            @inject IStringLocalizer<Home> L

            <h1>@L["Welcome"]</h1>
            <p>@L["Goodbye"]</p>

            """;

        /// <summary>As ResXFileCodeGenerator writes a Designer class.</summary>
        internal const string Designer = """
            namespace Shop.Properties {
                using System;

                [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "17.0.0.0")]
                [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
                [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
                internal class Resources {

                    private static global::System.Resources.ResourceManager resourceMan;

                    private static global::System.Globalization.CultureInfo resourceCulture;

                    internal Resources() {
                    }

                    internal static global::System.Resources.ResourceManager ResourceManager {
                        get {
                            if (object.ReferenceEquals(resourceMan, null)) {
                                global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Shop.Properties.Resources", typeof(Resources).Assembly);
                                resourceMan = temp;
                            }
                            return resourceMan;
                        }
                    }

                    internal static string Thanks {
                        get {
                            return ResourceManager.GetString("Thanks", resourceCulture);
                        }
                    }

                    internal static string Welcome {
                        get {
                            return ResourceManager.GetString("Welcome", resourceCulture);
                        }
                    }
                }
            }

            """;

        /// <summary>As Arcade's GenerateResxSource writes a class over a .resx.</summary>
        internal const string Strings = """
            namespace Shop;

            internal static partial class Strings
            {
                private static global::System.Resources.ResourceManager? manager;

                internal static global::System.Resources.ResourceManager ResourceManager => manager ??= new global::System.Resources.ResourceManager(typeof(Strings));

                internal static string GetResourceString(string key) => ResourceManager.GetString(key) ?? key;

                internal static string @Farewell => GetResourceString("Farewell");

                internal static string @Hello => GetResourceString("Hello");

                internal static string @Later => GetResourceString("Later");
            }

            """;

        internal const string Xliff = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="1.2" xsi:schemaLocation="urn:oasis:names:tc:xliff:document:1.2 xliff-core-1.2-transitional.xsd">
              <file datatype="xml" source-language="en" target-language="fr" original="../Strings.resx">
                <body>
                  <trans-unit id="Farewell">
                    <source>Farewell</source>
                    <target state="translated">Adieu</target>
                    <note />
                  </trans-unit>
                  <trans-unit id="Later">
                    <source>See you later</source>
                    <target state="new">See you later</target>
                    <note />
                  </trans-unit>
                </body>
              </file>
            </xliff>

            """;

        public Project() : base("localization-")
        {
            TestFolders.Write(Folder, "Shop.slnx", "<Solution><Project Path=\"Shop/Shop.csproj\" /></Solution>");
            TestFolders.Write(Folder, "Shop/Shop.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
            TestFolders.Write(Folder, "Shop/Program.cs", Program);
            TestFolders.Write(Folder, "Shop/Services/Greeter.cs", Greeter);
            TestFolders.Write(Folder, "Shop/Pages/Home.razor", Home);
            // Villagely's way: one line an entry, no neutral file.
            TestFolders.Write(Folder, "Shop/Pages/Home.fr.resx", Resx(oneLine: true, ("Welcome", "Bienvenue"), ("Goodbye", "Au revoir")));
            TestFolders.Write(Folder, "Shop/Pages/Home.es.resx", Resx(oneLine: true, ("Welcome", "Bienvenido")));
            TestFolders.Write(Folder, "Shop/Pages/Sign-In.razor", "@using Microsoft.Extensions.Localization\n@inject IStringLocalizer<Sign_In> L\n\n<h1>@L[\"Sign in\"]</h1>\n");
            TestFolders.Write(Folder, "Shop/Pages/Sign-In.fr.resx", Resx(oneLine: true, ("Sign in", "Se connecter")));
            // Visual Studio's way: three lines an entry.
            TestFolders.Write(Folder, "Shop/Services/Greeter.fr.resx", Resx(oneLine: false, ("Hello", "Bonjour"), ("Bye", "Au revoir")));
            TestFolders.Write(Folder, "Shop/Services/Greeter.es.resx", Resx(oneLine: false, ("Hello", "Hola")));
            TestFolders.Write(Folder, "Shop/Properties/Resources.Designer.cs", Designer);
            TestFolders.Write(Folder, "Shop/Properties/Resources.resx", Resx(oneLine: false, ("Thanks", "Thank you"), ("Welcome", "Welcome!")));
            TestFolders.Write(Folder, "Shop/Properties/Resources.fr.resx", Resx(oneLine: false, ("Welcome", "Bienvenue")));
            TestFolders.Write(Folder, "Shop/Strings.cs", Strings);
            TestFolders.Write(Folder, "Shop/Strings.resx", Resx(oneLine: false, ("Farewell", "Farewell"), ("Hello", "Hello there"), ("Later", "See you later")));
            TestFolders.Write(Folder, "Shop/xlf/Strings.fr.xlf", Xliff);
        }

        internal static string Resx(bool oneLine, params (string Name, string Value)[] entries) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n" + string.Concat(entries.Select(e => oneLine
                ? $"  <data name=\"{e.Name}\" xml:space=\"preserve\"><value>{e.Value}</value></data>\n"
                : $"  <data name=\"{e.Name}\" xml:space=\"preserve\">\n    <value>{e.Value}</value>\n  </data>\n")) + "</root>\n";
    }
}
