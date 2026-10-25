using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Roslyn.Extension;
using Passenger.App.Run;
using Passenger.Workspace;

namespace Passenger.Tests;

public sealed class AspireCodeHelpTests : IClassFixture<AspireCodeHelpTests.Project>
{
    private readonly Project project;

    public AspireCodeHelpTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void AddProject_navigates_to_the_real_project_but_a_resource_identifier_to_its_declaration()
    {
        MainWindow window = project.Open(project.PathOf("Host/Program.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "AddProject<Projects.Ap");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "Api.csproj", "AddProject to open Api.csproj, not generated metadata");
        Assert.True(PathNames.Same(PathNames.Canonical(project.PathOf("Api/Api.csproj")), window.EditorArea.Active!.Path));

        window.OpenFile(project.PathOf("Host/Program.cs"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "WithReference(connec");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Program.cs", window.EditorArea.Active!.Name);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Contains("var connected =", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A connection string a service reads, by GetConnectionString or a ConnectionStrings: key, and a service
    /// it discovers, by an https+http:// address or a services: key, go to the AppHost line that declares the resource,
    /// at its name.</summary>
    [AvaloniaFact]
    public void A_service_s_connection_string_or_discovered_service_goes_to_the_AppHost_line_declaring_it()
    {
        project.Rewrite("Api/Program.cs", Project.Service.Replace("builder.Build().Run();",
            "var keyed = builder.Configuration[\"ConnectionStrings:connected\"];\nvar section = builder.Configuration.GetSection(\"services:worker:http:0\");\nbuilder.Build().Run();", StringComparison.Ordinal));
        MainWindow window = project.Open(project.PathOf("Api/Program.cs"));
        try
        {
            GoesTo(window, "GetConnectionString(\"conn", "var connected = builder.AddConnectionString(\"", "connected");
            GoesTo(window, "ConnectionStrings:conn", "var connected = builder.AddConnectionString(\"", "connected");
            GoesTo(window, "services:wor", "var worker = builder.AddProject<Projects.Worker>(\"", "worker");
            window.OpenFile(project.PathOf("Worker/Program.cs"));
            Ui.Settle();
            GoesTo(window, "https+http://ap", "var api = builder.AddProject<Projects.Api>(\"", "api");
        }
        finally
        {
            window.Close();
        }

        void GoesTo(MainWindow window, string after, string declaring, string name)
        {
            string from = window.EditorArea.Active!.Path;
            Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => PathNames.Same(window.EditorArea.Active!.Path, PathNames.Canonical(project.PathOf("Host/Program.cs"))),
                $"Go to Declaration on {after}… to open the AppHost");
            CodeEditor editor = window.EditorArea.CodeEditor;
            string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
            Assert.True(line.StartsWith(declaring, StringComparison.Ordinal) && editor.CaretPosition.Column == declaring.Length
                && line[declaring.Length..].StartsWith(name, StringComparison.Ordinal),
                $"Go to Declaration on {after}… did not land at {name} where the AppHost declares it, but at column {editor.CaretPosition.Column + 1} of: {line}");
            window.OpenFile(from);
            Ui.Settle();
        }
    }

    /// <summary>Rename on a resource's name, where a service reads it or where the AppHost declares it, renames it in the
    /// AppHost and in every service that reads it (a connection string's name, a ConnectionStrings: key, a discovered
    /// service's address), and global undo takes the whole rename back; another resource's names stay as they were.</summary>
    [AvaloniaFact]
    public void Renaming_a_resource_renames_it_in_the_AppHost_and_the_services_that_read_it_and_one_undo_takes_it_back()
    {
        project.Rewrite("Worker/Program.cs", Project.Worker.Replace("builder.Build().Run();",
            "var store = builder.Configuration[\"ConnectionStrings:connected\"];\nbuilder.Build().Run();", StringComparison.Ordinal));
        string[] files = ["Host/Program.cs", "Api/Program.cs", "Worker/Program.cs"];
        Dictionary<string, string> before = files.ToDictionary(f => f, f => File.ReadAllText(project.PathOf(f)));
        MainWindow window = project.Open(project.PathOf("Host/Program.cs"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        try
        {
            // Open, as a person works on them, so the server reads each edit as it is made rather than from the disk.
            window.OpenFile(project.PathOf("Worker/Program.cs"));
            window.OpenFile(project.PathOf("Api/Program.cs"));
            Ui.Settle();
            Rename(window, "GetConnectionString(\"conn", "connected", "store");
            Ui.Run(window, AppActions.SaveAll);
            Assert.Contains("builder.AddConnectionString(\"store\")", File.ReadAllText(project.PathOf("Host/Program.cs")), StringComparison.Ordinal);
            Assert.Contains("GetConnectionString(\"store\")", File.ReadAllText(project.PathOf("Api/Program.cs")), StringComparison.Ordinal);
            Assert.Contains("GetConnectionString(\"db\")", File.ReadAllText(project.PathOf("Api/Program.cs")), StringComparison.Ordinal);
            Assert.Contains("[\"ConnectionStrings:store\"]", File.ReadAllText(project.PathOf("Worker/Program.cs")), StringComparison.Ordinal);

            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.Run(window, AppActions.SaveAll);
            foreach (string file in files)
            {
                Assert.True(File.ReadAllText(project.PathOf(file)) == before[file], $"Undo of the rename left {file} changed:\n{File.ReadAllText(project.PathOf(file))}");
            }

            window.OpenFile(project.PathOf("Host/Program.cs"));
            Ui.Settle();
            Rename(window, "AddProject<Projects.Api>(\"a", "api", "backend");
            Ui.Run(window, AppActions.SaveAll);
            Assert.Contains("AddProject<Projects.Api>(\"backend\")", File.ReadAllText(project.PathOf("Host/Program.cs")), StringComparison.Ordinal);
            Assert.Contains("\"https+http://backend\"", File.ReadAllText(project.PathOf("Worker/Program.cs")), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }

        static void Rename(MainWindow window, string after, string old, string name)
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == old, $"Rename's box on the resource {old}");
            window.KeyTextInput(name);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Status?.Contains($"{old} to {name}", StringComparison.Ordinal) == true,
                $"the rename of {old} to {name} to be made, or say why not");
            Assert.True(window.Status!.StartsWith($"Renamed {old} to {name}", StringComparison.Ordinal), $"The rename of {old} to {name} was not made: {window.Status}");
        }
    }

    /// <summary>A parameter the AppHost declares with no value of its own is marked on its name until its configuration has
    /// one; its fix asks for the value (masked for a secret) and sets it in the AppHost's user secrets for a secret (the run's
    /// own, never the person's) or in its appsettings.json, the file's other values kept, its mark going, the value never
    /// said; global undo takes each back. A parameter appsettings.json gives a value, or one with a value of its own, is not marked.</summary>
    [AvaloniaFact]
    public void A_parameter_with_no_value_is_marked_and_its_fix_sets_it_in_user_secrets_or_appsettings_and_undo_takes_it_back()
    {
        string secrets = Path.Combine(TestRun.UserSecrets, Project.SecretsId, "secrets.json");
        string settings = project.PathOf("Host/appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(secrets)!);
        File.Delete(secrets);
        project.Write("Host/Program.cs", Project.Host.Replace("builder.Build().Run();",
            "var password = builder.AddParameter(\"password\", secret: true);\nvar mode = builder.AddParameter(\"mode\");\n"
            + "var given = builder.AddParameter(\"given\");\nvar valued = builder.AddParameter(\"valued\", \"own\");\nbuilder.Build().Run();", StringComparison.Ordinal));
        string written = "{\n  \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } },\n  \"Parameters\": {\n    \"given\": \"set\"\n  }\n}\n";
        project.Write("Host/appsettings.json", written);
        MainWindow window = project.Open(project.PathOf("Host/Program.cs"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        try
        {
            Ui.WaitFor(() => UnsetParameters(window).SetEquals(["password", "mode"]), "the parameters with no value, and only those, to be marked");

            SetParameter(window, "AddParameter(\"pass", "Set password in the AppHost's user secrets…", secret: true, "s3cret-value");
            Ui.WaitFor(() => File.Exists(secrets) && File.ReadAllText(secrets).Contains("\"Parameters:password\": \"s3cret-value\"", StringComparison.Ordinal),
                "the secret to be set in the AppHost's user secrets");
            Ui.WaitFor(() => UnsetParameters(window).SetEquals(["mode"]), "the secret's mark to go once it has a value");
            Assert.True(window.Status == "Set password in the AppHost's user secrets", $"Setting the secret said: {window.Status}");

            SetParameter(window, "AddParameter(\"mo", "Set mode in the AppHost's appsettings.json…", secret: false, "fast");
            Ui.WaitFor(() => File.ReadAllText(settings).Contains("\"mode\": \"fast\"", StringComparison.Ordinal), "the value to be set in appsettings.json");
            Assert.Equal(written.Replace("\"given\": \"set\"\n", "\"given\": \"set\",\n    \"mode\": \"fast\"\n", StringComparison.Ordinal), File.ReadAllText(settings));
            Ui.WaitFor(() => UnsetParameters(window).Count == 0, "every mark to go once each parameter has a value");

            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(settings) == written, "undo to take the value back out of appsettings.json");
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
            Assert.False(File.Exists(secrets), $"Undo did not take back the user secrets it made: {window.Status}");
        }
        finally
        {
            window.Close();
            File.Delete(secrets);
        }
    }

    /// <summary>A secret parameter of an AppHost with no UserSecretsId: its fix gives the AppHost one (a GUID in its project
    /// file) and keeps the value in those user secrets, the run's own, in one command, the mark going once they give it;
    /// undo takes back both.</summary>
    [AvaloniaFact]
    public void A_secret_parameters_fix_gives_an_AppHost_with_no_UserSecretsId_one_and_keeps_the_value_in_its_secrets()
    {
        string projectFile = project.PathOf("OtherHost/OtherHost.csproj");
        string writtenProject = File.ReadAllText(projectFile);
        Assert.Null(ProjectEdits.PropertyIn(writtenProject, "UserSecretsId"));
        project.Rewrite("OtherHost/Program.cs", "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\n"
            + "var token = builder.AddParameter(\"token\", secret: true);\nbuilder.Build().Run();\n");
        MainWindow window = project.Open(project.PathOf("OtherHost/Program.cs"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        string? id = null;
        try
        {
            Ui.WaitFor(() => UnsetParameters(window).SetEquals(["token"]), "the secret parameter with no value to be marked");
            List<string> said = [];
            using IDisposable following = Avalonia.AvaloniaObjectExtensions.GetObservable(window.FindControl<TextBlock>("StatusText")!, TextBlock.TextProperty)
                .Subscribe(new Avalonia.Reactive.AnonymousObserver<string?>(text => said.Add(text ?? string.Empty)));
            SetParameter(window, "AddParameter(\"tok", "Set token in the AppHost's user secrets…", secret: true, "t0ken-value");
            Ui.WaitFor(() => ProjectEdits.PropertyIn(File.ReadAllText(projectFile), "UserSecretsId") is not null,
                () => $"the AppHost to be given a UserSecretsId ({window.Status})");
            id = ProjectEdits.PropertyIn(File.ReadAllText(projectFile), "UserSecretsId")!;
            Assert.True(Guid.TryParse(id, out _), $"The AppHost was not given a GUID for its UserSecretsId: {id}");
            string secrets = Path.Combine(TestRun.UserSecrets, id, "secrets.json");
            Ui.WaitFor(() => File.Exists(secrets) && File.ReadAllText(secrets).Contains("\"Parameters:token\": \"t0ken-value\"", StringComparison.Ordinal),
                "the secret to be kept in the AppHost's new user secrets, in the run's own folder");
            // The project read anew for its new id may say its own load's warning after.
            Assert.True(said.Contains("Set token in the AppHost's user secrets, giving it a UserSecretsId"), $"Setting the secret said: {string.Join(" | ", said)}");
            Ui.WaitFor(() => UnsetParameters(window).Count == 0, "the secret's mark to go once the AppHost's new user secrets give it");

            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(projectFile) == writtenProject && !File.Exists(secrets), "undo to take back the UserSecretsId and the secrets it made");
        }
        finally
        {
            window.Close();
            if (id is not null && Directory.Exists(Path.Combine(TestRun.UserSecrets, id)))
            {
                Directory.Delete(Path.Combine(TestRun.UserSecrets, id), recursive: true);
            }
        }
    }

    /// <summary>A parameter's value typed into the AppHost's appsettings.json is its value before it is saved: the mark on the
    /// parameter goes once typing pauses, read from the text the editor has, as its fix reads it, and the file on disk stays
    /// as it was.</summary>
    [AvaloniaFact]
    public void A_parameter_s_value_typed_in_appsettings_and_not_saved_takes_its_mark_away()
    {
        string program = PathNames.Canonical(project.PathOf("Host/Program.cs"));
        string settings = PathNames.Canonical(project.PathOf("Host/appsettings.json"));
        project.Write("Host/Program.cs", Project.Host.Replace("builder.Build().Run();", "var mode = builder.AddParameter(\"mode\");\nbuilder.Build().Run();", StringComparison.Ordinal));
        string written = "{\n  \"Parameters\": {\n    \"given\": \"set\"\n  }\n}\n";
        project.Write("Host/appsettings.json", written);
        MainWindow window = project.Open(program);
        try
        {
            Ui.WaitFor(() => Unset().SetEquals(["mode"]), "the parameter with no value to be marked");
            window.OpenFile(settings);
            Ui.Settle();
            Typing.Replace(window, "\"given\": \"set\"", "\"mode\": \"fast\"");
            Ui.WaitFor(() => Unset().Count == 0, () => $"the mark to go once appsettings.json in the editor gives mode a value, saved or not: {string.Join(", ", Unset())}");
            Assert.True(window.EditorArea.Find(settings)?.IsModified == true, "appsettings.json was saved, so the test no longer shows the editor's text is read");
            Assert.Equal(written, File.ReadAllText(settings));
        }
        finally
        {
            window.Close();
        }

        HashSet<string> Unset() => [.. window.EditorArea.Find(program)!.Markers
            .Where(m => m.Message.StartsWith("Parameter '", StringComparison.Ordinal)).Select(m => m.Message.Split('\'')[1])];
    }

    /// <summary>A parameter's fix chosen at the caret placed after some text, its value typed into the box it asks with
    /// (masked for a secret); what the window says after never holds the value.</summary>
    private static void SetParameter(MainWindow window, string after, string fix, bool secret, string value)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.QuickFix);
        Choose(window, fix);
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), $"{fix} to ask for the value");
        ConfirmDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>()));
        TextBox box = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        Assert.True(secret == (box.PasswordChar != default), $"{fix}'s box {(secret ? "shows a secret as it is typed" : "masks a value that is no secret")}");
        dialog.KeyTextInput(value);
        Ui.Press(dialog, "ENTER");
        Ui.Settle();
        Assert.DoesNotContain(value, window.Status ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The parameters the AppHost's file shown marks as having no value.</summary>
    private static HashSet<string> UnsetParameters(MainWindow window) => [.. window.EditorArea.Active!.Markers
        .Where(m => m.Message.StartsWith("Parameter '", StringComparison.Ordinal)).Select(m => m.Message.Split('\'')[1])];

    [AvaloniaFact]
    public void Connection_completion_offers_only_connected_resources_and_a_connection_warning_offers_a_native_reference_fix_undo_takes_back()
    {
        string host = File.ReadAllText(project.PathOf("Host/Program.cs"));
        string service = File.ReadAllText(project.PathOf("Api/Program.cs"));
        MainWindow window = project.Open(project.PathOf("Api/Program.cs"));
        window.Confirm = (_, _, _) => Task.FromResult(true);
        WaitForMissing(window, "api", "db");

        Ui.PlaceAfter(window.EditorArea.CodeEditor, "GetConnectionString(\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("connected"), "the connected database name to be offered");
        Assert.DoesNotContain("db", window.Assist.CompletionLabels);
        Assert.DoesNotContain("worker", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(window.EditorArea.CodeEditor, "GetConnectionString(\"d");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Add WithReference(db) to api"), "the native reference fix to be offered");
        Assert.Equal("Add WithReference(db) to api", window.Assist.Offered[0]);
        Assert.True(window.Assist.Offered.Count > 1, $"Only the Aspire fix was offered, without the language server's actions: {string.Join("; ", window.Assist.Offered)}");
        // Another file of the solution changes while the fix is offered.
        TaskCompletionSource refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler heard = (_, _) => refreshed.TrySetResult();
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        server.DiagnosticsRefreshRequested += heard;
        File.WriteAllText(project.PathOf("OtherHost/Program.cs"), "System.Console.WriteLine(\"Still no graph\");\n");
        Ui.WaitFor(() => refreshed.Task.IsCompleted, "the language server to ask for diagnostics again once another file changed");
        server.DiagnosticsRefreshRequested -= heard;
        Choose(window, "Add WithReference(db) to api");
        // Its refusals all name the Aspire reference, which its success does not.
        Ui.WaitFor(() => File.ReadAllText(project.PathOf("Host/Program.cs")).Contains("api.WithReference(db);", StringComparison.Ordinal)
            || window.Status?.Contains("Aspire reference", StringComparison.Ordinal) == true, "the native fix to add the missing AppHost edge, or say why not");
        Assert.True(File.ReadAllText(project.PathOf("Host/Program.cs")).Contains("api.WithReference(db);", StringComparison.Ordinal),
            $"The native fix chosen after another file changed added nothing to the AppHost: {window.Status}");
        Ui.WaitFor(() => !HasMissing(window), "the service warning to clear after adding its reference");
        Assert.Equal(service, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        Ui.Run(window, AppActions.FocusExplorer);
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.ReadAllText(project.PathOf("Host/Program.cs")) == host, "global undo to restore the AppHost");
        Assert.Equal(service, File.ReadAllText(project.PathOf("Api/Program.cs")));
        window.OpenFile(project.PathOf("Api/Program.cs"));
        Ui.Settle();
        Assert.Equal(service, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        WaitForMissing(window, "api", "db");
        window.Close();
    }

    /// <summary>A connection string a service reads by a name no resource of its AppHost has, by GetConnectionString or a
    /// ConnectionStrings: key, is warned of on the name unless the service's appsettings give it; Show Context Actions there
    /// offers Passenger's own Change to the declared name near it first, one edit Undo takes back, and its warning goes once
    /// the name is a resource's. An agent's code_actions lists the same fix first and apply_code_action makes it, saved.</summary>
    [AvaloniaFact]
    public void A_connection_string_no_resource_names_is_warned_of_unless_settings_give_it_and_changed_to_the_declared_name_near_it()
    {
        string service = Project.Service.Replace("builder.Build().Run();", "var typo = builder.Configuration.GetConnectionString(\"conected\");\n"
            + "var keyed = builder.Configuration[\"ConnectionStrings:connectd\"];\nvar given = builder.Configuration.GetConnectionString(\"settled\");\nbuilder.Build().Run();",
            StringComparison.Ordinal);
        project.Write("Api/Program.cs", service);
        project.Write("Api/appsettings.json", "{\n  \"ConnectionStrings\": {\n    \"settled\": \"Host=localhost;Database=settled\"\n  }\n}\n");
        MainWindow window = project.Open(project.PathOf("Api/Program.cs"));
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => Unknown(window).SetEquals(["conected", "connectd"]), $"the names no resource has, and only those, to be warned of: {string.Join(", ", Unknown(window))}");
            Assert.Contains(window.EditorArea.Active!.Markers, m => m.Kind == MarkerKind.Warning && m.Message
                == "No resource of the AppHost is named 'conected', and the service's appsettings give no connection string of that name. Did you mean 'connected'?");

            Ui.PlaceAfter(editor, "GetConnectionString(\"cone");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Change to connected"), "the declared name near the one no resource has, offered");
            Assert.Equal(("Change to connected", "a resource the AppHost declares"), (window.Assist.Offered[0], window.Assist.OfferedDetails[0]));
            Ui.Press(window, "ENTER");
            Assert.Equal(service.Replace("(\"conected\")", "(\"connected\")", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
            Ui.WaitFor(() => Unknown(window).SetEquals(["connectd"]), "the warning to go once the name is a resource's");
            Ui.Run(window, EditorActions.Undo);
            Assert.Equal(service, editor.Document.Snapshot.ToString());

            Passenger.App.Mcp.PassengerTools agent = new(window);
            int at = service.IndexOf("ConnectionStrings:conn", StringComparison.Ordinal) + "ConnectionStrings:conn".Length;
            int line = service[..at].Count(c => c == '\n') + 1;
            int column = at - service.LastIndexOf('\n', at - 1);
            IReadOnlyList<string> actions = Ui.Await(agent.CodeActions("Api/Program.cs", line, column), "the code actions an agent asks for");
            Assert.Equal("Change to connected [quickfix]", actions[0]);
            string applied = Ui.Await(agent.ApplyCodeAction("Api/Program.cs", line, column, "Change to connected"), "the agent's fix made");
            Assert.Equal($"Applied 'Change to connected' to {Path.Combine("Api", "Program.cs")}", applied);
            Assert.Equal(service.Replace("ConnectionStrings:connectd", "ConnectionStrings:connected", StringComparison.Ordinal), File.ReadAllText(project.PathOf("Api/Program.cs")));
        }
        finally
        {
            window.Close();
        }

        static HashSet<string> Unknown(MainWindow window) => [.. window.EditorArea.Active!.Markers
            .Where(m => m.Message.StartsWith("No resource of the AppHost is named '", StringComparison.Ordinal)).Select(m => m.Message.Split('\'')[1])];
    }

    [AvaloniaFact]
    public void A_direct_HttpClient_address_has_a_warning_but_a_stale_fix_does_not_overwrite_the_AppHost()
    {
        MainWindow window = project.Open(project.PathOf("Worker/Program.cs"));
        WaitForMissing(window, "worker", "api");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "https+http://ap");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Add WithReference(api) to worker"), "the service-discovery reference fix");
        string changed = File.ReadAllText(project.PathOf("Host/Program.cs")).Replace("builder.Build().Run();",
            "worker.WithReference(api);\nbuilder.Build().Run();\n// Changed after the fix was offered.\n", StringComparison.Ordinal);
        File.WriteAllText(project.PathOf("Host/Program.cs"), changed);
        Ui.WaitFor(() => !HasMissing(window), "the editor to observe the changed AppHost before accepting the stale offer");
        Choose(window, "Add WithReference(api) to worker");
        Ui.Settle();
        Assert.Equal(changed, File.ReadAllText(project.PathOf("Host/Program.cs")));
        Assert.Contains("changed", window.Status!, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_ambiguous_AppHost_or_unknown_graph_mutation_does_not_accuse_the_service(bool mutation)
    {
        if (mutation)
        {
            project.Rewrite("Host/Program.cs", Project.Host.Replace("builder.Build().Run();", "builder.Services.Clear();\nbuilder.Build().Run();", StringComparison.Ordinal));
        }
        else
        {
            project.Rewrite("OtherHost/Program.cs", Project.Host);
        }
        MainWindow window = project.Open(project.PathOf("Api/Program.cs"));
        Ui.Settle();
        Assert.False(HasMissing(window), "An unproven graph must not produce a missing-reference warning");
        Assert.DoesNotContain(window.EditorArea.Active!.Markers, m => m.Message.Contains("does not reference", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void Configuration_resources_share_the_semantic_graph_for_generic_and_reordered_named_constant_AddProject_calls()
    {
        project.Rewrite("Host/Program.cs", Project.Host.Replace("var worker = builder.AddProject<Projects.Worker>(\"worker\");", """
            const string WorkerName = "worker";
            const string WorkerProject = "../Worker/Worker.csproj";
            builder.AddProject(projectPath: WorkerProject, name: WorkerName).WithReference(api);
            """, StringComparison.Ordinal));
        MainWindow window = project.Open(project.PathOf("Host/Program.cs"));
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => PathNames.Same(configuration.Project, PathNames.Canonical(project.PathOf("Host/Host.csproj")))),
                "the semantic AppHost configuration");
            RunConfiguration host = window.Configurations.First(configuration => PathNames.Same(configuration.Project, PathNames.Canonical(project.PathOf("Host/Host.csproj"))));
            Assert.True(window.SelectConfiguration(host.Name));
            Ui.Run(window, AppActions.FindAction);
            window.KeyTextInput("Edit Configurations");
            Ui.Press(window, "ENTER");
            RunConfigurationsDialog dialog = Assert.IsType<RunConfigurationsDialog>(window.RunConfigurationsDialog);
            Ui.WaitFor(() => dialog.AspireTable.Rows.Count == 2, "generic and unassigned named-argument project resources");
            Assert.Equal(["api", "worker"], dialog.AspireTable.Rows.Select(row => row.Service).Order(StringComparer.Ordinal));
            Assert.Contains(dialog.AspireTable.Rows, row => Equals(row.Project.SelectedItem, "Api"));
            Assert.Contains(dialog.AspireTable.Rows, row => Equals(row.Project.SelectedItem, "Worker"));
            DialogButtonsTests.Check(dialog);
            Assert.True(dialog.AspireTable.Classes.Contains("framed"), "The Aspire resources are not the framed services table Compose's are");
            ComboBox worker = dialog.AspireTable.Rows.Single(row => row.Service == "worker").Action;
            Assert.Equal(["Run", "Debug", "Don't start"], Assert.IsType<string[]>(worker.ItemsSource));
            Assert.False(dialog.AspireTable.Rows.Single(row => row.Service == "worker").Project.IsEnabled, "A resource's project was offered as a choice, where its AppHost names it");
            Ui.Click(worker);
            Ui.Press(dialog, "END");
            Ui.Press(dialog, "ENTER");
            Ui.Click(dialog.OkButton);
            RunConfiguration kept = window.Configurations.First(configuration => configuration.Name == host.Name);
            Assert.Equal(ServiceStart.DontStart, kept.ServiceActions["worker"]);
            Assert.Equal(ServiceStart.Run, kept.ServiceActions["api"]);
            Assert.True(PathNames.Same(PathNames.Canonical(project.PathOf("Worker/Worker.csproj")), kept.ServiceProjects["worker"]),
                $"The worker's project was kept as {kept.ServiceProjects.GetValueOrDefault("worker")}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file no AppHost involves pays nothing for the AppHost in the folder: a key typed in it and its pause
    /// allocate on the UI thread under 8 bytes a line more with the open AppHost 5,000 lines longer; the AppHost's run
    /// icons stand through it, read again only as its own text changes; and the file's completion, context actions and
    /// declarations are the server's, Passenger's extension never asked.</summary>
    [AvaloniaFact]
    public void A_typing_pause_in_a_file_no_AppHost_involves_costs_nothing_of_Aspire()
    {
        const int Notes = 5000;
        MainWindow window = project.Open(project.PathOf("Host/Program.cs"));
        try
        {
            Passenger.App.Workspace.OpenDocument host = window.EditorArea.Active!;
            int[] declared = [.. host.Text.Snapshot.ToString().Split('\n').Select((text, line) => (text, line))
                .Where(l => l.text.Contains("AddProject", StringComparison.Ordinal)).Select(l => l.line)];
            Ui.WaitFor(() => host.RunMarks.Select(m => m.Line).SequenceEqual(declared), "the AppHost's run icons beside its AddProject lines");
            window.OpenFile(project.PathOf("Plain/Notes.cs"));
            Ui.Settle();
            IReadOnlyList<RunMark> marks = host.RunMarks;
            long few = UiBytesPerPause(window);
            Assert.True(ReferenceEquals(marks, host.RunMarks), "Keys in a file no AppHost involves made the AppHost's run icons again");

            window.OpenFile(project.PathOf("Host/Program.cs"));
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Selections = SelectionSet.Single(editor.Document.Snapshot.Length);
            Task copied = window.Clipboard!.SetTextAsync(string.Concat(Enumerable.Range(0, Notes).Select(i => $"\n// Note {i} of what the AppHost runs, which makes its text long.")));
            Ui.WaitFor(() => copied.IsCompleted, "the clipboard to take the notes");
            Ui.Press(window, Ui.Shortcut(EditorActions.Paste));
            Ui.WaitFor(() => host.Text.Snapshot.LineCount > Notes && !ReferenceEquals(marks, host.RunMarks) && host.RunMarks.Select(m => m.Line).SequenceEqual(declared),
                "the AppHost's run icons read again for its own text");
            window.OpenFile(project.PathOf("Plain/Notes.cs"));
            Ui.Settle();
            marks = host.RunMarks;
            long many = UiBytesPerPause(window);
            Assert.True(ReferenceEquals(marks, host.RunMarks), "Keys in a file no AppHost involves made the long AppHost's run icons again");
            long each = (many - few) / Notes;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"A typing pause in a plain file allocated {many:N0} bytes on the UI thread with the open AppHost {Notes:N0} lines longer, {few:N0} without: {each} bytes a line.");
            Assert.True(each < 8, $"A typing pause in a file no AppHost involves allocated {many / 1024:N0} KB on the UI thread with the open AppHost {Notes:N0} lines longer, {few / 1024:N0} KB without: {each} bytes for each of its lines, which the pause read");

            editor = window.EditorArea.CodeEditor;
            Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
            Task asked = server.RefuseNext(ExtensionMessage, 1);
            try
            {
                Ui.PlaceAfter(editor, "return ");
                Ui.Run(window, AppActions.Complete);
                Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("value"), "the server's completion in a file no AppHost involves");
                Ui.Press(window, "ESCAPE");
                Ui.PlaceAfter(editor, "Twi");
                Ui.Run(window, AppActions.QuickFix);
                Ui.WaitFor(() => window.Assist.Offered.Contains("Generate method 'Twice'"), "the server's fix in a file no AppHost involves");
                Ui.Press(window, "ESCAPE");
                Ui.PlaceAfter(editor, "Twice(val");
                Ui.Run(window, AppActions.GoToDeclaration);
                Ui.WaitFor(() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line).Contains("int value", StringComparison.Ordinal),
                    "Go to Declaration to reach the local in a file no AppHost involves");
                Assert.False(asked.IsCompleted, "Completion, Show Context Actions or Go to Declaration in a file no AppHost involves asked Passenger's extension for Aspire's help");
            }
            finally
            {
                _ = server.RefuseNext(ExtensionMessage, 0);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>How Passenger's extension is asked about a document.</summary>
    private const string ExtensionMessage = Passenger.Lsp.LspMethods.DispatchDocumentExtensionMessage;

    /// <summary>What a key typed in the plain file's comment and its pause allocate on the UI thread, the least of a few.</summary>
    private static long UiBytesPerPause(MainWindow window)
    {
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "// Typed here:");
        editor.Focus();
        Ui.Settle();
        long least = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput("a");
            Ui.Settle();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }

    private static bool HasMissing(MainWindow window) => window.CSharpService!.For(window.EditorArea.Active!)!.Diagnostics
        .Any(d => d.Source == "Passenger.Aspire");

    private static void WaitForMissing(MainWindow window, string service, string resource)
    {
        try
        {
            Ui.WaitFor(() => HasMissing(window), $"the missing {service} to {resource} reference warning");
        }
        catch (TimeoutException timeout)
        {
            Passenger.App.CSharp.CSharpDocument current = window.CSharpService!.For(window.EditorArea.Active!)!;
            string evidence;
            try
            {
                AspireHelp help = Ui.Await(current.Lsp!.ExtensionAsync<AspireHelp>(Handlers.AspireCodeHelp, new AspireHelpRequest(null, Explain: true)),
                    "semantic Aspire failure evidence", TimeSpan.FromSeconds(10));
                evidence = help.Evidence ?? "The semantic handler returned no evidence";
            }
            catch (Exception failed)
            {
                evidence = $"Semantic evidence request failed: {failed}";
            }
            string diagnostics = string.Join("\n", current.Diagnostics.Take(10).Select(d => $"{d.Source}: {d.Message}"));
            throw new TimeoutException($"{timeout.Message}\nStatus: {window.Status}\nDiagnostics:\n{diagnostics}\nAspire semantic evidence:\n{evidence}", timeout);
        }
        Assert.Contains(window.EditorArea.Active!.Markers, m => m.Kind == MarkerKind.Warning
            && m.Message.Contains($"'{service}' does not reference '{resource}'", StringComparison.Ordinal));
    }

    private static void Choose(MainWindow window, string label)
    {
        Ui.WaitFor(() => window.Assist.Offered.Contains(label), $"the native '{label}' action");
        int index = window.Assist.Offered.ToList().IndexOf(label);
        for (int i = 0; i < index; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
    }

    public sealed class Project : SharedProject
    {
        internal const string Host = """
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            var db = builder.AddConnectionString("db");
            var connected = builder.AddConnectionString("connected");
            var api = builder.AddProject<Projects.Api>("api").WithReference(connected);
            var worker = builder.AddProject<Projects.Worker>("worker");
            builder.Build().Run();
            """;

        /// <summary>The Host's UserSecretsId: its user secrets are kept under the run's own folder (TestRun.UserSecrets).</summary>
        internal const string SecretsId = "passenger-aspire-help-host";

        internal const string Worker = "var builder = WebApplication.CreateBuilder(args);\nvar client = new HttpClient();\nclient.BaseAddress = new Uri(\"https+http://api\");\nbuilder.Build().Run();\n";

        internal const string Service = "var builder = WebApplication.CreateBuilder(args);\nvar missing = builder.Configuration.GetConnectionString(\"db\");\nvar known = builder.Configuration.GetConnectionString(\"connected\");\nbuilder.Build().Run();\n";

        public Project() : base("aspire-help-")
        {
            WriteTo(Folder, SecretsId);
            // The AppHosts' graph is the starter's, in the package cache the test project's restore filled: never nuget.org.
            AspireApp.UseFeed(Folder);
            Prepare(byRealPath: true);
        }

        /// <summary>The solution's files written into a folder, the Host's user secrets kept under <paramref name="secretsId"/>:
        /// this class's and an agent's test of the same code help (McpLanguagesTests).</summary>
        internal static void WriteTo(string folder, string secretsId)
        {
            Put(folder, "Shop.slnx", "<Solution><Project Path=\"Api/Api.csproj\" /><Project Path=\"Worker/Worker.csproj\" /><Project Path=\"Host/Host.csproj\" /><Project Path=\"OtherHost/OtherHost.csproj\" /><Project Path=\"Plain/Plain.csproj\" /></Solution>");
            // A project no AppHost references, whose files Aspire's help leaves alone.
            Put(folder, "Plain/Plain.csproj", TestProjects.Library());
            Put(folder, "Plain/Notes.cs", "namespace Plain;\n\npublic static class Notes\n{\n    // Typed here:\n    public static int Count()\n    {\n        int value = 1;\n        return Twice(value);\n    }\n}\n");
            foreach (string name in new[] { "Api", "Worker" })
            {
                Put(folder, $"{name}/{name}.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
            }
            Put(folder, "Api/Program.cs", Service);
            Put(folder, "Worker/Program.cs", Worker);
            foreach (string name in new[] { "Host", "OtherHost" })
            {
                Put(folder, $"{name}/{name}.csproj", $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <Sdk Name="Aspire.AppHost.Sdk" Version="{AspireApp.TemplatesVersion}" />
                      <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings><IsAspireHost>true</IsAspireHost><AspireUseCliBundle>false</AspireUseCliBundle>{(name == "Host" ? $"<UserSecretsId>{secretsId}</UserSecretsId>" : "")}</PropertyGroup>
                      <ItemGroup><PackageReference Include="Aspire.Hosting.AppHost" Version="{AspireApp.TemplatesVersion}" /><ProjectReference Include="../Api/Api.csproj" /><ProjectReference Include="../Worker/Worker.csproj" /></ItemGroup>
                    </Project>
                    """);
            }
            Put(folder, "Host/Program.cs", Host);
            Put(folder, "OtherHost/Program.cs", "System.Console.WriteLine(\"No graph\");\n");
        }

        private static void Put(string folder, string name, string text) => TestFolders.Write(folder, name, text);
    }
}
