using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Dockerfiles and compose files through the window, on a solution of an ASP.NET Core app using Npgsql, a worker
/// and a library: a Dockerfile completes and is checked by the Dockerfile server, a compose file by the YAML server with
/// the Compose specification's schema, completes the names the file declares where a service names them, Go to
/// Declaration in a compose file goes where Passenger finds each name leads, and Rename renames one where it is declared
/// and used, its override's too;
/// Add > Docker Support writes a project's Dockerfile and the context's .dockerignore, or sets the SDK's container
/// properties, and Add > Docker Compose the solution's compose.yaml, each one command Undo takes back. The servers' tests
/// skip, saying why, without a Node.js that runs them; the folder is never restored (its nuget.config names no feed that
/// is there), so nothing reaches nuget.org.</summary>
public sealed class DockerFilesTests : IClassFixture<DockerFilesTests.Shop>
{
    private readonly Shop app;

    public DockerFilesTests(Shop app) => this.app = app;

    [AvaloniaFact]
    public void A_dockerfile_completes_an_instruction_and_marks_one_docker_does_not_have()
    {
        Shop.SkipWithoutNode();
        MainWindow window = app.Open("ops/api.Dockerfile");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Assert.Equal("Dockerfile", document.Language.Name);

            Ui.PlaceAfter(editor, "FROM mcr.microsoft.com/dotnet/asp");
            Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
            Ui.WaitFor(() => window.Browsed.Count > 0, "the aspnet image's page opened");
            Assert.Equal("https://mcr.microsoft.com/artifact/mar/dotnet/aspnet", window.Browsed[^1].AbsoluteUri);
            Ui.PlaceAfter(editor, "FROM ba");
            Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
            Ui.WaitFor(() => editor.CaretPosition == new TextPosition(0, 45), $"the caret at the base stage's name, not {editor.CaretPosition}");
            Assert.Single(window.Browsed);

            editor.Selections = SelectionSet.Single(editor.Document.Snapshot.Length);
            TypeEach(window, "WORK");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Any(l => l.StartsWith("WORKDIR", StringComparison.Ordinal)), "the Dockerfile server's instructions as one is typed");
            Ui.Press(window, "ESCAPE");

            // The typed WORK's error, carried through the text replaced, covers it all until the server's push for the new text lands.
            editor.ApplyEdits([new TextEdit(0, editor.Document.Snapshot.Length, Shop.OpsDockerfile + "FROMM scratch\n")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error && m.Message.Contains("FROMM", StringComparison.Ordinal)),
                $"the error under the instruction Docker has not, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]");
            Marker error = document.Markers.First(m => m.Kind == MarkerKind.Error && m.Message.Contains("FROMM", StringComparison.Ordinal));
            Assert.Equal("FROMM", editor.Document.Snapshot.GetText(error.Range.Start, error.Range.Length));
            Assert.Contains("Unknown instruction: FROMM", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    /// <summary>Settings' Complete image tags from Docker Hub, on at first: after FROM image: the Dockerfile server offers the
    /// image's tags, asked of Docker Hub (a stand-in on loopback here); turned off on the Docker page, beside Remote JSON
    /// Schemas, the server starts again and asks Docker Hub nothing, offering no tags.</summary>
    [AvaloniaFact]
    public void A_dockerfile_completes_an_images_tags_from_docker_hub_and_asks_it_nothing_once_settings_turn_that_off()
    {
        Shop.SkipWithoutNode();
        using StandInHub hub = new();
        MainWindow window = app.Open("ops/api.Dockerfile", hub.Place);
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Assert.True(window.Settings.DockerHubTags, "Complete image tags from Docker Hub is off in a fresh Passenger");
            editor.ApplyEdits([new TextEdit(0, editor.Document.Snapshot.Length, "FROM postgres:1\n" + Shop.OpsDockerfile)]);
            Ui.PlaceAfter(editor, "FROM postgres:1");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("17-alpine"), $"postgres's tags from Docker Hub (asked: {string.Join(", ", hub.Asked)})");
            Assert.Equal(["16", "17", "17-alpine"], window.Assist.CompletionLabels.Order(StringComparer.Ordinal));
            Assert.Equal([TokenAsked, TagsAsked], hub.Asked);
            Ui.Press(window, "ESCAPE");

            SettingsDialog dialog = DialogButtonsTests.Check(window.ShowSettings());
            dialog.Query = "docker hub";
            Ui.Settle();
            Assert.Equal([SettingsDialog.DockerPage], dialog.Pages);
            Assert.Equal([HubTags], dialog.Shown);
            CheckBox asks = Editor<CheckBox>(dialog, HubTags);
            Assert.True(asks.IsChecked == true, "Settings shows Complete image tags from Docker Hub off while it is on");
            Ui.Click(asks);
            dialog.Ok();
            Ui.Settle();
            Assert.False(window.Settings.DockerHubTags, "Clearing Complete image tags from Docker Hub left it on");

            editor.Selections = SelectionSet.Single(editor.Document.Snapshot.Length);
            TypeEach(window, "WORK");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Any(l => l.StartsWith("WORKDIR", StringComparison.Ordinal)), "the Dockerfile server started again answering");
            Ui.Press(window, "ESCAPE");
            Ui.PlaceAfter(editor, "FROM postgres:1");
            Ui.Run(window, AppActions.Complete);
            Ui.Settle();
            Assert.True(window.Assist.CompletionLabels.Count == 0,
                $"With Complete image tags from Docker Hub off, FROM postgres:1 still offered [{string.Join(", ", window.Assist.CompletionLabels)}]");
            Assert.True(hub.Asked.Count == 2, $"Docker Hub was asked with Complete image tags from Docker Hub off: {string.Join(", ", hub.Asked.Skip(2))}");
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    /// <summary>The Dockerfile server checks each text it is sent and names no version: its push for a text the person has
    /// typed over by the time the window takes it is that text's, never shown as the text now nor letting the file's
    /// checks go, so once they land the markers are the last text's.</summary>
    [AvaloniaFact]
    public void A_dockerfile_typed_over_before_its_servers_push_is_taken_is_never_marked_for_the_text_before()
    {
        Shop.SkipWithoutNode();
        MainWindow window = app.Open("ops/api.Dockerfile");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => window.Session.Web?.For(document) is { Lsp: not null } && !document.IsChecking, "the Dockerfile server's first checks");
            Passenger.Lsp.LspDocument lsp = window.Session.Web!.For(document)!.Lsp!;
            int pushes = 0;
            int now = -1;
            bool taken = false;
            List<string> shown = [];
            using ManualResetEventSlim release = new();
            void Counted(object? sender, EventArgs e)
            {
                if (Interlocked.Increment(ref pushes) == 1)
                {
                    // Runs after the window's own take of this push, posted before it; the server's next push is read once it has.
                    UiThread.Post(() => taken = true);
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            }
            void Watched(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(OpenDocument.Markers) && document.Text.Version == now)
                {
                    shown.AddRange(document.Markers.Select(m => m.Message));
                }
            }
            lsp.DiagnosticsPushed += Counted;
            document.PropertyChanged += Watched;
            try
            {
                const string First = "FROMM scratch\n";
                editor.ApplyEdits([new TextEdit(0, editor.Document.Snapshot.Length, Shop.OpsDockerfile + First)]);
                // The window's thread held until the server has pushed for that text, so the window takes the push after the next edit.
                Assert.True(Waiting.Until(() => Volatile.Read(ref pushes) > 0, TimeSpan.FromSeconds(10)), "The Dockerfile server pushed nothing for the edit");
                editor.ApplyEdits([new TextEdit(Shop.OpsDockerfile.Length, First.Length, "RUNN echo\n")]);
                now = document.Text.Version;
                Ui.PumpUntil(() => taken, "the window's take of the push for the text before");
                release.Set();
                Ui.Settle();
                Assert.True(!shown.Any(m => m.Contains("FROMM", StringComparison.Ordinal)), $"The text before was marked as the text now: [{string.Join(", ", shown)}]");
                string[] errors = [.. document.Markers.Where(m => m.Kind == MarkerKind.Error).Select(m => m.Message)];
                Assert.True(errors.Any(e => e.Contains("Unknown instruction: RUNN", StringComparison.Ordinal)) && !errors.Any(e => e.Contains("FROMM", StringComparison.Ordinal)),
                    $"Once its checks landed, the Dockerfile's markers were not its last text's: [{string.Join(", ", errors)}]");
            }
            finally
            {
                release.Set();
                lsp.DiagnosticsPushed -= Counted;
                document.PropertyChanged -= Watched;
            }
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    private const string HubTags = "Complete image tags from Docker Hub";
    private const string TokenAsked = "GET /auth.docker.io/token?service=registry.docker.io&scope=repository:library/postgres:pull";
    private const string TagsAsked = "GET /registry-1.docker.io/v2/library/postgres/tags/list";

    [AvaloniaFact]
    public void A_compose_file_completes_a_services_keys_by_the_compose_schema_marks_a_key_it_does_not_have_and_knows_composes_tags()
    {
        Shop.SkipWithoutNode(yaml: true);
        MainWindow window = app.Open("ops/compose.yaml");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "    image: postgres:17");
            Ui.Press(window, "ENTER");
            TypeEach(window, "heal");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("healthcheck"), "the Compose schema's keys of a service as one is typed");
            Ui.Press(window, "ESCAPE");

            editor.ApplyEdits([new TextEdit(0, editor.Document.Snapshot.Length, Shop.ComposeFile.Replace("    ports: !reset []", "    bogus: 1\n    ports: !reset []", StringComparison.Ordinal))]);
            // The server's, which a running engine's Compose would say of the line too (its own marker then left off).
            Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error && m.Message.Contains("Property bogus is not allowed", StringComparison.Ordinal)),
                "the error under the key a service does not have");
            Marker error = document.Markers.First(m => m.Kind == MarkerKind.Error && m.Message.Contains("Property bogus is not allowed", StringComparison.Ordinal));
            Assert.Equal("bogus", editor.Document.Snapshot.GetText(error.Range.Start, error.Range.Length));
            Assert.DoesNotContain(document.Markers, m => m.Message.StartsWith("Compose:", StringComparison.Ordinal)
                && editor.Document.Snapshot.GetLineFromOffset(m.Range.Start) == editor.Document.Snapshot.GetLineFromOffset(error.Range.Start));
            // Compose's own tags are no errors: !reset's line is unmarked.
            int reset = editor.Document.Snapshot.ToString().IndexOf("!reset", StringComparison.Ordinal);
            Assert.DoesNotContain(document.Markers, m => m.Kind is MarkerKind.Error or MarkerKind.Warning
                && editor.Document.Snapshot.GetLineFromOffset(m.Range.Start) == editor.Document.Snapshot.GetLineFromOffset(reset));
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    /// <summary>Where a service names something the file declares, completion lists those names first, read from the file's
    /// own model: its other services under depends_on (a list or a mapping), links, volumes_from, network_mode's service:
    /// and extends (the services of the file an extends names), its volumes under a service's volumes (a short entry's source
    /// or a long one's), its networks, secrets and configs under theirs; never the service itself, nor a name its list holds
    /// already, nor names of another kind, and none where no name is typed (an image).</summary>
    [AvaloniaFact]
    public void A_compose_file_completes_the_names_it_declares_where_a_service_names_them_and_no_others()
    {
        Shop.SkipWithoutNode(yaml: true);
        string compose = app.PathOf("ops2/compose.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(compose)!);
        File.WriteAllText(compose, Names);
        MainWindow window = app.Open("ops2/compose.yaml");
        try
        {
            Offers(window, "      - api\n    links", "      - api\n      - ‸\n    links", ["cache"]);
            Offers(window, "      - api\n    links", "      - api\n      - ca‸\n    links", ["cache"]);
            Offers(window, "        condition: service_started\n", "        condition: service_started\n      w‸\n", ["web"]);
            Offers(window, "    links: [api]", "    links: [api, ‸]", ["cache"]);
            Offers(window, "    volumes_from:\n      - api", "    volumes_from:\n      - api\n      - ‸", ["cache"]);
            Offers(window, "    network_mode: service:api", "    network_mode: service:‸", ["api", "web"]);
            Offers(window, "      service: base", "      service: ‸", ["base"], "service in ../ops/common.yaml");
            Offers(window, "      - pgdata:/srv", "      - ‸:/srv", ["pgdata"]);
            Offers(window, "        source: uploads", "        source: ‸", ["uploads"]);
            Offers(window, "      front:\n    secrets", "      front:\n      ‸\n    secrets", ["back"]);
            Offers(window, "      - token\n", "      - token\n      - ‸\n", ["signing"]);
            Offers(window, "      - source: nginx", "      - source: ‸", ["nginx", "proxy"]);
            Offers(window, "    image: nginx:1.27", "    image: ‸", []);

            Offers(window, "    volumes_from:\n      - api", "    volumes_from:\n      - api\n      - ca‸", ["cache"], chosen: "    volumes_from:\n      - api\n      - cache\n");
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    /// <summary>Rename on a compose file's own name renames it where it is declared and wherever the file and its
    /// override use it, one command global undo takes back: a service from a depends_on of it (a list or a mapping,
    /// links, volumes_from, network_mode's service:, an extends); a volume from its declaration (a short entry, a long
    /// one's source); a service another file declares, from an extends naming it, in every file. A name its kind has,
    /// or one Compose would not take, is refused, saying why.</summary>
    [AvaloniaFact]
    public void Rename_in_a_compose_file_renames_a_service_or_volume_where_it_is_declared_and_used_with_its_override_as_one_undo()
    {
        Shop.SkipWithoutNode(yaml: true);
        string compose = app.PathOf("ops2/compose.yaml");
        string overrides = app.PathOf("ops2/compose.override.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(compose)!);
        File.WriteAllText(compose, Renamed);
        File.WriteAllText(overrides, Overridden);
        MainWindow window = app.Open("ops2/compose.yaml");
        window.Confirm = (_, _, _) => Task.FromResult(true);
        CodeEditor editor = window.EditorArea.CodeEditor;
        try
        {
            Rename(window, "depends_on:\n      - d", "db", "postgres", "Renamed the service db to postgres in 2 files");
            Assert.Equal(Renamed.Replace("db", "postgres", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Assert.Equal(Overridden.Replace("db", "postgres", StringComparison.Ordinal), File.ReadAllText(overrides));
            Undone(window, compose, overrides);

            Rename(window, "\nvolumes:\n  da", "data", "pgdata", "Renamed the volume data to pgdata in 2 files");
            Assert.Equal(Renamed.Replace("      - data:", "      - pgdata:", StringComparison.Ordinal).Replace("source: data", "source: pgdata", StringComparison.Ordinal)
                .Replace("\n  data:\n", "\n  pgdata:\n", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Assert.Equal(Overridden.Replace("      - data:", "      - pgdata:", StringComparison.Ordinal), File.ReadAllText(overrides));
            Undone(window, compose, overrides);

            Rename(window, "service: ba", "base", "redis", "Renamed the service base to redis in 3 files");
            Assert.Equal(Renamed.Replace("service: base", "service: redis", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Assert.Equal("services:\n  redis:\n    image: redis:7\n", File.ReadAllText(app.PathOf("ops/common.yaml")));
            Assert.Equal(Shop.ComposeFile.Replace("service: base", "service: redis", StringComparison.Ordinal), File.ReadAllText(app.PathOf("ops/compose.yaml")));
            Undone(window, compose, overrides);
            Assert.Equal("services:\n  base:\n    image: redis:7\n", File.ReadAllText(app.PathOf("ops/common.yaml")));
            Assert.Equal(Shop.ComposeFile, File.ReadAllText(app.PathOf("ops/compose.yaml")));

            Rename(window, "services:\n  w", "web", "worker", "compose.yaml has a service named worker already");
            Rename(window, "services:\n  w", "web", "my web", "A service's name has only letters, digits, dots, dashes and underscores");
            Assert.Equal(Renamed, editor.Document.Snapshot.ToString());
            Assert.Equal(Overridden, File.ReadAllText(overrides));
        }
        finally
        {
            window.Close();
            app.Reset();
        }

        static void Rename(MainWindow window, string after, string old, string name, string said)
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == old, $"Rename's box on {old}");
            window.KeyTextInput(name);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Status == said, $"the rename of {old} to {name} to say '{said}', not '{window.Status}'");
        }

        // Global undo, from outside the editor, takes the rename back in every file; the open one keeps its text unsaved.
        static void Undone(MainWindow window, string compose, string overrides)
        {
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString() == Renamed && File.ReadAllText(overrides) == Overridden,
                $"undo to take the rename back in {Path.GetFileName(compose)} and its override");
            window.OpenFile(compose);
            Ui.Settle();
        }
    }

    /// <summary>A compose file whose services name a service and a volume in each way they can be, with an override that
    /// names them too, for Rename.</summary>
    private const string Renamed = """
        services:
          web:
            image: nginx:1.27
            depends_on:
              - db
            links: [db]
            volumes_from:
              - db:ro
            volumes:
              - data:/srv
          worker:
            extends: db
            network_mode: service:db
            depends_on:
              db:
                condition: service_started
            volumes:
              - type: volume
                source: data
                target: /cache
          db:
            image: postgres:17
            volumes:
              - data:/var/lib/postgresql/data
          cache:
            extends:
              file: ../ops/common.yaml
              service: base
        volumes:
          data:
          logs:

        """;

    private const string Overridden = """
        services:
          db:
            environment:
              POSTGRES_PASSWORD: example
          web:
            depends_on:
              - db
            volumes:
              - data:/data

        """;

    /// <summary>A compose file whose services name each kind of thing it declares, for completion to be asked in.</summary>
    private const string Names = """
        services:
          web:
            image: nginx:1.27
            depends_on:
              - api
            links: [api]
            volumes_from:
              - api
            volumes:
              - pgdata:/srv
              - type: volume
                source: uploads
                target: /cache
            networks:
              front:
            secrets:
              - token
            configs:
              - source: nginx
          api:
            image: busybox:1.37
            extends:
              file: ../ops/common.yaml
              service: base
          cache:
            image: redis:7
            network_mode: service:api
            depends_on:
              api:
                condition: service_started
        volumes:
          pgdata:
          uploads:
        networks:
          front:
          back:
        secrets:
          token:
            file: ./token.txt
          signing:
            file: ./signing.txt
        configs:
          nginx:
            file: ./nginx.conf
          proxy:
            file: ./proxy.conf

        """;

    /// <summary>The file's own names completion lists with the caret at ‸ in <paramref name="typed"/>, typed in place of
    /// <paramref name="written"/>: <paramref name="expected"/>, in order, each said to be its kind (or <paramref name="said"/>);
    /// with <paramref name="chosen"/>, the first is chosen and the text then holds it.</summary>
    private static void Offers(MainWindow window, string written, string typed, string[] expected, string? said = null, string? chosen = null)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Contains(written, Names, StringComparison.Ordinal);
        string text = Names.Replace(written, typed.Replace("‸", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(0, editor.Document.Snapshot.Length, text)]);
        editor.Selections = SelectionSet.Single(Names.IndexOf(written, StringComparison.Ordinal) + typed.IndexOf('‸', StringComparison.Ordinal));
        Ui.Settle();
        Ui.Run(window, AppActions.Complete);
        string[] kinds = ["service", "volume", "network", "secret", "config"];
        string[] Own() => [.. window.Assist.CompletionDetails.Where(d => kinds.Any(k => d.Detail == k || d.Detail.StartsWith(k + " in ", StringComparison.Ordinal))).Select(d => d.Label)];
        if (expected.Length > 0)
        {
            Ui.WaitFor(() => Own().Length > 0, $"the file's names after '{typed}'");
            Assert.Equal(expected, Own());
            Assert.Equal(expected, window.Assist.CompletionLabels.Take(expected.Length));
            string kind = said ?? window.Assist.CompletionDetails[0].Detail;
            Assert.All(window.Assist.CompletionDetails.Take(expected.Length), d => Assert.Equal(kind, d.Detail));
        }
        else
        {
            Ui.Settle();
            Assert.True(Own().Length == 0, $"'{typed}' offered the file's names [{string.Join(", ", Own())}]");
        }
        if (chosen is null)
        {
            Ui.Press(window, "ESCAPE");
            return;
        }
        Ui.Press(window, "ENTER");
        Assert.Contains(chosen, editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Go_to_declaration_in_a_compose_file_leads_to_the_dockerfile_and_project_services_declarations_variables_env_files_and_registry_pages()
    {
        Shop.SkipWithoutNode(yaml: true);
        MainWindow window = app.Open("ops/compose.yaml");
        try
        {
            string compose = app.PathOf("ops/compose.yaml");
            Ui.WaitFor(() => window.Session.Workspace?.Projects.Any(p => p.Name == "Api") == true, "the solution's projects");

            Ui.Run(window, AppActions.ShowStructure);
            Ui.WaitFor(() => window.StructureView.Rows.Any(r => r.Name == "depends_on"), "the compose file's outline in the Structure window");
            Assert.Equal(["services", "api", "build"], window.StructureView.Rows.Take(3).Select(r => r.Name));

            GoFrom(window, compose, "dockerfile: ops/api.Dock");
            Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the build's Dockerfile and project offered");
            Assert.Equal(["api.Dockerfile", "Api.csproj"], window.Assist.Offered);
            Ui.Press(window, "ESCAPE");

            Arrives(window, compose, "depends_on:\n      d", compose, new TextPosition(19, 2));
            Arrives(window, compose, "links: [ca", compose, new TextPosition(25, 2));
            Arrives(window, compose, "volumes_from:\n      - cac", compose, new TextPosition(25, 2));
            Arrives(window, compose, "network_mode: service:d", compose, new TextPosition(19, 2));
            Arrives(window, compose, "service: ba", app.PathOf("ops/common.yaml"), new TextPosition(1, 2));

            Arrives(window, compose, "      - da", compose, new TextPosition(31, 2));
            Arrives(window, compose, "      - back", compose, new TextPosition(33, 2));
            Arrives(window, compose, "      - tok", compose, new TextPosition(35, 2));

            Arrives(window, compose, "$DB_PASS", app.PathOf("ops/.env"), new TextPosition(1, 0));
            Arrives(window, compose, "${REGIS", app.PathOf("ops/.env"), new TextPosition(0, 0));
            Arrives(window, compose, "      - api.e", app.PathOf("ops/api.env"), new TextPosition(0, 0));

            GoFrom(window, compose, "image: postgres:1");
            Ui.WaitFor(() => window.Browsed.Count > 0, "postgres's page opened");
            Assert.Equal("https://hub.docker.com/_/postgres", window.Browsed[^1].AbsoluteUri);
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    [AvaloniaFact]
    public void Add_docker_support_writes_the_projects_dockerfile_and_the_contexts_dockerignore_and_undo_takes_both_away()
    {
        MainWindow window = app.Open(null);
        try
        {
            AwaitEvaluated(window);
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Project, "Api"), "Add", "Docker Support…");
            DockerSupportDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<DockerSupportDialog>()));
            Assert.Equal(DockerSupportKind.Dockerfile, dialog.Chosen);
            dialog.Accept();
            Ui.Settle();

            string dockerfile = app.PathOf("Api/Dockerfile");
            string ignore = app.PathOf(".dockerignore");
            Assert.Equal(Shop.Dockerfile, File.ReadAllText(dockerfile));
            Assert.Equal(DockerFiles.Ignore, File.ReadAllText(ignore));
            Assert.Equal(dockerfile, window.EditorArea.Active?.Path);

            window.OpenFile(ignore);
            Ui.Settle();
            Assert.Equal(("Ignore", "#"), (window.EditorArea.Active!.Language.Name, window.EditorArea.Active.Language.LineComment));
            Assert.Equal("#", Language.ForPath(app.PathOf("ops/.env")).LineComment);

            List<string> asked = Answer(window);
            window.ExplorerView.Reveal(app.PathOf("Api/Api.csproj"));
            Ui.Settle();
            Ui.ClickRow(window, Row(window, NodeKind.Project, "Api"));
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(["Undo"], asked);
            Assert.False(File.Exists(dockerfile), $"Undo left the Dockerfile: {window.Status}");
            Assert.False(File.Exists(ignore), $"Undo left the .dockerignore: {window.Status}");
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    [AvaloniaFact]
    public void Add_docker_support_by_the_sdk_sets_a_workers_container_properties_and_leaves_the_rest_of_its_project_file_as_it_was()
    {
        MainWindow window = app.Open(null);
        try
        {
            AwaitEvaluated(window);
            string worker = app.PathOf("Worker/Worker.csproj");
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Project, "Worker"), "Add", "Docker Support…");
            DockerSupportDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<DockerSupportDialog>()));
            dialog.Chosen = DockerSupportKind.Sdk;
            dialog.Accept();
            Ui.Settle();

            // The property commented out stays as it was, setting nothing: the one MSBuild reads goes with the other.
            string set = Shop.Worker.Replace("    <Nullable>enable</Nullable>\r\n", "    <Nullable>enable</Nullable>\r\n"
                + "    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\r\n    <ContainerRepository>worker</ContainerRepository>\r\n", StringComparison.Ordinal);
            Assert.Equal(set, File.ReadAllText(worker));
            Assert.False(File.Exists(app.PathOf("Worker/Dockerfile")), "The SDK's container support wrote a Dockerfile too");

            Assert.Null(window.AddDockerSupport(worker, DockerSupportKind.Sdk));
            Assert.Equal(set, File.ReadAllText(worker));
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    [AvaloniaFact]
    public void Add_docker_compose_runs_an_app_using_npgsql_beside_postgres_it_waits_for_with_its_connection_string_and_lists_the_file_in_the_solution()
    {
        MainWindow window = app.Open(null);
        try
        {
            AwaitEvaluated(window);
            Ui.ChooseFromRowMenu(window, Row(window, NodeKind.Solution, "Shop"), "Add", "Docker Compose…");
            DockerComposeDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<DockerComposeDialog>()));
            Assert.Equal([("Api", "no Dockerfile: Docker Support adds one"), ("Worker", "no Dockerfile: Docker Support adds one")],
                dialog.Projects.Select(p => (p.Name, p.Detail)));
            ComposeChoice postgres = Assert.Single(dialog.Services);
            Assert.Equal(("postgres", "postgres:17, for Api"), (postgres.Name, postgres.Detail));
            dialog.Projects.Single(p => p.Name == "Worker").IsChosen = false;
            dialog.Close(true);
            Ui.Settle();

            string compose = app.PathOf("compose.yaml");
            Assert.Equal(Shop.Compose, File.ReadAllText(compose));
            Assert.Equal(Shop.Dockerfile, File.ReadAllText(app.PathOf("Api/Dockerfile")));
            Assert.Contains("<Folder Name=\"/Solution Items/\">\r\n    <File Path=\"compose.yaml\" />\r\n  </Folder>", File.ReadAllText(app.PathOf("Shop.slnx")), StringComparison.Ordinal);
            Assert.Equal(compose, window.EditorArea.Active?.Path);

            List<string> asked = Answer(window);
            Ui.ClickRow(window, Row(window, NodeKind.Solution, "Shop"));
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(["Undo"], asked);
            Assert.False(File.Exists(compose), $"Undo left compose.yaml: {window.Status}");
            Assert.False(File.Exists(app.PathOf("Api/Dockerfile")), "Undo left Api's Dockerfile");
            Assert.Equal(Shop.Solution, File.ReadAllText(app.PathOf("Shop.slnx")));
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    /// <summary>An Add that changes the project file or the solution open in an editor is one command with what it made:
    /// Undo puts the open file's text back (the SDK route's, its editor's own step; Add Docker Compose's, with the files it
    /// made taken away) and Redo makes them all again.</summary>
    [AvaloniaFact]
    public void Undo_takes_back_an_adds_change_to_an_open_project_or_solution_with_what_it_made_and_redo_makes_them_again()
    {
        string worker = app.PathOf("Worker/Worker.csproj");
        MainWindow window = app.Open("Worker/Worker.csproj");
        try
        {
            AwaitEvaluated(window);
            List<string> asked = Answer(window);
            OpenDocument project = window.EditorArea.Active!;
            Assert.Null(window.AddDockerSupport(worker, DockerSupportKind.Sdk));
            string set = File.ReadAllText(worker);
            Assert.Contains("<ContainerRepository>worker</ContainerRepository>", set, StringComparison.Ordinal);
            Assert.Equal(Lf(set), Lf(project.Text.Snapshot.ToString()));
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(Lf(Shop.Worker), Lf(project.Text.Snapshot.ToString()));
            Ui.Run(window, EditorActions.Redo);
            Assert.Equal(Lf(set), Lf(project.Text.Snapshot.ToString()));

            window.OpenFile(app.PathOf("Shop.slnx"));
            Ui.Settle();
            OpenDocument solution = window.EditorArea.Active!;
            Assert.Null(window.AddDockerCompose([app.PathOf("Api/Api.csproj")], ["postgres"]));
            string[] made = [app.PathOf("compose.yaml"), app.PathOf("Api/Dockerfile"), app.PathOf(".dockerignore")];
            string listed = File.ReadAllText(app.PathOf("Shop.slnx"));
            Assert.Contains("compose.yaml", solution.Text.Snapshot.ToString(), StringComparison.Ordinal);

            window.OpenFile(app.PathOf("Shop.slnx"));
            Ui.Settle();
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(["Undo"], asked);
            Assert.Equal(Lf(Shop.Solution), Lf(solution.Text.Snapshot.ToString()));
            Assert.True(made.All(m => !File.Exists(m)), $"Undo left [{string.Join(", ", made.Where(File.Exists).Select(Path.GetFileName))}]: {window.Status}");

            window.OpenFile(app.PathOf("Shop.slnx"));
            Ui.Settle();
            Ui.Run(window, EditorActions.Redo);
            Assert.True(made.All(File.Exists), $"Redo did not make [{string.Join(", ", made.Where(m => !File.Exists(m)).Select(Path.GetFileName))}]: {window.Status}");
            Assert.Equal(Lf(listed), Lf(solution.Text.Snapshot.ToString()));
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    /// <summary>A project at its build's context's root, beside its solution or opened on its own, is copied into the
    /// build's work folder (./), and its NuGet.config by the name the disk gives it, since the image's file system tells
    /// letter case apart.</summary>
    [AvaloniaFact]
    public void Add_docker_support_on_a_project_at_its_contexts_root_copies_it_and_its_nuget_config_as_named_into_the_work_folder()
    {
        foreach (bool solution in (bool[])[true, false])
        {
            string written = File.ReadAllText(DockerSupportAtRoot(solution));
            Assert.True(written.Contains("COPY [\"NuGet.config\", \"./\"]\nCOPY [\"Tiny.csproj\", \"./\"]\nRUN dotnet restore \"./Tiny.csproj\"\n", StringComparison.Ordinal),
                $"Opened as {(solution ? "Tiny.slnx" : "Tiny.csproj")}, the project at the context's root was not copied into the work folder as it is named:\n{written}");
        }
    }

    /// <summary>Where a Linux engine runs and holds the images it builds on (never pulled by a test: the SDK's is about
    /// 800 MB, which a fresh runner would fetch each run), the Dockerfile Add Docker Support writes for a project at its
    /// context's root builds.</summary>
    [AvaloniaFact]
    public void Add_docker_supports_dockerfile_for_a_project_at_its_contexts_root_builds()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string[] missing = [.. ((string[])["mcr.microsoft.com/dotnet/sdk:10.0", "mcr.microsoft.com/dotnet/runtime:10.0"])
            .Where(i => !Task.Run(() => Passenger.App.Containers.ContainerEngine.Shared.RunAsync(["image", "inspect", i], TimeSpan.FromSeconds(30).Scaled())).GetAwaiter().GetResult().Succeeded)];
        Assert.SkipWhen(missing.Length > 0, $"The engine lacks the images the Dockerfile builds on, which no test pulls: pull {string.Join(" and ", missing)} to run it");
        string dockerfile = DockerSupportAtRoot(solution: false);
        string folder = Path.GetDirectoryName(dockerfile)!;
        string image = $"ptest{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}tiny";
        Passenger.Core.ChildResult built = Task.Run(() => Passenger.App.Containers.ContainerEngine.Shared.RunAsync(
            ["build", "--pull=false", "--label", Docker.RunLabel, "-t", image, "-f", dockerfile, folder], TimeSpan.FromMinutes(10).Scaled())).GetAwaiter().GetResult();
        try
        {
            Assert.True(built.Succeeded, $"The Dockerfile written did not build: {Passenger.App.Containers.ContainerEngine.Said(built)}");
        }
        finally
        {
            Docker.Ask(["rmi", "-f", image]);
        }
    }

    /// <summary>Add Docker Support's Dockerfile for a console app at its folder's root, opened beside its solution or on
    /// its own: the Dockerfile's path.</summary>
    private static string DockerSupportAtRoot(bool solution)
    {
        string folder = Tiny(solution ? "docker-beside-" : "docker-alone-", solution);
        MainWindow window = OpenOwn(Path.Combine(folder, solution ? "Tiny.slnx" : "Tiny.csproj"));
        try
        {
            Ui.WaitFor(() => window.Session.Workspace?.Projects is [{ IsEvaluated: true }], "the project evaluated", TimeSpan.FromMinutes(2));
            Assert.Null(window.AddDockerSupport(Path.Combine(PathNames.Canonical(folder), "Tiny.csproj"), DockerSupportKind.Dockerfile));
            return Path.Combine(folder, "Dockerfile");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Add Docker Compose builds a project's own Dockerfile as that Dockerfile is written: from the project's
    /// folder when its COPY lines read the project's files there, from the solution's when they read them from it, on the
    /// ports it sets (ASPNETCORE_HTTP_PORTS, else ASP.NET Core's 8080), never its launch profiles'; Passenger's own new
    /// Dockerfile, from the solution's folder on the launch profiles' port.</summary>
    [AvaloniaFact]
    public void Add_docker_compose_builds_a_projects_own_dockerfile_from_the_folder_it_copies_from_on_the_ports_it_sets()
    {
        string folder = Fresh("docker-own-",
            ("Mall.slnx", "<Solution>\n  <Project Path=\"Api/Api.csproj\" />\n  <Project Path=\"Shop/Shop.csproj\" />\n  <Project Path=\"Web/Web.csproj\" />\n</Solution>\n"),
            ("nuget.config", Shop.NuGetConfig),
            ("Api/Api.csproj", WebProject), ("Api/Program.cs", WebProgram), ("Api/Properties/launchSettings.json", Profile(7001)),
            ("Api/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nWORKDIR /app\nENV ASPNETCORE_HTTP_PORTS=5000\nEXPOSE 5000\n\n"
                + "FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nWORKDIR /src\nCOPY [\"Api.csproj\", \"./\"]\nRUN dotnet restore \"Api.csproj\"\nCOPY . .\n"
                + "RUN dotnet publish \"Api.csproj\" -c Release -o /app/publish\n\nFROM base AS final\nCOPY --from=build /app/publish .\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n"),
            ("Web/Web.csproj", WebProject), ("Web/Program.cs", WebProgram), ("Web/Properties/launchSettings.json", Profile(7002)),
            ("Web/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nWORKDIR /app\n\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nWORKDIR /src\n"
                + "COPY [\"nuget.config\", \"./\"]\nCOPY [\"Web/Web.csproj\", \"Web/\"]\nRUN dotnet restore \"Web/Web.csproj\"\nCOPY . .\n"
                + "RUN dotnet publish \"Web/Web.csproj\" -c Release -o /app/publish\n\nFROM base AS final\nCOPY --from=build /app/publish .\nENTRYPOINT [\"dotnet\", \"Web.dll\"]\n"),
            ("Shop/Shop.csproj", WebProject), ("Shop/Program.cs", WebProgram), ("Shop/Properties/launchSettings.json", Profile(5080)));
        MainWindow window = OpenOwn(Path.Combine(folder, "Mall.slnx"));
        try
        {
            AwaitEvaluated(window, 3);
            string root = PathNames.Canonical(folder);
            Assert.Null(window.AddDockerCompose([.. ((string[])["Api", "Shop", "Web"]).Select(p => Path.Combine(root, p, $"{p}.csproj"))], []));
            string compose = File.ReadAllText(Path.Combine(folder, "compose.yaml"));
            Assert.Equal("services:\n"
                + "  api:\n    build:\n      context: Api\n      dockerfile: Dockerfile\n    image: api\n    ports:\n      - \"5000:5000\"\n"
                + "  shop:\n    build:\n      context: .\n      dockerfile: Shop/Dockerfile\n    image: shop\n    ports:\n      - \"5080:5080\"\n"
                + "  web:\n    build:\n      context: .\n      dockerfile: Web/Dockerfile\n    image: web\n    ports:\n      - \"8080:8080\"\n", compose);
            Assert.True(File.Exists(Path.Combine(folder, "Shop", "Dockerfile")), "Shop, with no Dockerfile, was given none");
            if (Docker.WhyNot is null)
            {
                Docker.Run("compose", "-f", Path.Combine(folder, "compose.yaml"), "config", "--quiet");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Each connection string an app has is pointed at the backing service its value is for, its database kept:
    /// Npgsql's Host= at postgres, made with that database; SqlClient's Server= at SQL Server, whose image makes no
    /// database, as a comment says; Redis' host:port at redis; SQLite's Data Source= file left as the app has it.</summary>
    [AvaloniaFact]
    public void Add_docker_compose_points_each_connection_string_at_the_service_its_value_is_for_and_keeps_its_database()
    {
        string folder = Fresh("docker-strings-",
            ("Orders.slnx", "<Solution>\n  <Project Path=\"Orders/Orders.csproj\" />\n</Solution>\n"),
            ("nuget.config", Shop.NuGetConfig),
            ("Orders/Orders.csproj", WebProject.Replace("</Project>", "  <ItemGroup>\n    <PackageReference Include=\"Npgsql\" Version=\"9.0.3\" />\n"
                + "    <PackageReference Include=\"Microsoft.Data.SqlClient\" Version=\"6.0.2\" />\n    <PackageReference Include=\"StackExchange.Redis\" Version=\"2.8.16\" />\n"
                + "    <PackageReference Include=\"Microsoft.Data.Sqlite\" Version=\"9.0.0\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal)),
            ("Orders/Program.cs", WebProgram),
            ("Orders/appsettings.json", "{\n  \"ConnectionStrings\": {\n    \"Main\": \"Host=localhost;Database=orders;Username=app;Password=secret\",\n"
                + "    \"Reports\": \"Server=localhost;Database=reports;User Id=sa;Password=secret;TrustServerCertificate=True\",\n"
                + "    \"Cache\": \"localhost:6379,abortConnect=false\",\n    \"Local\": \"Data Source=orders.db\"\n  }\n}\n"));
        MainWindow window = OpenOwn(Path.Combine(folder, "Orders.slnx"));
        try
        {
            AwaitEvaluated(window, 1);
            Assert.Null(window.AddDockerCompose([Path.Combine(PathNames.Canonical(folder), "Orders", "Orders.csproj")], ["postgres", "sqlserver", "redis"]));
            string compose = File.ReadAllText(Path.Combine(folder, "compose.yaml"));
            Assert.Contains("    environment:\n"
                + "      ConnectionStrings__Main: \"Host=postgres;Port=5432;Database=orders;Username=postgres;Password=${POSTGRES_PASSWORD:-postgres}\"\n"
                + "      ConnectionStrings__Reports: \"Server=sqlserver,1433;Database=reports;User Id=sa;Password=${MSSQL_SA_PASSWORD:-Passenger_dev1};TrustServerCertificate=True\"\n"
                + "      ConnectionStrings__Cache: \"redis:6379\"\n    depends_on:\n", compose, StringComparison.Ordinal);
            Assert.DoesNotContain("ConnectionStrings__Local", compose, StringComparison.Ordinal);
            Assert.Contains("  postgres:\n    image: postgres:17\n    environment:\n      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres}\n      POSTGRES_DB: orders\n",
                compose, StringComparison.Ordinal);
            Assert.Contains("  sqlserver:\n    # SQL Server's image makes no database: reports is the app's to make (EF Core's Migrate or EnsureCreated) or yours, with sqlcmd\n",
                compose, StringComparison.Ordinal);
            if (Docker.WhyNot is null)
            {
                Docker.Run("compose", "-f", Path.Combine(folder, "compose.yaml"), "config", "--quiet");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A project gets ASP.NET Core's image when the framework reaches it through a project it references (as
    /// evaluated) or a package (as its restore says), not only through its own SDK.</summary>
    [AvaloniaFact]
    public void Add_docker_support_gives_a_project_that_asp_net_core_reaches_through_a_reference_or_a_package_the_aspnet_image()
    {
        const string Console = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n";
        string folder = Fresh("docker-aspnet-",
            ("Kit.slnx", "<Solution>\n  <Project Path=\"ByPackage/ByPackage.csproj\" />\n  <Project Path=\"ByReference/ByReference.csproj\" />\n  <Project Path=\"Kit/Kit.csproj\" />\n</Solution>\n"),
            ("nuget.config", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"kit\" value=\"./feed\" />\n  </packageSources>\n</configuration>\n"),
            ("Kit/Kit.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <PackageId>Shop.Kit</PackageId>\n"
                + "    <Version>1.0.0</Version>\n  </PropertyGroup>\n  <ItemGroup>\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n</Project>\n"),
            ("Kit/Kit.cs", "namespace Kit;\n\npublic static class Pages\n{\n}\n"),
            ("ByReference/ByReference.csproj", Console + "  <ItemGroup>\n    <ProjectReference Include=\"..\\Kit\\Kit.csproj\" />\n  </ItemGroup>\n</Project>\n"),
            ("ByReference/Program.cs", "System.Console.WriteLine(\"By reference\");\n"),
            ("ByPackage/ByPackage.csproj", Console + "  <ItemGroup>\n    <PackageReference Include=\"Shop.Kit\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n"),
            ("ByPackage/Program.cs", "System.Console.WriteLine(\"By package\");\n"));
        Passenger.Core.ChildResult packed = Task.Run(() => Passenger.Core.ChildProcesses.RunAsync(
            Passenger.Core.ChildProcesses.DotnetCommand(["pack", Path.Combine(folder, "Kit", "Kit.csproj"), "-o", Path.Combine(folder, "feed")]), limit: Restore.Limit.Scaled())).GetAwaiter().GetResult();
        Assert.True(packed.Succeeded, $"Kit did not pack: {packed.Output}{packed.Error}");
        Restore.Run(Path.Combine(folder, "ByPackage", "ByPackage.csproj"));
        MainWindow window = OpenOwn(Path.Combine(folder, "Kit.slnx"));
        try
        {
            AwaitEvaluated(window, 3);
            string root = PathNames.Canonical(folder);
            foreach (string project in (string[])["ByReference", "ByPackage"])
            {
                Assert.Null(window.AddDockerSupport(Path.Combine(root, project, $"{project}.csproj"), DockerSupportKind.Dockerfile));
                string written = File.ReadAllText(Path.Combine(folder, project, "Dockerfile"));
                Assert.True(written.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\n", StringComparison.Ordinal),
                    $"{project}, which ASP.NET Core reaches, was not given the aspnet image:\n{written}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Go to Declaration on a build takes the project the final stage's ENTRYPOINT runs (its own, or the stage's it
    /// is FROM), never an earlier stage's nor a comment's; a variable is Compose's when the .env beside the file sets it, so
    /// one only a service's env_file sets is said not set for Compose, and $$VAR, a dollar sign written whole, is none.</summary>
    [AvaloniaFact]
    public void Go_to_declaration_in_a_compose_file_takes_the_final_stages_entrypoint_and_its_variables_as_compose_reads_them()
    {
        Shop.SkipWithoutNode(yaml: true);
        string compose = app.PathOf("ops2/compose.yaml");
        const string Text = "services:\n  earlier:\n    build:\n      context: ..\n      dockerfile: ops2/earlier.Dockerfile\n  commented:\n    build:\n      context: ..\n"
            + "      dockerfile: ops2/commented.Dockerfile\n    env_file:\n      - web.env\n    environment:\n      MODE: ${ASPNETCORE_ENVIRONMENT}\n"
            + "      LITERAL: $$DB_PASSWORD\n";
        Directory.CreateDirectory(Path.GetDirectoryName(compose)!);
        File.WriteAllText(compose, Text);
        File.WriteAllText(app.PathOf("ops2/.env"), "DB_PASSWORD=secret\n");
        File.WriteAllText(app.PathOf("ops2/web.env"), "ASPNETCORE_ENVIRONMENT=Development\n");
        File.WriteAllText(app.PathOf("ops2/earlier.Dockerfile"), "FROM mcr.microsoft.com/dotnet/sdk:10.0 AS tools\nENTRYPOINT [\"dotnet\", \"Lib.dll\"]\n\n"
            + "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final\nWORKDIR /app\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n");
        File.WriteAllText(app.PathOf("ops2/commented.Dockerfile"), "# ENTRYPOINT [\"dotnet\", \"Worker.dll\"]\nFROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\n"
            + "ENTRYPOINT [\"dotnet\", \"Api.dll\"]\n\nFROM base AS final\nWORKDIR /app\n");
        MainWindow window = app.Open("ops2/compose.yaml");
        try
        {
            Ui.WaitFor(() => window.Session.Workspace?.Projects is { Count: 3 } projects && projects.All(p => p.IsEvaluated), "the solution's projects evaluated", TimeSpan.FromMinutes(2));
            foreach (string build in (string[])["earlier", "commented"])
            {
                GoFrom(window, compose, $"dockerfile: ops2/{build}.Dock");
                Ui.WaitFor(() => window.Assist.Offered.Count == 2, $"the {build} build's Dockerfile and project offered ({window.Status})");
                Assert.Equal([$"{build}.Dockerfile", "Api.csproj"], window.Assist.Offered);
                Ui.Press(window, "ESCAPE");
            }

            GoFrom(window, compose, "${ASPNETCORE_ENV");
            Ui.WaitFor(() => window.Status?.Contains("ASPNETCORE_ENVIRONMENT is not set for Compose: web.env sets it in the commented service's container", StringComparison.Ordinal) == true,
                $"the variable only an env_file sets said not set for Compose, not '{window.Status}'");
            Assert.Equal(compose, window.EditorArea.Active?.Path);

            GoFrom(window, compose, "$$DB_PASS");
            Ui.Settle();
            Assert.True(window.EditorArea.Active?.Path == compose, $"Go to Declaration on $$DB_PASSWORD, which Compose leaves as written, went to {window.EditorArea.Active?.Path}");
        }
        finally
        {
            window.Close();
            app.Reset();
        }
    }

    private static readonly string WebProject = TestProjects.Web();

    private const string WebProgram = "var app = WebApplication.Create(args);\napp.Run();\n";

    /// <summary>Launch profiles listening on one HTTP port.</summary>
    private static string Profile(int port) =>
        $"{{\n  \"profiles\": {{\n    \"http\": {{\n      \"commandName\": \"Project\",\n      \"applicationUrl\": \"http://localhost:{port}\"\n    }}\n  }}\n}}\n";

    /// <summary>A folder of the test's own holding the files given.</summary>
    private static string Fresh(string name, params (string Path, string Text)[] files)
    {
        string folder = TestFolders.Create(name);
        foreach ((string path, string text) in files)
        {
            string full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return folder;
    }

    /// <summary>A console app at its folder's root with a NuGet.config of that spelling (no feed: it restores nothing),
    /// beside its solution or not.</summary>
    private static string Tiny(string name, bool solution)
    {
        List<(string, string)> files =
        [
            ("Tiny.csproj", TestProjects.Console()),
            ("Program.cs", "System.Console.WriteLine(\"Tiny\");\n"),
            ("NuGet.config", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n  </packageSources>\n</configuration>\n"),
        ];
        if (solution)
        {
            files.Add(("Tiny.slnx", "<Solution>\n  <Project Path=\"Tiny.csproj\" />\n</Solution>\n"));
        }
        return Fresh(name, [.. files]);
    }

    /// <summary>A window on a folder of the test's own, opened as the solution or project named.</summary>
    private static MainWindow OpenOwn(string opened)
    {
        MainWindow window = Ui.OpenFolder(opened, languageServer: false, autosaves: false);
        return window;
    }

    /// <summary>A text with its line breaks as '\n', as an editor and a file may hold them otherwise.</summary>
    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Go to Declaration from just after a stretch of the file, which is opened (again) first.</summary>
    private static void GoFrom(MainWindow window, string file, string after)
    {
        window.OpenFile(file);
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
    }

    private static void Arrives(MainWindow window, string file, string after, string at, TextPosition position)
    {
        GoFrom(window, file, after);
        Ui.WaitFor(() => window.EditorArea.Active?.Path == at && window.EditorArea.CodeEditor.CaretPosition == position,
            $"Go to Declaration after '{after}' to go to {Path.GetFileName(at)} {position.Line + 1}:{position.Column + 1}; it is at "
            + $"{Path.GetFileName(window.EditorArea.Active?.Path)} {window.EditorArea.CodeEditor.CaretPosition.Line + 1}:{window.EditorArea.CodeEditor.CaretPosition.Column + 1} ({window.Status})");
    }

    private static void AwaitEvaluated(MainWindow window, int count = 3) =>
        Ui.WaitFor(() => window.Session.Workspace?.Projects is { } projects && projects.Count == count && projects.All(p => p.IsEvaluated), "the solution's projects evaluated", TimeSpan.FromMinutes(2));

    private static void TypeEach(MainWindow window, string text)
    {
        foreach (char c in text)
        {
            window.KeyTextInput(c.ToString());
        }
    }

    private static T Editor<T>(SettingsDialog dialog, string label) where T : Control =>
        ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label).Parent!).GetVisualDescendants().OfType<T>().First();

    private static List<string> Answer(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        return asked;
    }

    private static ExplorerNode Row(MainWindow window, NodeKind kind, string name) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Name == name)
        ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

    /// <summary>Docker Hub on a loopback port of its own, each request under its host's name as the Dockerfile server's
    /// preload sends it there: a token from auth.docker.io, and postgres's tags from registry-1.docker.io for that token;
    /// every request kept by its method and path.</summary>
    private sealed class StandInHub : IDisposable
    {
        private const string Token = "stand-in";

        private readonly List<string> asked = [];
        private readonly StandInHttp http;

        public StandInHub()
        {
            http = new StandInHttp(AnswerAsync);
            Place = new Uri($"{http.Root}/");
        }

        public Uri Place { get; }

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (asked)
                {
                    return [.. asked];
                }
            }
        }

        public void Dispose() => http.Dispose();

        private Task AnswerAsync(StandInHttp.Exchange exchange)
        {
            lock (asked)
            {
                asked.Add(exchange.Line);
            }
            string path = exchange.Path;
            bool bearer = string.Equals(exchange.Header("Authorization"), $"Bearer {Token}", StringComparison.OrdinalIgnoreCase);
            (int status, string body) = path switch
            {
                _ when path.StartsWith("/auth.docker.io/token?", StringComparison.Ordinal) => (200, $"{{\"token\":\"{Token}\"}}"),
                "/registry-1.docker.io/v2/library/postgres/tags/list" when bearer => (200, "{\"name\":\"library/postgres\",\"tags\":[\"16\",\"17\",\"17-alpine\",\"latest\"]}"),
                _ => (404, "{}"),
            };
            return exchange.ReplyAsync(status, "application/json", body);
        }
    }

    /// <summary>A solution of an ASP.NET Core app (Api, using Npgsql, with launch profiles and a connection string), a worker
    /// and a library the app references, with a compose file of its own under ops/ (its .env, env_file, a file it extends,
    /// and a Dockerfile); never restored. The tests share it, each putting back what it changed.</summary>
    public sealed class Shop : IDisposable
    {
        public const string Solution = "<Solution>\r\n  <Project Path=\"Api/Api.csproj\" />\r\n  <Project Path=\"Lib/Lib.csproj\" />\r\n  <Project Path=\"Worker/Worker.csproj\" />\r\n</Solution>\r\n";

        public const string Api = $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\r\n\r\n  <PropertyGroup>\r\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n"
            + "  <ItemGroup>\r\n    <PackageReference Include=\"Npgsql\" Version=\"9.0.3\" />\r\n    <ProjectReference Include=\"..\\Lib\\Lib.csproj\" />\r\n  </ItemGroup>\r\n\r\n</Project>\r\n";

        /// <summary>A worker whose image's name was once set, now commented out.</summary>
        public const string Worker = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <OutputType>Exe</OutputType>\r\n"
            + $"    <!-- <ContainerRepository>shop-worker</ContainerRepository> -->\r\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\r\n"
            + "    <Nullable>enable</Nullable>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

        public const string Lib = $"<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

        public const string LaunchSettings = "{\n  \"profiles\": {\n    \"http\": {\n      \"commandName\": \"Project\",\n      \"applicationUrl\": \"http://localhost:5080\"\n    },\n"
            + "    \"https\": {\n      \"commandName\": \"Project\",\n      \"applicationUrl\": \"https://localhost:7080;http://localhost:5080\"\n    }\n  }\n}\n";

        public const string AppSettings = "{\n  \"ConnectionStrings\": {\n    \"Shop\": \"Host=localhost;Database=shop;Username=postgres;Password=postgres\"\n  }\n}\n";

        /// <summary>No feed that is there: a restore of the folder fails at once, offline.</summary>
        public const string NuGetConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"none\" value=\"./no-packages\" />\n  </packageSources>\n</configuration>\n";

        public const string ComposeFile = "services:\n  api:\n    build:\n      context: ..\n      dockerfile: ops/api.Dockerfile\n    image: ${REGISTRY}/shop/api:latest\n"
            + "    depends_on:\n      db:\n        condition: service_healthy\n    env_file:\n      - api.env\n    environment:\n"
            + "      ConnectionStrings__Shop: \"Host=db;Password=$DB_PASSWORD\"\n    networks:\n      - backend\n    volumes:\n      - data:/var/data\n"
            + "    secrets:\n      - token\n  db:\n    image: postgres:17\n    ports: !reset []\n    links: [cache]\n    volumes_from:\n      - cache\n"
            + "  cache:\n    extends:\n      file: common.yaml\n      service: base\n    network_mode: service:db\nvolumes:\n  data: {}\nnetworks:\n  backend: {}\n"
            + "secrets:\n  token:\n    file: ./token.txt\n";

        public const string OpsDockerfile = "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nWORKDIR /app\nEXPOSE 8080\n\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\n"
            + "WORKDIR /src\nCOPY . .\nRUN dotnet publish \"Api/Api.csproj\" -c Release -o /app/publish\n\nFROM base AS final\nWORKDIR /app\n"
            + "COPY --from=build /app/publish .\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n";

        /// <summary>What Add > Docker Support writes for Api.</summary>
        public const string Dockerfile = "# Built from the solution's folder: docker build -f Api/Dockerfile .\nFROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nUSER $APP_UID\n"
            + "WORKDIR /app\nENV ASPNETCORE_HTTP_PORTS=5080\nEXPOSE 5080\n\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nARG BUILD_CONFIGURATION=Release\nWORKDIR /src\n"
            + "COPY [\"nuget.config\", \"./\"]\nCOPY [\"Api/Api.csproj\", \"Api/\"]\nCOPY [\"Lib/Lib.csproj\", \"Lib/\"]\nRUN dotnet restore \"./Api/Api.csproj\"\nCOPY . .\n"
            + "WORKDIR \"/src/Api\"\nRUN dotnet build \"./Api.csproj\" -c $BUILD_CONFIGURATION -o /app/build\n\nFROM build AS publish\nARG BUILD_CONFIGURATION=Release\n"
            + "RUN dotnet publish \"./Api.csproj\" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false\n\nFROM base AS final\nWORKDIR /app\n"
            + "COPY --from=publish /app/publish .\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n";

        /// <summary>What Add > Docker Compose writes for Api alone.</summary>
        public const string Compose = "services:\n  api:\n    build:\n      context: .\n      dockerfile: Api/Dockerfile\n    image: api\n    ports:\n      - \"5080:5080\"\n"
            + "    environment:\n      ConnectionStrings__Shop: \"Host=postgres;Port=5432;Database=shop;Username=postgres;Password=${POSTGRES_PASSWORD:-postgres}\"\n"
            + "    depends_on:\n      postgres:\n        condition: service_healthy\n  postgres:\n    image: postgres:17\n    environment:\n"
            + "      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres}\n      POSTGRES_DB: shop\n    volumes:\n      - postgres-data:/var/lib/postgresql/data\n    healthcheck:\n"
            + "      test: [\"CMD-SHELL\", \"pg_isready -U postgres\"]\n      interval: 5s\n      timeout: 5s\n      retries: 10\n\nvolumes:\n  postgres-data:\n";

        private static readonly (string Path, string Text)[] Files =
        [
            ("Shop.slnx", Solution),
            ("nuget.config", NuGetConfig),
            ("Api/Api.csproj", Api),
            ("Api/Program.cs", "var app = WebApplication.Create(args);\napp.MapGet(\"/\", () => \"Shop\");\napp.Run();\n"),
            ("Api/Properties/launchSettings.json", LaunchSettings),
            ("Api/appsettings.json", AppSettings),
            ("Lib/Lib.csproj", Lib),
            ("Lib/Prices.cs", "namespace Lib;\n\npublic static class Prices\n{\n}\n"),
            ("Worker/Worker.csproj", Worker),
            ("Worker/Program.cs", "System.Console.WriteLine(\"Working\");\n"),
            ("ops/compose.yaml", ComposeFile),
            ("ops/.env", "REGISTRY=ghcr.io/shop\nDB_PASSWORD=secret\n"),
            ("ops/api.env", "ASPNETCORE_ENVIRONMENT=Development\n"),
            ("ops/common.yaml", "services:\n  base:\n    image: redis:7\n"),
            ("ops/token.txt", "dev-token\n"),
            ("ops/api.Dockerfile", OpsDockerfile),
        ];

        /// <summary>What the tests' Adds write, and the files a test writes beside the fixture's, taken away again.</summary>
        private static readonly string[] Made = ["Api/Dockerfile", "Worker/Dockerfile", "Lib/Dockerfile", ".dockerignore", "compose.yaml",
            "ops2/compose.yaml", "ops2/compose.override.yaml", "ops2/.env", "ops2/web.env", "ops2/earlier.Dockerfile", "ops2/commented.Dockerfile"];

        public Shop()
        {
            Folder = TestFolders.Create("Shop");
            Reset();
        }

        public string Folder { get; }

        /// <summary>A file's path as the IDE names it: canonical, its '/' Windows' '\' there, so it compares with an open file's.</summary>
        public string PathOf(string relative) => Path.GetFullPath(Path.Combine(PathNames.Canonical(Folder), relative));

        public void Reset()
        {
            foreach ((string path, string text) in Files)
            {
                string full = Path.Combine(Folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            foreach (string made in Made)
            {
                if (File.Exists(Path.Combine(Folder, made)))
                {
                    File.Delete(Path.Combine(Folder, made));
                }
            }
        }

        /// <summary>A window on the solution, with a file open when one is named, its Dockerfile server asking
        /// <paramref name="dockerHub"/> for image tags when one is given; with no C# language server, which nothing the class
        /// checks asks of (Docker's files are their servers', the Adds' projects the MSBuild host's evaluation), and which
        /// would start cold on the solution in every window, never restored.</summary>
        public MainWindow Open(string? file, Uri? dockerHub = null)
        {
            MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, DockerHub = dockerHub, StartsLanguageServer = false };
            window.Show();
            window.OpenFolder(Folder);
            if (file is not null)
            {
                window.OpenFile(PathOf(file));
            }
            Ui.Settle();
            return window;
        }

        /// <summary>Skips a test that needs the Dockerfile or YAML server where there is no Node.js to run it, or (for the
        /// YAML server, <paramref name="yaml"/>) one older than it needs.</summary>
        public static void SkipWithoutNode(bool yaml = false)
        {
            WebLanguagesTests.SkipWithoutNode();
            if (yaml)
            {
                Passenger.App.Web.NodeRuntime node = Task.Run(() => Passenger.App.Web.NodeRuntime.FindAsync(null)).GetAwaiter().GetResult();
                string? old = node.Problem ?? node.TooOldFor(Path.Combine(Passenger.App.Web.WebLanguages.Modules, "yaml-language-server", "package.json"), "the YAML language server");
                Assert.SkipWhen(old is not null, $"The YAML language server cannot run here: {old}");
            }
        }

        public void Dispose() => TestFolders.Delete(Folder);
    }
}
