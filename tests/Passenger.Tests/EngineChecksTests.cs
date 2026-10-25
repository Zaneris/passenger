using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>What the person's container engine says of a Dockerfile and a compose file, as a person sees it in the window:
/// BuildKit's build checks marked beside the Dockerfile server's and listed by file in Problems' Containers tab, gone once
/// a fix is saved; a compose file's <c>${VAR}</c>s showing what Compose makes of them with the .env beside it, the base
/// file's values its override changes showing the value Compose ends with, and Compose's error marked where it names it
/// and listed in Problems, a name the file does not declare fixed by Passenger's own fixes, a connection string a .NET
/// service's environment never sets marked on the service and where its code reads it, and set by their fixes; and with no
/// engine running, nothing of it per file. The engine's tests skip, saying why, where
/// no Linux engine runs (no Node.js is needed: the markers stand without the files' servers).</summary>
public sealed class EngineChecksTests
{
    private const string Dockerfile = "FROM busybox:1.37 as build\nRUN echo built > /built\n\nFROM busybox:1.37\nCOPY --from=build /built /built\nCMD [\"cat\", \"/built\"]\n";

    /// <summary>A frontend image no registry has: were it pulled, the check would fail, saying only that its host is unknown.</summary>
    private const string Frontend = "passenger.invalid/unknown-frontend:1";

    [AvaloniaFact]
    public void A_dockerfile_shows_buildkits_checks_beside_its_servers_and_in_problems_never_running_the_frontend_it_names_and_a_fix_saved_clears_them()
    {
        SkipWithoutBuildx();
        // Its syntax directive names a frontend image, which BuildKit would pull and run to check it: the engine's own checks it.
        string folder = Folder(("Dockerfile", $"# syntax={Frontend}\n# check=error=false\n{Dockerfile}"));
        MainWindow window = Open(folder, "Dockerfile");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            TextSnapshot text = document.Text.Snapshot;
            // Off, as every other test has it: no registry is asked, nothing is marked.
            Assert.False(window.Settings.BuildKitChecks, "Check Dockerfiles with BuildKit is on in the tests' run, which reaches registries");
            Assert.DoesNotContain(document.Markers, m => m.Message.StartsWith("BuildKit:", StringComparison.Ordinal));

            SettingsDialog dialog = DialogButtonsTests.Check(window.ShowSettings());
            dialog.Query = "buildkit";
            Ui.Settle();
            Assert.Equal([SettingsDialog.DockerPage], dialog.Pages);
            Assert.Equal([BuildKitSetting], dialog.Shown);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible
                && t.Text?.Contains("reads each base image's metadata from its registry", StringComparison.Ordinal) == true
                && t.Text.Contains("never one a # syntax= line names, which is neither pulled nor run", StringComparison.Ordinal));
            CheckBox on = ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == BuildKitSetting).Parent!).GetVisualDescendants().OfType<CheckBox>().First();
            Ui.Click(on);
            dialog.Ok();
            Ui.Settle();
            Assert.True(window.Settings.BuildKitChecks, "Ticking Check Dockerfiles with BuildKit left it off");

            // Podman has no BuildKit: the status bar and Problems say the Dockerfile goes unchecked, and nothing is marked.
            EngineState engine = Task.Run(() => ContainerEngine.Shared.StateAsync()).GetAwaiter().GetResult();
            if (engine.IsPodman)
            {
                Ui.WaitFor(() => window.Status == engine.Limit, $"the status bar to say Podman has no BuildKit, not \"{window.Status}\"");
                Assert.Contains(window.BottomPanel.Problems.ContainerProblems, p => p.Message == engine.Limit);
                Assert.DoesNotContain(document.Markers, m => m.Message.StartsWith("BuildKit:", StringComparison.Ordinal));
                return;
            }

            Ui.WaitFor(() => document.Markers.Any(m => m.Message.EndsWith("(FromAsCasing)", StringComparison.Ordinal)), $"BuildKit's FromAsCasing on the first line ({window.Status})");
            Marker casing = document.Markers.Single(m => m.Message.EndsWith("(FromAsCasing)", StringComparison.Ordinal));
            Assert.Equal(MarkerKind.Warning, casing.Kind);
            Assert.Equal("FROM busybox:1.37 as build", text.GetText(casing.Range.Start, casing.Range.Length));
            Assert.Equal("BuildKit: 'as' and 'FROM' keywords' casing do not match (FromAsCasing)", casing.Message);
            Assert.Equal(new Uri("https://docs.docker.com/go/dockerfile/rule/from-as-casing/"), casing.Link);
            Assert.Equal("FromAsCasing", casing.LinkText);

            ProblemsModel problems = window.BottomPanel.Problems;
            Problem listed = Assert.Single(problems.ContainerProblems);
            Assert.Equal((PathOf(folder, "Dockerfile"), 2, casing.Message), (listed.Path, listed.Position.Line, listed.Message));
            problems.Shown = ProblemsTab.Containers;
            Assert.Equal([(0, "Dockerfile", "1 problem"), (1, casing.Message, "Dockerfile:3:1")], problems.Rows.Select(r => (r.Depth, r.Label, r.Detail)));
            Assert.Empty(Docker.Ask(["image", "ls", "-q", Frontend]).Trim());

            int casingAt = text.ToString().IndexOf(" as ", StringComparison.Ordinal);
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(casingAt, 4, " AS ")]);
            Ui.Run(window, AppActions.Save);
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("BuildKit:", StringComparison.Ordinal)) && problems.ContainerProblems.Count == 0,
                $"BuildKit's warning gone once the fix was saved: [{string.Join(", ", problems.ContainerProblems.Select(p => p.Message))}]");
            Assert.Empty(problems.Rows);
        }
        finally
        {
            window.Close();
        }
    }

    private const string BuildKitSetting = "Check Dockerfiles with BuildKit";

    [AvaloniaFact]
    public void A_compose_file_shows_what_its_variables_come_to_and_the_values_its_override_changes_as_hints()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        const string Compose = "services:\n  web:\n    image: \"busybox:${TAG:-1.36}\"\n    ports:\n      - \"${PORT:-8080}:80\"  # the host's port\n"
            + "    environment:\n      GREETING: hello $NAME\n      ZOO: $MONKEY\n    volumes:\n      - $PWD/data:/data\n  db:\n    image: postgres:16\n    environment:\n      POSTGRES_USER: shop\n"
            + "      POSTGRES_PASSWORD: ${DB_PASSWORD:-hunter2}\n";
        string folder = Folder(("compose.yaml", Compose), (".env", "PORT=9090\nNAME=world\nMONKEY=banana\nDB_PASSWORD=s3cret-value\n"),
            ("compose.override.yaml", "services:\n  db:\n    image: postgres:17\n    environment:\n      POSTGRES_USER: shop\n      POSTGRES_PASSWORD: other-s3cret\n"));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            // MONKEY has no word KEY; PWD is the compose file's folder, which Compose runs in.
            string folderHint = FolderHint(folder);
            string[] expected = ["${TAG:-1.36}|= 1.36", "${PORT:-8080}|= 9090", "$NAME|= world", "$MONKEY|= banana", folderHint, "postgres:16|compose.override.yaml: postgres:17",
                "${DB_PASSWORD:-hunter2}|= ••••••", "${DB_PASSWORD:-hunter2}|compose.override.yaml: ••••••"];
            Ui.WaitFor(() => Hints(document, expected).SequenceEqual(expected), $"Compose's values after the text, not [{string.Join(", ", Hints(document, expected))}]");
            Assert.DoesNotContain(document.InlayHints, h => h.Label.Contains("s3cret", StringComparison.Ordinal));

            // Typed, the file is read again once typing pauses: a default of a variable the .env leaves unset is its value.
            string text = document.Text.Snapshot.ToString();
            int port = text.IndexOf("${PORT:-8080}", StringComparison.Ordinal);
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(port, "${PORT:-8080}".Length, "${HOST_PORT:-8081}")]);
            string[] typed = ["${TAG:-1.36}|= 1.36", "${HOST_PORT:-8081}|= 8081", "$NAME|= world", "$MONKEY|= banana", folderHint, "postgres:16|compose.override.yaml: postgres:17",
                "${DB_PASSWORD:-hunter2}|= ••••••", "${DB_PASSWORD:-hunter2}|compose.override.yaml: ••••••"];
            Ui.WaitFor(() => Hints(document, typed).SequenceEqual(typed), $"the new variable's default, not [{string.Join(", ", Hints(document, typed))}]");
            Assert.Empty(window.BottomPanel.Problems.ContainerProblems);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A secret is known by its variable's name (a word PASS, PW, PWD or KEY, PASSWORD or SECRET anywhere in it, a
    /// name ending in KEY but an ordinary word's), by the key or list item's KEY= it is given to, or by its place as a URL's
    /// password; a value holding such a URL shows it with the password masked.</summary>
    [AvaloniaFact]
    public void A_compose_files_hints_mask_a_secret_by_its_variables_name_the_key_it_is_given_to_and_a_urls_password()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        const string Compose = "services:\n  db:\n    image: postgres:17\n    environment:\n      POSTGRES_PASSWORD: ${DB_PASS}\n      OPENAI: ${APIKEY}\n"
            + "      ADMIN: ${DBPASSWORD}\n      DATABASE_URL: postgres://u:${PGPASS}@db/x\n      CACHE_PASSWORD: ${CACHE_VALUE}\n      BROKER: ${BROKER_URL}\n"
            + "      ZOO: $MONKEY\n  api:\n    image: busybox:1.37\n    environment:\n      - SIGNING_KEY=${SIGNER}\n    volumes:\n      - $PWD/data:/data\n";
        string[] secrets = ["pass-one", "key-two", "pass-three", "pass-four", "value-five", "hunter-six", "signer-seven"];
        string folder = Folder(("compose.yaml", Compose), (".env", $"DB_PASS={secrets[0]}\nAPIKEY={secrets[1]}\nDBPASSWORD={secrets[2]}\nPGPASS={secrets[3]}\n"
            + $"CACHE_VALUE={secrets[4]}\nBROKER_URL=amqp://app:{secrets[5]}@rabbit:5672\nSIGNER={secrets[6]}\nMONKEY=banana\n"));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            string[] expected = ["${DB_PASS}|= ••••••", "${APIKEY}|= ••••••", "${DBPASSWORD}|= ••••••", "${PGPASS}|= ••••••", "${CACHE_VALUE}|= ••••••",
                "${BROKER_URL}|= amqp://app:••••••@rabbit:5672", "$MONKEY|= banana", "${SIGNER}|= ••••••", FolderHint(folder)];
            Ui.WaitFor(() => Hints(document, expected).SequenceEqual(expected), $"each secret masked, not [{string.Join(", ", Hints(document, expected))}]");
            string leaked = string.Join(", ", document.InlayHints.Where(h => secrets.Any(s => h.Label.Contains(s, StringComparison.Ordinal))).Select(h => h.Label));
            Assert.True(leaked.Length == 0, $"A compose file's hints showed secrets: {leaked}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Compose fetches a Git repository's or an OCI artifact's compose file that an <c>include</c> names, unless
    /// told not to: the check, which reads the file on this computer alone, asks nothing of either's host (a listener on
    /// loopback here, which no connection reaches) and marks nothing for it.</summary>
    [AvaloniaFact]
    public void A_compose_files_remote_includes_are_never_fetched_and_mark_nothing()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        using Listener host = new();
        string folder = Folder(("git/compose.yaml", $"include:\n  - https://127.0.0.1:{host.Port}/shop.git\nservices:\n  web:\n    image: busybox:${{TAG:-1.37}}\n"),
            ("oci/compose.yaml", $"include:\n  - oci://127.0.0.1:{host.Port}/shop/compose:1\nservices:\n  web:\n    image: busybox:${{TAG:-1.37}}\n"));
        MainWindow window = Open(folder, "git/compose.yaml");
        try
        {
            OpenDocument git = window.EditorArea.Active!;
            window.OpenFile(PathOf(folder, "oci/compose.yaml"));
            Ui.Settle();
            OpenDocument oci = window.EditorArea.Active!;
            string[] expected = ["${TAG:-1.37}|= 1.37"];
            Ui.WaitFor(() => Hints(git, expected).SequenceEqual(expected) && Hints(oci, expected).SequenceEqual(expected), "both files checked");
            Assert.True(host.Connections == 0, $"Checking a compose file reached an included repository's host {host.Connections} times");
            string marked = string.Join(", ", git.Markers.Concat(oci.Markers).Where(m => m.Message.StartsWith("Compose:", StringComparison.Ordinal)).Select(m => m.Message));
            Assert.True(marked.Length == 0, $"A remote include not fetched was marked: {marked}");
            Assert.Empty(window.BottomPanel.Problems.ContainerProblems);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A port on loopback that counts the connections made to it.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task accepting;
        private int connections;

        public Listener()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accepting = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using TcpClient client = await listener.AcceptTcpClientAsync();
                        Interlocked.Increment(ref connections);
                    }
                }
                catch (Exception stopped) when (stopped is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // Disposed.
                }
            });
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref connections) + (listener.Pending() ? 1 : 0);

        public void Dispose()
        {
            listener.Stop();
            accepting.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [AvaloniaFact]
    public void A_compose_files_error_is_marked_where_compose_names_it_and_listed_in_problems_until_it_is_fixed()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = Folder(("compose.yaml", "services:\n  web:\n    image: busybox:1.37\n    depends_on:\n      - cache\n"));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            const string Said = "Compose: service \"web\" depends on undefined service \"cache\": invalid compose project";
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == Said), $"Compose's error marked, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]");
            Marker error = document.Markers.Single(m => m.Message == Said);
            Assert.Equal((MarkerKind.Error, "cache"), (error.Kind, document.Text.Snapshot.GetText(error.Range.Start, error.Range.Length)));
            Problem listed = Assert.Single(window.BottomPanel.Problems.ContainerProblems);
            Assert.Equal((PathOf(folder, "compose.yaml"), new TextPosition(4, 8), Said), (listed.Path, listed.Position, listed.Message));

            // The service it depends on added (typed, not saved): Compose reads the text as typed once typing pauses.
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.ApplyEdits([new TextEdit(document.Text.Snapshot.Length, 0, "  cache:\n    image: busybox:1.37\n")]);
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("Compose:", StringComparison.Ordinal)) && window.BottomPanel.Problems.ContainerProblems.Count == 0,
                "Compose's error gone once the service is there");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Compose's error that echoes what it interpolated from a secret's variable in the .env says it masked, by
    /// Passenger's one rule for secrets, wherever it is said: the compose file's marker and Problems' Containers tab as the
    /// engine checks the file, and the run of its configuration, which says why it cannot run in the status bar, in Problems
    /// and to an agent.</summary>
    [AvaloniaFact]
    public void Composes_error_echoing_a_secrets_value_says_it_masked_in_the_marker_problems_the_status_bar_and_to_an_agent()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        const string Secret = "s3cr3t-hunter2";
        string folder = Folder(("compose.yaml", "services:\n  web:\n    image: busybox:1.37\n    cpus: ${DB_PASSWORD}\n"), (".env", $"DB_PASSWORD={Secret}\n"));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("parsing", StringComparison.Ordinal)),
                $"Compose's error of the value it could not read marked, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]");
            string marked = document.Markers.First(m => m.Message.Contains("parsing", StringComparison.Ordinal)).Message;
            Assert.True(marked.Contains($"\"{ContainerSecrets.Masked}\"", StringComparison.Ordinal) && !marked.Contains(Secret, StringComparison.Ordinal),
                $"The marker said the secret's value: {marked}");
            Problem listed = Assert.Single(window.BottomPanel.Problems.ContainerProblems);
            Assert.Equal(marked, listed.Message);

            Assert.True(window.SelectConfiguration("Compose: compose.yaml"), "The compose file's configuration was not made");
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.RunProgram) is null && window.Status?.Contains("parsing", StringComparison.Ordinal) == true,
                $"Run to say why the configuration cannot run ({window.Status})");
            Assert.True(window.Status!.Contains(ContainerSecrets.Masked, StringComparison.Ordinal) && !window.Status.Contains(Secret, StringComparison.Ordinal),
                $"The status bar said the secret's value: {window.Status}");
            Assert.DoesNotContain(window.BottomPanel.Problems.ContainerProblems, p => p.Message.Contains(Secret, StringComparison.Ordinal));

            Exception refused = Assert.ThrowsAny<Exception>(() => Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Run("Compose: compose.yaml"), "an agent's run"));
            Assert.True(refused.Message.Contains(ContainerSecrets.Masked, StringComparison.Ordinal) && !refused.Message.Contains(Secret, StringComparison.Ordinal),
                $"An agent's run said the secret's value: {refused.Message}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A name Compose finds a service uses that the file does not declare is marked where it is written, and Show
    /// Context Actions there offers Passenger's own fixes first: a service depended on that is not there, removed from
    /// depends_on or changed to the service of a near name; a volume or secret declared at the top level, under its key or
    /// a key made for it, as the file indents. Each is one undo step, and the marker goes once Compose reads the text again.</summary>
    [AvaloniaFact]
    public void A_compose_files_undeclared_names_are_fixed_by_changing_removing_or_declaring_them_each_one_undo_step()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        const string Compose = "services:\n  web:\n    image: busybox:1.37\n    depends_on:\n      - dbb\n      - cache\n  db:\n    image: busybox:1.37\n"
            + "  cache:\n    image: busybox:1.37\n";
        string folder = Folder(("compose.yaml", Compose));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;

            Marker misspelt = Marked(window, document, "dbb");
            Assert.Equal(new TextPosition(4, 8), document.Text.Snapshot.GetPosition(misspelt.Range.Start));
            Fixes(window, misspelt, ["Remove dbb from depends_on", "Change to db"]);
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            string changed = Compose.Replace("- dbb", "- db", StringComparison.Ordinal);
            Assert.Equal(changed, editor.Document.Snapshot.ToString());
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(Compose, editor.Document.Snapshot.ToString());
            Ui.Run(window, EditorActions.Redo);
            Assert.Equal(changed, editor.Document.Snapshot.ToString());
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("Compose:", StringComparison.Ordinal)), "Compose's error gone once the name is the service's");

            editor.ApplyEdits([new TextEdit(changed.IndexOf("- db", StringComparison.Ordinal) + 2, 2, "queue")]);
            Marker missing = Marked(window, document, "queue");
            Fixes(window, missing, ["Remove queue from depends_on"]);
            Ui.Press(window, "ENTER");
            Assert.Equal(Compose.Replace("      - dbb\n", string.Empty, StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("Compose:", StringComparison.Ordinal)), "Compose's error gone once it is taken out");

            string before = editor.Document.Snapshot.ToString();
            int db = before.IndexOf("  db:", StringComparison.Ordinal);
            editor.ApplyEdits([new TextEdit(db, 0, "    volumes:\n      - data:/data\n    secrets:\n      - api_key\n")]);
            Fixes(window, Marked(window, document, "data"), ["Declare data at the top level"]);
            Ui.Press(window, "ENTER");
            string declared = editor.Document.Snapshot.ToString();
            Assert.EndsWith("    image: busybox:1.37\nvolumes:\n  data:\n", declared, StringComparison.Ordinal);
            Ui.Run(window, EditorActions.Undo);
            Assert.DoesNotContain("\nvolumes:", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Ui.Run(window, EditorActions.Redo);
            Assert.Equal(declared, editor.Document.Snapshot.ToString());
            Fixes(window, Marked(window, document, "api_key"), ["Declare api_key at the top level"]);
            Assert.Equal("under secrets, read from ./api_key.txt", window.Assist.OfferedDetails[0]);
            Ui.Press(window, "ENTER");
            Assert.EndsWith("volumes:\n  data:\nsecrets:\n  api_key:\n    file: ./api_key.txt\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("Compose:", StringComparison.Ordinal)) && window.BottomPanel.Problems.ContainerProblems.Count == 0,
                $"every Compose error gone once the volume and secret are declared ({string.Join(", ", document.Markers.Select(m => m.Message))})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A fix is worked out again on the text as it stands when it is chosen: a line added above while its list
    /// shows still declares the secret where it belongs, under the file's own key and indentation; one the file no longer
    /// needs (declared by hand meanwhile) is refused, saying so, and changes nothing.</summary>
    [AvaloniaFact]
    public void A_compose_fix_is_worked_out_again_as_it_is_chosen_and_refused_once_the_file_no_longer_needs_it()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        const string Compose = "services:\n    web:\n        image: busybox:1.37\n        secrets:\n            - api_key\nsecrets:\n    other:\n        file: ./other.txt\n";
        string folder = Folder(("compose.yaml", Compose));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            Fixes(window, Marked(window, document, "api_key"), ["Declare api_key at the top level"]);
            editor.ApplyEdits([new TextEdit(0, 0, "# The shop\n")]);
            Ui.Press(window, "ENTER");
            Assert.Equal("# The shop\n" + Compose + "    api_key:\n        file: ./api_key.txt\n", editor.Document.Snapshot.ToString());

            Ui.Run(window, EditorActions.Undo);
            string asked = editor.Document.Snapshot.ToString();
            Fixes(window, Marked(window, document, "api_key"), ["Declare api_key at the top level"]);
            editor.ApplyEdits([new TextEdit(asked.Length, 0, "    api_key:\n        environment: API_KEY\n")]);
            string byHand = editor.Document.Snapshot.ToString();
            Ui.Press(window, "ENTER");
            Assert.Equal(byHand, editor.Document.Snapshot.ToString());
            Assert.Equal("Declare api_key at the top level was not applied: the file changed, so it no longer fits", window.Status);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An agent's code_actions on Compose's marker list Passenger's own fixes first, and apply_code_action makes one
    /// on the text as it stands, the file saved. Skipped without a Node.js too, since the agents' code tools serve a compose
    /// file through its YAML server.</summary>
    [AvaloniaFact]
    public void An_agent_lists_a_compose_files_own_fixes_first_and_applies_one_saved()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        DockerFilesTests.Shop.SkipWithoutNode(yaml: true);
        const string Compose = "services:\n  web:\n    image: busybox:1.37\n    networks:\n      - backend\n";
        string folder = Folder(("compose.yaml", Compose));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            Marked(window, window.EditorArea.Active!, "backend");
            Passenger.App.Mcp.PassengerTools agent = new(window);
            IReadOnlyList<string> actions = Ui.Await(agent.CodeActions("compose.yaml", 5, 9), "the code actions an agent asks for");
            Assert.Equal("Declare backend at the top level [quickfix]", actions[0]);
            string applied = Ui.Await(agent.ApplyCodeAction("compose.yaml", 5, 9, "Declare backend at the top level"), "the agent's fix made");
            Assert.Equal("Applied 'Declare backend at the top level' to compose.yaml", applied);
            Assert.Equal(Compose + "networks:\n  backend:\n", File.ReadAllText(PathOf(folder, "compose.yaml")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A .NET service whose project reads connection strings neither its environment nor its settings set is
    /// warned of on its name, once the language server has read the project; one its appsettings give at localhost (in
    /// its container, the container itself) more softly; one given elsewhere not at all. Show Context Actions offers
    /// Passenger's fix first: set in the service's environment, one undo step, to the Postgres the file runs, else to a
    /// value the person fills in; a localhost one of a kind the file does not run has none.</summary>
    [AvaloniaFact]
    public void A_dotnet_services_connection_strings_its_environment_never_sets_are_marked_and_set_by_their_fixes()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = ConnectionsFolder(Connections);
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            Marker db = Unset(window, document, "db");
            Assert.Equal(("api", MarkerKind.Warning), (Under(document, db), db.Kind));
            Assert.Equal("api runs Api, which reads the connection string db: the service's environment never sets ConnectionStrings__db", db.Message);
            Unset(window, document, "cache");
            Marker shop = Local(document, "shop");
            Assert.Equal(("api", MarkerKind.Info), (Under(document, shop), shop.Kind));
            Assert.Equal("api runs Api, whose appsettings.json gives the connection string shop at localhost, which in its container is the container itself: "
                + "set ConnectionStrings__shop in the service's environment to db's connection string", shop.Message);
            Assert.Equal("api runs Api, whose appsettings.Development.json gives the connection string queue at localhost, which in its container is the container itself: "
                + "set ConnectionStrings__queue in the service's environment to where it runs (a service of this file by its name, host.docker.internal for this machine)",
                Local(document, "queue").Message);
            Assert.DoesNotContain(document.Markers, m => m.Message.Contains("logs", StringComparison.Ordinal));
            Assert.Contains(window.BottomPanel.Problems.ContainerProblems, p => p.Message == db.Message);

            editor.Selections = SelectionSet.Single(db.Range.Start + 1);
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes of the connection strings api's environment never sets");
            Assert.Equal(["Set ConnectionStrings__db in api's environment", "Set ConnectionStrings__shop in api's environment", "Set ConnectionStrings__cache in api's environment"],
                window.Assist.Offered.Take(3));
            Assert.Equal(["to db's connection string", "to db's connection string", "to a value you fill in"], window.Assist.OfferedDetails.Take(3));
            Assert.DoesNotContain(window.Assist.Offered, o => o.Contains("ConnectionStrings__queue", StringComparison.Ordinal));
            Ui.Press(window, "ENTER");
            string set = Connections.Replace("      - db\n", "      - db\n    environment:\n"
                + "      ConnectionStrings__db: \"Host=db;Port=5432;Database=db;Username=postgres;Password=${POSTGRES_PASSWORD:-postgres}\"\n", StringComparison.Ordinal);
            Assert.Equal(set, editor.Document.Snapshot.ToString());
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(Connections, editor.Document.Snapshot.ToString());
            Ui.Run(window, EditorActions.Redo);
            Assert.Equal(set, editor.Document.Snapshot.ToString());
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.EndsWith("ConnectionStrings__db", StringComparison.Ordinal))
                && document.Markers.Any(m => m.Message.EndsWith("ConnectionStrings__cache", StringComparison.Ordinal)),
                $"db's warning gone once its environment sets it, cache's kept ({string.Join(", ", document.Markers.Select(m => m.Message))})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An agent's code_actions on a service's name list the fixes of the connection strings its environment does
    /// not set first: the one its settings give at localhost, the ones never given (in Production, which the service sets,
    /// appsettings.Development.json is not read), not the one its env_file sets; apply_code_action makes one on the text as
    /// it stands, the file saved, after the entry the file has. Skipped without a Node.js too, since the agents' code tools
    /// serve a compose file through its YAML server.</summary>
    [AvaloniaFact]
    public void An_agent_sets_a_connection_string_a_services_environment_never_sets_through_its_fix()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        DockerFilesTests.Shop.SkipWithoutNode(yaml: true);
        string compose = Connections.Replace("      - db\n", "      - db\n    env_file: api.env\n    environment:\n      - ASPNETCORE_ENVIRONMENT=Production\n", StringComparison.Ordinal);
        string folder = ConnectionsFolder(compose, ("api.env", "ConnectionStrings__db=Host=db;Username=postgres\n"));
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument document = window.EditorArea.Active!;
            Unset(window, document, "cache");
            Unset(window, document, "queue");
            Local(document, "shop");
            Assert.DoesNotContain(document.Markers, m => m.Message.EndsWith("ConnectionStrings__db", StringComparison.Ordinal));
            Passenger.App.Mcp.PassengerTools agent = new(window);
            IReadOnlyList<string> actions = Ui.Await(agent.CodeActions("compose.yaml", 2, 3), "the code actions an agent asks for");
            Assert.Equal(["Set ConnectionStrings__shop in api's environment [quickfix]", "Set ConnectionStrings__queue in api's environment [quickfix]",
                "Set ConnectionStrings__cache in api's environment [quickfix]"], actions.Take(3));
            Assert.DoesNotContain(actions, a => a.Contains("ConnectionStrings__db", StringComparison.Ordinal));
            string applied = Ui.Await(agent.ApplyCodeAction("compose.yaml", 2, 3, "Set ConnectionStrings__shop in api's environment"), "the agent's fix made");
            Assert.Equal("Applied 'Set ConnectionStrings__shop in api's environment' to compose.yaml", applied);
            Assert.Equal(compose.Replace("      - ASPNETCORE_ENVIRONMENT=Production\n", "      - ASPNETCORE_ENVIRONMENT=Production\n"
                + "      - ConnectionStrings__shop=Host=db;Port=5432;Database=shop;Username=postgres;Password=${POSTGRES_PASSWORD:-postgres}\n", StringComparison.Ordinal),
                File.ReadAllText(PathOf(folder, "compose.yaml")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The connection strings a .NET service reads that its compose file's environment does not set are marked
    /// in its own code too, where it reads each (GetConnectionString's name, a ConnectionStrings: key), the one its
    /// settings give at localhost more softly. Show Context Actions on a read offers the compose file's fix first, one
    /// undo step there, the code left as it was, and the read's mark goes with the compose file's. An agent's
    /// code_actions on a read lists the same fix first, and apply_code_action makes it, saved.</summary>
    [AvaloniaFact]
    public void A_services_reads_of_connection_strings_its_compose_environment_never_sets_are_marked_in_its_code_and_set_from_there()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = ConnectionsFolder(Connections);
        MainWindow window = Open(folder, "compose.yaml");
        try
        {
            OpenDocument compose = window.EditorArea.Active!;
            Unset(window, compose, "db");
            window.OpenFile(PathOf(folder, "Api/Program.cs"));
            Ui.Settle();
            OpenDocument program = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            const string Db = "compose.yaml: api runs Api, which reads the connection string db: the service's environment never sets ConnectionStrings__db";
            Ui.WaitFor(() => program.Markers.Any(m => m.Message == Db), $"db's read marked with the compose file's warning, not [{string.Join(", ", program.Markers.Select(m => m.Message))}]");
            Marker read = program.Markers.First(m => m.Message == Db);
            Assert.Equal((MarkerKind.Warning, "\"db\""), (read.Kind, Under(program, read)));
            Assert.Contains(program.Markers, m => m.Message == "compose.yaml: api runs Api, which reads the connection string cache: the service's environment never sets ConnectionStrings__cache"
                && Under(program, m) == "\"ConnectionStrings:cache\"");
            Assert.Contains(program.Markers, m => m.Kind == MarkerKind.Info && Under(program, m) == "\"shop\""
                && m.Message.StartsWith("compose.yaml: api runs Api, whose appsettings.json gives the connection string shop at localhost", StringComparison.Ordinal));

            editor.Selections = SelectionSet.Single(read.Range.Start + 1);
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes of db's read");
            Assert.Equal(("Set ConnectionStrings__db in api's environment", "in compose.yaml, to db's connection string"), (window.Assist.Offered[0], window.Assist.OfferedDetails[0]));
            string code = editor.Document.Snapshot.ToString();
            Ui.Press(window, "ENTER");
            string set = Connections.Replace("      - db\n", "      - db\n    environment:\n"
                + "      ConnectionStrings__db: \"Host=db;Port=5432;Database=db;Username=postgres;Password=${POSTGRES_PASSWORD:-postgres}\"\n", StringComparison.Ordinal);
            Ui.WaitFor(() => compose.Text.Snapshot.ToString() == set, $"the compose file to set db's connection string in api's environment ({window.Status})");
            Assert.Equal(("Set ConnectionStrings__db in api's environment in compose.yaml", code), (window.Status, editor.Document.Snapshot.ToString()));
            Ui.WaitFor(() => !program.Markers.Any(m => m.Message == Db) && program.Markers.Any(m => m.Message.EndsWith("never sets ConnectionStrings__cache", StringComparison.Ordinal)),
                $"db's read's mark to go once the compose file sets it, cache's kept ({string.Join(", ", program.Markers.Select(m => m.Message))})");
            // The compose file open, the change is its own undo step there.
            window.OpenFile(PathOf(folder, "compose.yaml"));
            Ui.Settle();
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(Connections, compose.Text.Snapshot.ToString());
            Ui.WaitFor(() => program.Markers.Any(m => m.Message == Db), "db's read marked again once the compose file no longer sets it");

            Passenger.App.Mcp.PassengerTools agent = new(window);
            int at = code.IndexOf("GetConnectionString(\"d", StringComparison.Ordinal) + "GetConnectionString(\"d".Length;
            int line = code[..at].Count(c => c == '\n') + 1;
            int column = at - code.LastIndexOf('\n', at - 1);
            IReadOnlyList<string> actions = Ui.Await(agent.CodeActions(Path.Combine("Api", "Program.cs"), line, column), "the code actions an agent asks for");
            Assert.Equal("Set ConnectionStrings__db in api's environment [quickfix]", actions[0]);
            string applied = Ui.Await(agent.ApplyCodeAction(Path.Combine("Api", "Program.cs"), line, column, "Set ConnectionStrings__db in api's environment"), "the agent's fix made");
            Assert.Equal("Applied 'Set ConnectionStrings__db in api's environment' to compose.yaml", applied);
            Assert.Equal(set, File.ReadAllText(PathOf(folder, "compose.yaml")));
            Assert.Equal(code, File.ReadAllText(PathOf(folder, "Api/Program.cs")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A compose file whose api service is built from a Dockerfile that runs the Api project, beside the Postgres it
    /// depends on, named db.</summary>
    private const string Connections = "services:\n  api:\n    build: ./Api\n    depends_on:\n      - db\n  db:\n    image: postgres:17\n";

    /// <summary>A folder of the test's own with a web project, Api, whose code reads the connection strings db, shop, queue
    /// and logs (by GetConnectionString) and cache (by its ConnectionStrings: key), its settings giving shop at localhost and
    /// logs elsewhere, its Development settings queue at localhost; its Dockerfile, and the compose file given, restored.</summary>
    private static string ConnectionsFolder(string compose, params (string Name, string Text)[] more)
    {
        string folder = Folder([
            ("Shop.slnx", "<Solution>\n  <Project Path=\"Api/Api.csproj\" />\n</Solution>\n"),
            ("Api/Api.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>", "<Nullable>enable</Nullable>")),
            ("Api/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nstring? db = builder.Configuration.GetConnectionString(\"db\");\n"
                + "string? shop = builder.Configuration.GetConnectionString(\"shop\");\nstring? queue = builder.Configuration.GetConnectionString(\"queue\");\n"
                + "string? logs = builder.Configuration.GetConnectionString(\"logs\");\n"
                + "string? cache = builder.Configuration[\"ConnectionStrings:cache\"];\nvar app = builder.Build();\napp.Run();\n"),
            ("Api/appsettings.json", "{\n  \"ConnectionStrings\": {\n    \"shop\": \"Host=localhost;Database=shop;Username=postgres;Password=postgres\",\n"
                + "    \"logs\": \"Host=logs.example.com;Database=logs\"\n  }\n}\n"),
            ("Api/appsettings.Development.json", "{\n  \"ConnectionStrings\": {\n    \"queue\": \"amqp://guest:guest@localhost:5672\"\n  }\n}\n"),
            ("Api/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /app\nCOPY bin/Release/net10.0/publish .\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n"),
            ("compose.yaml", compose),
            .. more]);
        Restore.Run(PathOf(folder, "Shop.slnx"));
        return folder;
    }

    /// <summary>The warning that the service's environment never sets a connection string, once the language server has
    /// loaded the project and the compose file is checked again.</summary>
    private static Marker Unset(MainWindow window, OpenDocument document, string name)
    {
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the project ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.EndsWith($"never sets ConnectionStrings__{name}", StringComparison.Ordinal)),
            $"the warning that ConnectionStrings__{name} is never set, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]", TimeSpan.FromMinutes(1));
        return document.Markers.First(m => m.Message.EndsWith($"never sets ConnectionStrings__{name}", StringComparison.Ordinal));
    }

    /// <summary>The softer warning that the project's settings give a connection string at localhost, once marked.</summary>
    private static Marker Local(OpenDocument document, string name)
    {
        string said = $"gives the connection string {name} at localhost";
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains(said, StringComparison.Ordinal)),
            $"the warning that the settings give {name} at localhost, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]", TimeSpan.FromMinutes(1));
        return document.Markers.First(m => m.Message.Contains(said, StringComparison.Ordinal));
    }

    /// <summary>Compose's marker on a name, once its check has run, with the caret put on it.</summary>
    private static Marker Marked(MainWindow window, OpenDocument document, string name)
    {
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.StartsWith("Compose:", StringComparison.Ordinal) && Under(document, m) == name),
            $"Compose's error marked on {name}, not on [{string.Join(", ", document.Markers.Select(m => $"{Under(document, m)}: {m.Message}"))}]");
        Marker marker = document.Markers.First(m => m.Message.StartsWith("Compose:", StringComparison.Ordinal) && Under(document, m) == name);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(marker.Range.Start + 1);
        return marker;
    }

    private static string Under(OpenDocument document, Marker marker) =>
        marker.Range.End <= document.Text.Snapshot.Length ? document.Text.Snapshot.GetText(marker.Range.Start, marker.Range.Length) : string.Empty;

    /// <summary>Show Context Actions on a marker: Passenger's own fixes first, these and no other of its own.</summary>
    private static void Fixes(MainWindow window, Marker marker, string[] expected)
    {
        Assert.Equal(MarkerKind.Error, marker.Kind);
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, $"the fixes of {marker.Message}");
        string[] own = [.. window.Assist.Offered.Where(o => o.StartsWith("Remove ", StringComparison.Ordinal) || o.StartsWith("Change to ", StringComparison.Ordinal)
            || o.StartsWith("Declare ", StringComparison.Ordinal))];
        Assert.Equal(expected, own);
        Assert.Equal(expected, window.Assist.Offered.Take(expected.Length));
    }

    /// <summary>Without a Node.js, a compose file's language server cannot start, but the engine still checks it: each file
    /// opened after the first waits for no server's checks, and an agent's diagnostics answer the engine's findings with a
    /// word that the server's are missing and why, rather than refusing.</summary>
    [AvaloniaFact]
    public void Without_a_node_js_an_agents_diagnostics_of_a_compose_file_are_the_engines_with_why_the_servers_are_missing()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        const string Broken = "services:\n  web:\n    image: busybox:1.37\n    depends_on:\n      - cache\n";
        string folder = Folder(("compose.yaml", Broken), ("other/compose.yaml", Broken));
        string missing = Path.Combine(TestRun.Root, "no-node-here", OperatingSystem.IsWindows() ? "node.exe" : "node");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        try
        {
            window.ApplySettings(window.Settings with { NodeInterpreter = missing });
            window.OpenFolder(folder);
            window.OpenFile(PathOf(folder, "compose.yaml"));
            Ui.Settle();
            window.OpenFile(PathOf(folder, Path.Combine("other", "compose.yaml")));
            Ui.Settle();
            OpenDocument second = window.EditorArea.Active!;
            Assert.False(second.IsChecking, "A compose file opened after the YAML server could not start waits for its checks");

            const string Said = "Compose: service \"web\" depends on undefined service \"cache\": invalid compose project";
            IReadOnlyList<Passenger.App.Mcp.Finding> found = (IReadOnlyList<Passenger.App.Mcp.Finding>)Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Diagnostics("other/compose.yaml"), "the diagnostics an agent asks for");
            Assert.Contains(found, f => f.Severity == "error" && f.Message == Said);
            Passenger.App.Mcp.Finding note = Assert.Single(found, f => f.Severity == "info");
            Assert.Equal($"Checking it beyond the container engine needs the YAML language server: there is no Node.js at {missing}, which Settings | Languages & Frameworks | Node.js names",
                note.Message);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void With_no_engine_running_a_dockerfile_and_a_compose_file_show_nothing_of_it_and_problems_say_only_the_engines_state()
    {
        string folder = Folder(("Dockerfile", Dockerfile), ("compose.yaml", "services:\n  web:\n    image: busybox\n    ports: [\"${PORT:-8080}:80\"]\n"));
        MainWindow window = new() { Width = 1200, Height = 800, Engine = new ContainerEngine(cli: ContainersTests.StandInCli(TestFolders.Create("ctr-cli-"))) };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(PathOf(folder, "Dockerfile"));
        Ui.Settle();
        try
        {
            OpenDocument dockerfile = window.EditorArea.Active!;
            window.OpenFile(PathOf(folder, "compose.yaml"));
            Ui.Settle();
            OpenDocument compose = window.EditorArea.Active!;
            Ui.WaitFor(() => window.BottomPanel.Problems.ContainerProblems.Count > 0, "the engine's state in Problems");
            Problem engine = Assert.Single(window.BottomPanel.Problems.ContainerProblems);
            Assert.StartsWith("Docker isn't running", engine.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(dockerfile.Markers, m => m.Message.StartsWith("BuildKit:", StringComparison.Ordinal));
            Assert.Empty(compose.InlayHints);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The hint after <c>$PWD</c>: the compose file's folder, which Compose runs in, never where the IDE was started.</summary>
    private static string FolderHint(string folder)
    {
        string pwd = PathNames.Canonical(folder);
        return $"$PWD|= {(pwd.Length > 48 ? pwd[..47] + "…" : pwd)}";
    }

    /// <summary>Each value hint as the stretch of text it follows (the expected one it ends, else its line up to it) and its
    /// label, in order.</summary>
    private static string[] Hints(OpenDocument document, string[] expected)
    {
        string text = document.Text.Snapshot.ToString();
        return [.. document.InlayHints.Where(h => h.Kind == InlayHintKind.Value).Select(h =>
        {
            string before = text[..h.Offset];
            string? known = expected.Select(e => e.Split('|')[0]).FirstOrDefault(e => before.EndsWith(e, StringComparison.Ordinal));
            return $"{known ?? before[(before.LastIndexOf('\n') + 1)..]}|{h.Label}";
        })];
    }

    private static void SkipWithoutBuildx()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        EngineState state = Task.Run(() => ContainerEngine.Shared.StateAsync()).GetAwaiter().GetResult();
        Assert.SkipWhen(state.BuildxVersion is null && !state.IsPodman, "docker buildx, whose build checks a Dockerfile's are, is not installed here");
    }

    /// <summary>A folder of the test's own holding the files given.</summary>
    private static string Folder(params (string Name, string Text)[] files)
    {
        string folder = TestFolders.Create("engine-checks-");
        foreach ((string name, string text) in files)
        {
            string path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return folder;
    }

    private static string PathOf(string folder, string name) => Path.Combine(PathNames.Canonical(folder), name);

    private static MainWindow Open(string folder, string file)
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(PathOf(folder, file));
        Ui.Settle();
        return window;
    }
}
