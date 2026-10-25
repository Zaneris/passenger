using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Configuration known from what the app reads: appsettings.json and the project's secrets.json complete the
/// keys its options classes and reads name, Go to Declaration goes from a key to what reads it and back, a read of a
/// key nothing gives is warned of with its fixes, and Manage User Secrets gives a project its secrets in the run's own
/// folder.</summary>
public sealed class ConfigurationTests : IClassFixture<ConfigurationTests.Project>
{
    private readonly Project project;

    public ConfigurationTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>Completion in appsettings.json lists a section's keys from the options class bound to it (its
    /// properties, a renamed one by its configuration name, a nested class's) and from the code's reads, leaving out
    /// those the object has; a bool's key goes in with its value, and an enum's value offers its names. A key's card
    /// says what the code reads it as, a comment begun above it (a lone '/' so far) leaving it read as ever.</summary>
    [AvaloniaFact]
    public void Appsettings_completes_a_section_s_keys_from_its_options_class_and_an_enum_s_names()
    {
        MainWindow window = project.Open(project.PathOf("Shop/appsettings.json"));
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            string card = Hover(window, "\"Region\"");
            Assert.True(card.Contains("Payments:Region: string (PaymentOptions.Region)", StringComparison.Ordinal), $"Region's card: {card}");
            card = Hover(window, "\"Mode\"");
            Assert.True(card.Contains("One of Test, Live", StringComparison.Ordinal), $"Mode's card: {card}");
            Ui.Press(window, "ESCAPE");
            Ui.PlaceAfter(editor, "\"Payments\": {");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Enabled"), "the options class's keys to be offered in its section");
            IReadOnlyList<string> offered = window.Assist.CompletionLabels;
            foreach (string wanted in new[] { "Enabled", "Retry", "api_url", "ApiKey" })
            {
                Assert.True(offered.Contains(wanted), $"Payments' {wanted} was not offered: {string.Join(", ", offered)}");
            }
            foreach (string unwanted in new[] { "Region", "Mode", "Endpoints", "ApiUrl", "Banner", "Host" })
            {
                Assert.False(offered.Contains(unwanted), $"{unwanted} was offered in Payments, which has it already or never reads it: {string.Join(", ", offered)}");
            }
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(editor, "\"Mode\": ");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Live"), "the enum's names to be offered as Mode's value");
            Assert.Contains("Test", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE");

            Ui.PlaceAfter(editor, "\"Mail\": {");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Port"), "MailOptions' Port to be offered");
            Assert.DoesNotContain("Host", window.Assist.CompletionLabels);
            Ui.Press(window, "ENTER");
            Ui.Settle();
            Assert.Contains("\"Mail\": {\"Port\": 0,", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

            Ui.PlaceAfter(editor, "\"Payments\": {");
            Ui.Type(window, "SLASH", "/");
            card = Hover(window, "\"Region\"");
            Assert.True(card.Contains("Payments:Region: string", StringComparison.Ordinal), $"Region's card below a lone '/': {card}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Go to Declaration from a key in appsettings.json goes to the options property it sets, an agent's
    /// definition too, and from its section to where the section is bound; from a read in C# it goes to where
    /// appsettings.json gives the key.</summary>
    [AvaloniaFact]
    public void Go_to_Declaration_goes_from_a_key_to_its_property_and_from_a_read_to_where_it_is_given()
    {
        WebLanguagesTests.SkipWithoutNode();
        MainWindow window = project.Open(project.PathOf("Shop/appsettings.json"));
        try
        {
            GoesTo(window, "\"Ho", "Options.cs", "public string Host");
            window.OpenFile(project.PathOf("Shop/appsettings.json"));
            Ui.Settle();
            GoesTo(window, "\"Ma", "Program.cs", "BindConfiguration(\"Mail\")");
            window.OpenFile(project.PathOf("Shop/Program.cs"));
            Ui.Settle();
            GoesTo(window, "Configuration[\"Payments:Reg", "appsettings.json", "\"Region\": \"eu\"");

            int line = Project.Settings.Split('\n').ToList().FindIndex(l => l.Contains("\"Host\"", StringComparison.Ordinal)) + 1;
            Passenger.App.Mcp.PassengerTools agent = new(window);
            IReadOnlyList<Passenger.App.Mcp.Place> places = Ui.Await(agent.Definition("Shop/appsettings.json", line, 7), "an agent's definition of Mail:Host");
            Passenger.App.Mcp.Place place = Assert.Single(places);
            Assert.True(place.Path.EndsWith("Options.cs", StringComparison.Ordinal) && place.Text.Contains("public string Host", StringComparison.Ordinal),
                $"The agent's definition of Mail:Host went to {place.Path}:{place.Line}: {place.Text}");
        }
        finally
        {
            window.Close();
        }

        static void GoesTo(MainWindow window, string after, string file, string line)
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == file, $"Go to Declaration on {after}… to open {file} ({window.Status})");
            CodeEditor editor = window.EditorArea.CodeEditor;
            string landed = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
            Assert.True(landed.Contains(line, StringComparison.Ordinal), $"Go to Declaration on {after}… landed on: {landed}");
        }
    }

    /// <summary>A read of a key nothing the app runs with gives is warned of where the key is written; one with a default, one
    /// checked for null, a value type's and one a launch profile gives are not. Its fix asks the value and sets it in
    /// appsettings.json, or for a secret's name (masked as typed) in user secrets, giving the project a UserSecretsId first,
    /// each one undo step; the warning goes once the key is given. An agent's code_actions lists the same fix first and
    /// apply_code_action makes it with the value given as name.</summary>
    [AvaloniaFact]
    public void A_key_nothing_gives_is_warned_of_and_its_fixes_set_it_in_appsettings_or_user_secrets()
    {
        string settings = project.PathOf("Shop/appsettings.json");
        string projectFile = project.PathOf("Shop/Shop.csproj");
        string writtenSettings = File.ReadAllText(settings);
        string writtenProject = File.ReadAllText(projectFile);
        MainWindow window = project.Open(project.PathOf("Shop/Program.cs"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        string? secretsId = null;
        try
        {
            Ui.WaitFor(() => Missing(window).SetEquals(["Payments:ApiKey", "Shop:Banner"]),
                $"the keys nothing gives, and only those, to be warned of: {string.Join(", ", Missing(window))}");

            Set(window, "Shop:Ban", "Add Shop:Banner to appsettings.json…", secret: false, "Welcome");
            Ui.WaitFor(() => File.ReadAllText(settings).Contains("\"Banner\": \"Welcome\"", StringComparison.Ordinal), "the key to be set in appsettings.json");
            Assert.Equal(writtenSettings.Replace("\"Host\": \"smtp.example\"\n  }\n", "\"Host\": \"smtp.example\"\n  },\n  \"Shop\": {\n    \"Banner\": \"Welcome\"\n  }\n",
                StringComparison.Ordinal), File.ReadAllText(settings));
            Ui.WaitFor(() => Missing(window).SetEquals(["Payments:ApiKey"]), "the warning to go once appsettings.json gives the key");
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(settings) == writtenSettings, "undo to take the key back out of appsettings.json");

            Set(window, "\"Payments:ApiK", "Add Payments:ApiKey to user secrets…", secret: true, "s3cret-value");
            Ui.WaitFor(() => ProjectEditsPropertyIn(File.ReadAllText(projectFile)) is not null, "the project to be given a UserSecretsId");
            secretsId = ProjectEditsPropertyIn(File.ReadAllText(projectFile))!;
            string secrets = Path.Combine(TestRun.UserSecrets, secretsId, "secrets.json");
            Ui.WaitFor(() => File.Exists(secrets) && File.ReadAllText(secrets).Contains("\"Payments:ApiKey\": \"s3cret-value\"", StringComparison.Ordinal),
                "the secret to be kept in the project's user secrets, in the run's own folder");
            // Shop:Banner's, which undo brought back, stays.
            Ui.WaitFor(() => Missing(window).SetEquals(["Shop:Banner"]), $"the secret's warning to go once user secrets give it: {string.Join(", ", Missing(window))}");
            Assert.DoesNotContain("s3cret-value", window.Status ?? string.Empty, StringComparison.Ordinal);
            string card = Hover(window, "Payments:ApiKey\"");
            Assert.True(card.Contains("user secrets = ••••••", StringComparison.Ordinal) && !card.Contains("s3cret-value", StringComparison.Ordinal),
                $"The read's card did not say the user secret masked: {card}");
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(projectFile) == writtenProject && !File.Exists(secrets), "undo to take back the UserSecretsId and the secrets it made");

            Ui.WaitFor(() => Missing(window).Contains("Shop:Banner"), "the warning to come back once undo took the key away");
            Passenger.App.Mcp.PassengerTools agent = new(window);
            string text = File.ReadAllText(project.PathOf("Shop/Program.cs"));
            int at = text.IndexOf("Shop:Ban", StringComparison.Ordinal) + "Shop:Ban".Length;
            int line = text[..at].Count(c => c == '\n') + 1;
            int column = at - text.LastIndexOf('\n', at - 1);
            IReadOnlyList<string> actions = Ui.Await(agent.CodeActions("Shop/Program.cs", line, column), "the code actions an agent asks for");
            Assert.Equal("Add Shop:Banner to appsettings.json… [quickfix]", actions[0]);
            string applied = Ui.Await(agent.ApplyCodeAction("Shop/Program.cs", line, column, "Add Shop:Banner to appsettings.json…", name: "Hi"), "the agent's fix made");
            Assert.Equal("Added Shop:Banner to Shop's appsettings.json", applied);
            Assert.Contains("\"Banner\": \"Hi\"", File.ReadAllText(settings), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            if (secretsId is not null && Directory.Exists(Path.Combine(TestRun.UserSecrets, secretsId)))
            {
                Directory.Delete(Path.Combine(TestRun.UserSecrets, secretsId), recursive: true);
            }
        }

        static void Set(MainWindow window, string after, string fix, bool secret, string value)
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains(fix), $"{fix} to be offered");
            Assert.Equal(fix, window.Assist.Offered[0]);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), $"{fix} to ask for the value");
            ConfirmDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>()));
            TextBox box = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
            Assert.True(secret == (box.PasswordChar != default), $"{fix}'s box {(secret ? "shows a secret as it is typed" : "masks a value that is no secret")}");
            dialog.KeyTextInput(value);
            Ui.Press(dialog, "ENTER");
            Ui.Settle();
        }

        static HashSet<string> Missing(MainWindow window) => [.. window.EditorArea.Active!.Markers
            .Where(m => m.Message.StartsWith("Nothing Shop runs with gives the configuration key '", StringComparison.Ordinal)).Select(m => m.Message.Split('\'')[1])];
    }

    /// <summary>Manage User Secrets on the project's row gives a project with none a UserSecretsId and opens its secrets.json,
    /// made in the run's own user secrets folder, never the person's; its keys complete there as appsettings.json's do, and
    /// asked again it opens the same file, the project left as it is.</summary>
    [AvaloniaFact]
    public void Manage_User_Secrets_gives_the_project_its_secrets_and_opens_them()
    {
        string projectFile = project.PathOf("Shop/Shop.csproj");
        MainWindow window = project.Open(project.PathOf("Shop/Program.cs"));
        string? secretsId = null;
        try
        {
            window.ExplorerView.Reveal(PathNames.Canonical(projectFile));
            Ui.Settle();
            ExplorerNode row = window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Project && r.Path == PathNames.Canonical(projectFile));
            Ui.ChooseFromRowMenu(window, row, "Manage User Secrets");
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "secrets.json", $"the project's secrets.json to open ({window.Status})");
            secretsId = ProjectEditsPropertyIn(File.ReadAllText(projectFile));
            Assert.True(Guid.TryParse(secretsId, out _), $"The project was not given a GUID for its UserSecretsId: {secretsId}");
            string secrets = PathNames.Canonical(Path.Combine(TestRun.UserSecrets, secretsId!, "secrets.json"));
            Assert.True(PathNames.Same(secrets, window.EditorArea.Active!.Path), $"The secrets opened are not under the run's own folder: {window.EditorArea.Active.Path}");
            Assert.Equal("{\n}\n", File.ReadAllText(secrets));

            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "{");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Payments"), "the code's sections to be offered in the project's secrets.json");
            foreach (string wanted in new[] { "Mail", "Shop", "Greeting" })
            {
                Assert.Contains(wanted, window.Assist.CompletionLabels);
            }
            Ui.Press(window, "ESCAPE");

            string given = File.ReadAllText(projectFile);
            window.OpenFile(project.PathOf("Shop/Program.cs"));
            Ui.Settle();
            Ui.Run(window, AppActions.FindAction);
            window.KeyTextInput("Manage User Secrets");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "secrets.json", $"the same secrets.json to open again ({window.Status})");
            Assert.True(PathNames.Same(secrets, window.EditorArea.Active!.Path), $"Asked again, another secrets.json opened: {window.EditorArea.Active.Path}");
            Assert.Equal(given, File.ReadAllText(projectFile));
        }
        finally
        {
            window.Close();
            if (secretsId is not null && Directory.Exists(Path.Combine(TestRun.UserSecrets, secretsId)))
            {
                Directory.Delete(Path.Combine(TestRun.UserSecrets, secretsId), recursive: true);
            }
        }
    }

    private static string? ProjectEditsPropertyIn(string text) => ProjectEdits.PropertyIn(text, "UserSecretsId");

    /// <summary>The card shown with the pointer resting on some text of the editor's.</summary>
    private static string Hover(MainWindow window, string text)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Press(window, "ESCAPE");
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, $"the card on {text}");
        return window.Assist.HoverText!;
    }

    public sealed class Project : SharedProject
    {
        internal const string Program = """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payments"));
            builder.Services.AddOptions<MailOptions>().BindConfiguration("Mail");
            var region = builder.Configuration["Payments:Region"];
            var key = builder.Configuration.GetValue<string>("Payments:ApiKey");
            var banner = builder.Configuration["Shop:Banner"];
            var theme = builder.Configuration["Shop:Theme"];
            var greeting = builder.Configuration["Greeting"] ?? "Hello";
            var port = builder.Configuration.GetValue<int>("Shop:Port");
            var mode = builder.Configuration["Shop:Mode"];
            if (mode is null)
            {
                mode = "plain";
            }
            var app = builder.Build();
            app.MapGet("/", () => $"{region}{key}{banner}{theme}{greeting}{port}{mode}");
            app.Run();

            """;

        internal const string Options = """
            using Microsoft.Extensions.Configuration;

            public enum PaymentMode
            {
                Test,
                Live,
            }

            public sealed class PaymentOptions
            {
                public string Region { get; set; } = "";
                public bool Enabled { get; set; }
                public PaymentMode Mode { get; set; }
                public RetryOptions Retry { get; set; } = new();
                public List<EndpointOptions> Endpoints { get; set; } = [];
                [ConfigurationKeyName("api_url")]
                public string? ApiUrl { get; set; }
            }

            public sealed class RetryOptions
            {
                public int Count { get; set; }
            }

            public sealed class EndpointOptions
            {
                public string Url { get; set; } = "";
            }

            public sealed class MailOptions
            {
                public string Host { get; set; } = "";
                public int Port { get; set; }
            }

            """;

        internal const string Settings = """
            {
              "Payments": {
                "Region": "eu",
                "Mode": "Test",
                "Endpoints": [
                  { "Url": "https://pay.example" }
                ]
              },
              "Mail": {
                "Host": "smtp.example"
              }
            }

            """;

        public Project() : base("configuration-")
        {
            TestFolders.Write(Folder, "Shop.slnx", "<Solution><Project Path=\"Shop/Shop.csproj\" /></Solution>");
            TestFolders.Write(Folder, "Shop/Shop.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>"));
            TestFolders.Write(Folder, "Shop/Program.cs", Program);
            TestFolders.Write(Folder, "Shop/Options.cs", Options);
            TestFolders.Write(Folder, "Shop/appsettings.json", Settings);
            // A launch profile's variable gives Shop:Theme.
            TestFolders.Write(Folder, "Shop/Properties/launchSettings.json", """
                {
                  "profiles": {
                    "http": {
                      "commandName": "Project",
                      "applicationUrl": "http://localhost:5180",
                      "environmentVariables": {
                        "ASPNETCORE_ENVIRONMENT": "Development",
                        "Shop__Theme": "dark"
                      }
                    }
                  }
                }

                """);
            Prepare();
        }
    }
}
