using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Endpoints;
using Passenger.App.State;

namespace Passenger.Tests;

/// <summary>The Endpoints window, an HTTP client: the requests of a folder's .http files listed with their methods,
/// Test sending one as written and holding its answer to the file's criteria, Interactive Test sending it as changed in
/// its dialog, and every test on a client of its own that is gone once the test is done.</summary>
public sealed class EndpointsTests : IDisposable
{
    private readonly StandInApi api = new();

    public void Dispose() => api.Dispose();

    [AvaloniaFact]
    public void The_window_lists_each_request_of_the_folders_http_files_with_its_method_and_name()
    {
        MainWindow window = Open(Folder());
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3, "the .http file's three requests");
        Assert.Equal(["GET", "POST", "GET"], window.Endpoints.Requests.Select(r => r.Method));
        Assert.Equal(["All items", "Add an item", "{{base}}/missing"], window.Endpoints.Requests.Select(r => r.Title));
        Assert.Equal("dev", window.Endpoints.Environment);
        window.Close();
    }

    [AvaloniaFact]
    public void Test_sends_the_request_as_written_and_holds_its_answer_to_the_files_criteria()
    {
        MainWindow window = Open(Folder());
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3, "the requests");
        EndpointRequest all = window.Endpoints.Requests[0];

        Ui.Click(RowButton(window, all, "endpointTest"));
        Ui.WaitFor(() => window.Endpoints.Response.Response is not null, "the answer");

        Assert.True(window.Endpoints.Response.Passed, $"The answer met none of its criteria: {window.Endpoints.Response.Summary}");
        Assert.StartsWith("200 OK", window.Endpoints.Response.Summary);
        Assert.Equal(3, window.Endpoints.Response.Results.Count);
        Assert.Contains("\"name\": \"Ada\"", window.Endpoints.Response.Shown);
        Assert.True(window.Endpoints.Outcome(all));
        StandInApi.Request received = Assert.Single(api.Received);
        Assert.Equal("GET /items", received.Line);
        Assert.Equal("application/json", received.Header("Accept"));

        Ui.Click(RowButton(window, window.Endpoints.Requests[2], "endpointTest"));
        Ui.WaitFor(() => window.Endpoints.Response.Response is not null, "the second answer");
        Assert.False(window.Endpoints.Response.Passed, "A 404 passed a test that expects an answer below 400");
        Assert.False(window.Endpoints.Outcome(window.Endpoints.Requests[2]));
        window.Close();
    }

    [AvaloniaFact]
    public void Every_test_is_sent_on_a_client_of_its_own_that_is_gone_when_the_test_is_done()
    {
        MainWindow window = Open(Folder());
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3, "the requests");
        for (int i = 0; i < 3; i++)
        {
            Ui.Click(RowButton(window, window.Endpoints.Requests[0], "endpointTest"));
            Ui.WaitFor(() => window.Endpoints.Response.Response is not null, "an answer");
            int test = i + 1;
            // Each client closes its connection as it is disposed, before the next test starts.
            Ui.WaitFor(() => api.Connections == test && api.Open == 0, $"test {test}'s connection, closed once it was done (connections {api.Connections}, open {api.Open})");
        }
        Assert.Equal(3, api.Received.Select(r => r.Connection).Distinct().Count());
        window.Close();
    }

    [AvaloniaFact]
    public void Interactive_test_sends_the_request_as_changed_in_its_dialog_and_checks_the_criteria_given_there()
    {
        MainWindow window = Open(Folder());
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3, "the requests");
        EndpointRequest add = window.Endpoints.Requests[1];
        Ui.Click(RowButton(window, add, "endpointInteractive"));
        InteractiveTestDialog dialog = DialogButtonsTests.Check(window.InteractiveTest ?? throw new Xunit.Sdk.XunitException("Interactive Test opened no dialog"));
        Ui.Settle();
        Assert.Equal("POST", dialog.Method.SelectedItem);
        Assert.Equal("status 201", dialog.Criteria.Text);

        Assert.Equal([(true, "Content-Type", "application/json")], dialog.Headers.Pairs);

        dialog.Method.SelectedItem = "PUT";
        KeyValueGrid.Row trace = dialog.Headers.Rows[^1];
        trace.Key.Text = "X-Trace";
        Ui.Settle();
        trace.Value.Text = "{{trace}}";
        Ui.Settle();
        Assert.Equal(3, dialog.Headers.Rows.Count);
        Assert.Equal("Headers (2)", dialog.TabTitles[1]);
        dialog.Body.Text = """{ "name": "Grace" }""";
        dialog.Criteria.Text = "status 2xx\nheader Content-Type contains json\njson $.name equals Grace\ntime < 5000";
        Ui.Click(dialog.Send);
        Ui.WaitFor(() => dialog.Response.Response is not null, "the dialog's answer");

        Assert.True(dialog.Response.Passed, $"The changed request's answer failed: {string.Join("; ", dialog.Response.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation} got {r.Actual}"))}");
        StandInApi.Request received = api.Received[^1];
        Assert.Equal("PUT /items", received.Line);
        Assert.Equal("t-42", received.Header("X-Trace"));
        Assert.Equal("""{ "name": "Grace" }""", received.Body);

        dialog.Criteria.Text = "status 200";
        Ui.Click(dialog.Send);
        Ui.WaitFor(() => dialog.Response.Response is not null, "the second answer");
        ExpectationResult failed = Assert.Single(dialog.Response.Results);
        Assert.False(failed.Passed);
        Assert.Equal("201", failed.Actual);
        Assert.Contains("POST {{base}}/items", File.ReadAllText(add.File));

        int sent = api.Received.Count;
        dialog.Criteria.Text = "colour blue";
        Ui.Click(dialog.Send);
        Assert.Contains("no criterion", dialog.Problem);
        Assert.Equal(sent, api.Received.Count);
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void The_urls_query_and_the_params_table_follow_each_other_and_a_param_turned_off_is_not_sent()
    {
        MainWindow window = Open(Folder());
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3, "the requests");
        Ui.Click(RowButton(window, window.Endpoints.Requests[0], "endpointInteractive"));
        InteractiveTestDialog dialog = window.InteractiveTest!;
        Ui.Settle();
        Assert.Equal(0, dialog.PageShown);
        Assert.Empty(dialog.Params.Pairs);

        KeyValueGrid.Row page = dialog.Params.Rows[^1];
        page.Key.Text = "page";
        Ui.Settle();
        page.Value.Text = "2";
        Ui.Settle();
        KeyValueGrid.Row query = dialog.Params.Rows[^1];
        query.Key.Text = "q";
        Ui.Settle();
        query.Value.Text = "{{trace}}";
        Ui.Settle();
        Assert.Equal("{{base}}/items?page=2&q={{trace}}", dialog.Url.Text);
        Assert.Equal("Params (2)", dialog.TabTitles[0]);

        dialog.Params.Rows[0].On.IsChecked = false;
        Ui.Settle();
        Assert.Equal("{{base}}/items?q={{trace}}", dialog.Url.Text);
        Assert.Equal([(false, "page", "2"), (true, "q", "{{trace}}")], dialog.Params.Pairs);

        dialog.Url.Text = "{{base}}/items?limit=5&q=x";
        Ui.Settle();
        Assert.Equal([(true, "limit", "5"), (true, "q", "x"), (false, "page", "2")], dialog.Params.Pairs);

        Ui.Click(dialog.Send);
        Ui.WaitFor(() => dialog.Response.Response is not null, "the answer");
        Assert.Equal("GET /items?limit=5&q=x", api.Received[^1].Line);
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void A_request_whose_variable_has_no_value_says_which_and_sends_nothing()
    {
        string folder = Folder();
        File.WriteAllText(Path.Combine(folder, "http-client.env.json"), """{ "prod": { "base": "https://example.invalid" } }""");
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3 && window.Endpoints.Environment == "prod", "the requests in prod");
        window.Endpoints.ChooseEnvironment("No environment");
        Ui.Settle();
        Ui.Click(RowButton(window, window.Endpoints.Requests[0], "endpointTest"));
        Assert.Contains("{{base}}", window.Endpoints.Response.Summary);
        Assert.Empty(api.Received);
        window.Close();
    }

    [AvaloniaFact]
    public void An_http_file_written_on_disk_is_listed_again()
    {
        string folder = Folder();
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3, "the requests");
        File.WriteAllText(Path.Combine(folder, "more.rest"), "DELETE {{base}}/items/1\n");
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 4, "the new file's request");
        Assert.Contains(window.Endpoints.Requests, r => r.Method == "DELETE");

        window.DockHost.Hide(ToolWindowIds.Endpoints);
        Ui.Settle();
        File.WriteAllText(Path.Combine(folder, "later.http"), "PATCH {{base}}/items/1\n");
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 5, "the request of the file written while the window was closed");
        window.Close();
    }

    [AvaloniaFact]
    public void An_answer_longer_than_passenger_reads_is_cut_there_and_says_so()
    {
        string folder = Folder();
        File.WriteAllText(Path.Combine(folder, "huge.http"), "GET {{base}}/huge\n");
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 4, "the requests");
        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Url.EndsWith("/huge", StringComparison.Ordinal)), "endpointTest"));
        Ui.WaitFor(() => window.Endpoints.Response.Response is not null, "the huge answer");

        EndpointResponse answer = window.Endpoints.Response.Response!;
        Assert.True(answer.Error is null, $"The huge answer failed: {answer.Error}");
        Assert.True(answer.Cut, "An answer longer than Passenger reads was read whole");
        Assert.Equal(EndpointClient.MostRead, answer.Size);
        Assert.Contains("the rest unread", window.Endpoints.Response.Summary);
        Assert.EndsWith("more characters not shown", window.Endpoints.Response.Shown);
        window.Close();
    }

    [AvaloniaFact]
    public void A_request_whose_variables_name_each_other_over_and_over_sends_nothing_and_says_why()
    {
        string folder = Folder();
        // Each variable names the next ten times, ten deep: ten billion references filled in, were nothing to stop it.
        StringBuilder file = new();
        for (int i = 0; i < 10; i++)
        {
            string next = "{{v" + (i + 1).ToString(CultureInfo.InvariantCulture) + "}}";
            file.Append(CultureInfo.InvariantCulture, $"@v{i} = {string.Concat(Enumerable.Repeat(next, 10))}\n");
        }
        file.Append("@v10 = x\n\nGET {{base}}/items?q={{v0}}\n");
        File.WriteAllText(Path.Combine(folder, "laughs.http"), file.ToString());
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 4, "the requests");

        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.File.EndsWith("laughs.http", StringComparison.Ordinal)), "endpointTest"));
        Assert.Contains("over and over", window.Endpoints.Response.Summary);
        Assert.Empty(api.Received);
        window.Close();
    }

    [AvaloniaFact]
    public void A_request_that_reads_the_persons_environment_asks_first_naming_the_host_and_each_variable_and_sends_nothing_unless_allowed()
    {
        MainWindow window = Open(SecretsFolder());
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 4, "the requests");
        EndpointRequest whoami = window.Endpoints.Requests.Single(r => r.Name == "Who am I");
        string set = System.Environment.GetEnvironmentVariable(StateStore.Variable)!;

        // The env file's token hides a $processEnv the .http file never shows; the question names it, and one not set.
        Ui.Click(RowButton(window, whoami, "endpointTest"));
        EndpointQuestionDialog question = DialogButtonsTests.Check(window.Endpoints.Question ?? throw new Xunit.Sdk.XunitException("A request reading the person's environment was sent unasked"));
        Assert.Contains($"127.0.0.1:{api.Port}", question.Title);
        Assert.Equal([new EndpointRead(false, "PASSENGER_ENDPOINTS_UNSET", Missing: true), new EndpointRead(false, StateStore.Variable)], question.Reads);
        Assert.Contains(question.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("PASSENGER_ENDPOINTS_UNSET, which is not set", StringComparison.Ordinal) == true);
        Ui.Click(question.DontAllowButton);
        Assert.Null(window.Endpoints.Question);
        Assert.Empty(api.Received);
        Assert.StartsWith("Not sent", window.Endpoints.Response.Summary, StringComparison.Ordinal);
        Assert.Null(window.Endpoints.Outcome(whoami));

        Ui.Click(RowButton(window, whoami, "endpointTest"));
        question = window.Endpoints.Question ?? throw new Xunit.Sdk.XunitException("A Don't allow was kept, where the next send should ask again");
        Ui.Click(question.AllowButton);
        StandInApi.Request received = Assert.Single(api.Received);
        Assert.Equal(set, received.Header("X-Token"));
        Assert.Equal(string.Empty, received.Header("X-Missing"));

        Ui.Click(RowButton(window, whoami, "endpointTest"));
        Assert.Null(window.Endpoints.Question);
        Assert.Equal(2, api.Received.Count);

        Ui.Click(RowButton(window, window.Endpoints.Requests[0], "endpointTest"));
        Assert.Null(window.Endpoints.Question);
        Assert.Equal(3, api.Received.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Always_for_this_folder_is_kept_with_the_folder_so_the_next_window_sends_unasked_until_the_request_reads_more()
    {
        string folder = SecretsFolder();
        string settings = TestFolders.Create("endpoints-settings-");
        MainWindow window = Open(folder, settings);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 4, "the requests");
        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Who am I"), "endpointTest"));
        EndpointQuestionDialog question = window.Endpoints.Question ?? throw new Xunit.Sdk.XunitException("A request reading the person's environment was sent unasked");
        question.Always.IsChecked = true;
        Ui.Click(question.AllowButton);
        Assert.Single(api.Received);
        window.Close();
        Ui.Settle();
        string kept = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(settings, "workspaces"), "endpoints.json", SearchOption.AllDirectories)));
        Assert.Contains($"127.0.0.1:{api.Port} env PASSENGER_SETTINGS", kept);

        MainWindow again = Open(folder, settings);
        again.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => again.Endpoints.Requests.Count == 4, "the requests in the next window");
        Ui.Click(RowButton(again, again.Endpoints.Requests.Single(r => r.Name == "Who am I"), "endpointTest"));
        Assert.True(again.Endpoints.Question is null, "Always for this folder was not kept: the next window asked again");
        Assert.Equal(2, api.Received.Count);

        // The env files are read as a request is sent.
        File.WriteAllText(Path.Combine(folder, "http-client.env.json"),
            $$$"""{ "dev": { "base": "{{{api.Root}}}", "token": "{{$processEnv PASSENGER_SETTINGS}}{{$env.PASSENGER_ENDPOINTS_OTHER}}" } }""");
        Ui.Click(RowButton(again, again.Endpoints.Requests.Single(r => r.Name == "Who am I"), "endpointTest"));
        question = again.Endpoints.Question ?? throw new Xunit.Sdk.XunitException("A variable the folder never allowed was sent unasked");
        Assert.Contains(question.Reads, r => r.Name == "PASSENGER_ENDPOINTS_OTHER");
        Ui.Click(question.DontAllowButton);
        Assert.Equal(2, api.Received.Count);
        again.Close();
    }

    [AvaloniaFact]
    public void A_body_from_a_file_outside_the_folder_asks_first_and_one_inside_does_not()
    {
        string folder = Folder();
        string outside = Path.Combine(TestFolders.Create("endpoints-outside-"), "secret.json");
        File.WriteAllText(outside, """{ "secret": true }""");
        File.WriteAllText(Path.Combine(folder, "body.json"), """{ "name": "Inside" }""");
        bool links = !OperatingSystem.IsWindows();
        if (links)
        {
            File.CreateSymbolicLink(Path.Combine(folder, "linked.json"), outside);
        }
        File.WriteAllText(Path.Combine(folder, "bodies.http"), $"""
            ### Inside
            POST {"{{base}}"}/items
            Content-Type: application/json

            < ./body.json

            ### Outside
            POST {"{{base}}"}/items
            Content-Type: application/json

            < {outside}

            ### Linked
            POST {"{{base}}"}/items
            Content-Type: application/json

            < ./linked.json
            """);
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 6, "the requests");

        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Inside"), "endpointTest"));
        Assert.Null(window.Endpoints.Question);
        Assert.Equal("""{ "name": "Inside" }""", Assert.Single(api.Received).Body);

        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Outside"), "endpointTest"));
        EndpointQuestionDialog question = window.Endpoints.Question ?? throw new Xunit.Sdk.XunitException("A body from outside the folder was sent unasked");
        Assert.Equal([new EndpointRead(true, Passenger.Core.PathNames.Canonical(outside))], question.Reads);
        Ui.Click(question.DontAllowButton);
        Assert.Single(api.Received);

        if (links)
        {
            Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Linked"), "endpointTest"));
            question = window.Endpoints.Question ?? throw new Xunit.Sdk.XunitException("A link to a file outside the folder was sent unasked");
            Assert.Equal([new EndpointRead(true, Passenger.Core.PathNames.Canonical(outside))], question.Reads);
            Ui.Click(question.AllowButton);
            Assert.Equal("""{ "secret": true }""", api.Received[^1].Body);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void A_test_is_the_windows_work_until_its_answer_and_Stop_ends_it()
    {
        string folder = Folder();
        File.AppendAllText(Path.Combine(folder, "api.http"), "\n\n### Slow\nGET {{base}}/slow\n");
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 4, "the requests");
        Button slow = RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Slow"), "endpointTest");
        Button other = RowButton(window, window.Endpoints.Requests[0], "endpointTest");
        WorkingRing ring = window.FindControl<WorkingRing>("StatusWorking")!;

        Ui.Click(slow, settle: false);
        Ui.PumpUntil(() => window.Status?.StartsWith($"Sending GET {api.Root}/slow", StringComparison.Ordinal) == true, "the status bar to say the test's stage");
        Assert.True(ring.IsVisible, "The status bar should turn its ring while a test waits for its answer");
        Ui.Click(other, settle: false);
        Ui.PumpUntil(() => window.Status?.StartsWith("Already under way: ", StringComparison.Ordinal) == true, "a second test to be refused while one is under way");

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => window.Endpoints.Busy is null, "Stop to end the test");
        Assert.Equal("Stopped before an answer came", window.Endpoints.Response.Summary);
        Assert.False(ring.IsVisible, "The status bar's ring should stop once the test is stopped");
        Assert.Null(window.Endpoints.Outcome(window.Endpoints.Requests.Single(r => r.Name == "Slow")));
        Assert.Equal(["GET /slow"], api.Received.Select(r => r.Line));
        window.Close();
    }

    [AvaloniaFact]
    public void A_redirect_to_another_host_goes_without_the_requests_headers_and_says_so()
    {
        string folder = Folder();
        File.AppendAllText(Path.Combine(folder, "api.http"), "\n\n### Moved\nGET {{base}}/moved\nX-Trace: {{trace}}\n\n### Elsewhere\nGET {{base}}/elsewhere\nX-Trace: {{trace}}\n");
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 5, "the requests");

        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Moved"), "endpointTest"));
        Assert.Equal(["GET /moved", "GET /items"], api.Received.Select(r => r.Line));
        Assert.Equal("t-42", api.Received[^1].Header("X-Trace"));
        Assert.Empty(window.Endpoints.Response.Response!.Notes);

        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Elsewhere"), "endpointTest"));
        Assert.Equal(["GET /moved", "GET /items", "GET /elsewhere", "GET /items"], api.Received.Select(r => r.Line));
        Assert.StartsWith("localhost:", api.Received[^1].Header("Host"), StringComparison.Ordinal);
        Assert.Null(api.Received[^1].Header("X-Trace"));
        Assert.Equal(200, window.Endpoints.Response.Response!.Status);
        Assert.Equal([$"Redirected to http://localhost:{api.Port}, another origin, without the request's headers"], window.Endpoints.Response.Response.Notes);
        window.Close();
    }

    /// <summary>Another port of the same host is another server, which what a request reads was never allowed to: a redirect
    /// there goes without the request's headers, the person's environment in them, and one that would carry the body there
    /// is not followed.</summary>
    [AvaloniaFact]
    public void A_redirect_to_another_port_of_the_same_host_takes_neither_the_requests_headers_nor_its_body_there()
    {
        using StandInApi other = new();
        api.RedirectRoot = other.Root;
        string folder = Folder();
        File.AppendAllText(Path.Combine(folder, "api.http"), "\n\n### Other port\nGET {{base}}/other-port\nX-Secret: {{$env.PASSENGER_SETTINGS}}\n"
            + "\n### Posted to another port\nPOST {{base}}/other-port\nContent-Type: application/json\n\n{\"secret\":\"posted\"}\n");
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 5, "the requests");

        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Other port"), "endpointTest"));
        EndpointQuestionDialog question = window.Endpoints.Question ?? throw new Xunit.Sdk.XunitException("A request reading the person's environment was sent unasked");
        Ui.Click(question.AllowButton);
        Assert.Equal(System.Environment.GetEnvironmentVariable(StateStore.Variable), Assert.Single(api.Received).Header("X-Secret"));
        StandInApi.Request redirected = Assert.Single(other.Received);
        Assert.Equal("GET /items", redirected.Line);
        Assert.True(redirected.Header("X-Secret") is null, "What the request read was allowed to one port of the host and sent to another");
        Assert.Equal(200, window.Endpoints.Response.Response!.Status);
        Assert.Equal([$"Redirected to {other.Root}, another origin, without the request's headers"], window.Endpoints.Response.Response.Notes);

        Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Posted to another port"), "endpointTest"));
        Assert.Equal("POST /other-port", api.Received[^1].Line);
        Assert.Contains("posted", api.Received[^1].Body, StringComparison.Ordinal);
        Assert.True(other.Received.Count == 1, "A 307 carried the request's body to another port of the host");
        Assert.Equal(307, window.Endpoints.Response.Response!.Status);
        Assert.Equal([$"Not followed to {other.Root}: a 307 would send the request's body there"], window.Endpoints.Response.Response.Notes);
        window.Close();
    }

    [AvaloniaFact]
    public void A_redirect_to_another_port_does_not_forward_an_allowed_environment_header()
    {
        using StandInApi other = new();
        api.RedirectRoot = other.Root;
        string folder = Folder();
        File.AppendAllText(Path.Combine(folder, "api.http"), "\n\n### Other port\nGET {{base}}/other-port\nX-Secret: {{$env.PASSENGER_SETTINGS}}\n");
        MainWindow window = Open(folder);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Endpoints);
            Ui.WaitFor(() => window.Endpoints.Requests.Any(r => r.Name == "Other port"), "the redirect request");
            Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Other port"), "endpointTest"));
            EndpointQuestionDialog question = Assert.IsType<EndpointQuestionDialog>(window.Endpoints.Question);
            Ui.Click(question.AllowButton);
            Assert.Equal(Environment.GetEnvironmentVariable(StateStore.Variable), Assert.Single(api.Received).Header("X-Secret"));
            Assert.Null(Assert.Single(other.Received).Header("X-Secret"));
            Assert.Equal(200, window.Endpoints.Response.Response!.Status);
            Assert.Equal([$"Redirected to {other.Root}, another origin, without the request's headers"], window.Endpoints.Response.Response.Notes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_307_redirect_to_another_port_does_not_send_the_requests_body_there()
    {
        using StandInApi other = new();
        api.RedirectRoot = other.Root;
        string folder = Folder();
        File.AppendAllText(Path.Combine(folder, "api.http"), "\n\n### Other port\nPOST {{base}}/other-port\nContent-Type: application/json\n\n{\"secret\":\"synthetic\"}\n");
        MainWindow window = Open(folder);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Endpoints);
            Ui.WaitFor(() => window.Endpoints.Requests.Any(r => r.Name == "Other port"), "the redirect request");
            Ui.Click(RowButton(window, window.Endpoints.Requests.Single(r => r.Name == "Other port"), "endpointTest"));
            Assert.Contains("synthetic", Assert.Single(api.Received).Body, StringComparison.Ordinal);
            Assert.Empty(other.Received);
            Assert.Equal(307, window.Endpoints.Response.Response!.Status);
            Assert.Equal([$"Not followed to {other.Root}: a 307 would send the request's body there"], window.Endpoints.Response.Response.Notes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_requests_last_outcome_stays_with_it_as_lines_move()
    {
        string folder = Folder();
        MainWindow window = Open(folder);
        window.ShowToolWindow(ToolWindowIds.Endpoints);
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3, "the requests");
        Ui.Click(RowButton(window, window.Endpoints.Requests[0], "endpointTest"));
        Ui.Click(RowButton(window, window.Endpoints.Requests[2], "endpointTest"));
        int line = window.Endpoints.Requests[0].Line;

        File.WriteAllText(Path.Combine(folder, "api.http"), "# Read first.\n\n" + File.ReadAllText(Path.Combine(folder, "api.http")));
        Ui.WaitFor(() => window.Endpoints.Requests.Count == 3 && window.Endpoints.Requests[0].Line == line + 2, "the requests two lines down");
        Assert.True(window.Endpoints.Outcome(window.Endpoints.Requests[0]), "A named request's outcome was lost as its lines moved");
        Assert.False(window.Endpoints.Outcome(window.Endpoints.Requests[2]), "An unnamed request's outcome was lost as its lines moved");
        Assert.Null(window.Endpoints.Outcome(window.Endpoints.Requests[1]));
        window.Close();
    }

    /// <summary>The folder with a request whose env file's token is the person's PASSENGER_SETTINGS, which the .http file
    /// never names, and a variable the run never sets.</summary>
    private string SecretsFolder()
    {
        string folder = Folder();
        File.WriteAllText(Path.Combine(folder, "secrets.http"), """
            ### Who am I
            GET {{base}}/items
            X-Token: {{token}}
            X-Missing: {{$env.PASSENGER_ENDPOINTS_UNSET}}
            """);
        File.WriteAllText(Path.Combine(folder, "http-client.env.json"),
            $$$"""{ "dev": { "base": "{{{api.Root}}}", "token": "{{$processEnv PASSENGER_SETTINGS}}" } }""");
        return folder;
    }

    private string Folder()
    {
        string folder = TestFolders.Create("endpoints");
        File.WriteAllText(Path.Combine(folder, "api.http"), """
            @trace = t-42

            ### All items
            # @expect status 200
            # @expect header Content-Type contains json
            # @expect json $.items[0].name equals Ada
            GET {{base}}/items
            Accept: application/json

            ###
            # @name Add an item
            # @expect status 201
            POST {{base}}/items HTTP/1.1
            Content-Type: application/json

            { "name": "Ada" }

            > {%
                client.test("made", () => {});
            %}

            ###
            {{base}}/missing
            """);
        File.WriteAllText(Path.Combine(folder, "http-client.env.json"), $$"""{ "dev": { "base": "{{api.Root}}" } }""");
        return folder;
    }

    private static MainWindow Open(string folder, string? settings = null)
    {
        MainWindow window = settings is null ? new() { Width = 1200, Height = 800 } : new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        if (settings is not null)
        {
            window.KeepLayout();
        }
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private static Button RowButton(MainWindow window, EndpointRequest request, string name)
    {
        Button? found = null;
        Ui.WaitFor(() => (found = window.Endpoints.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == name && Ancestors(b).Any(a => ReferenceEquals(a.DataContext, request)))) is not null, $"{request.Title}'s {name} button");
        return found!;

        static IEnumerable<Visual> Ancestors(Visual visual) => visual.GetVisualAncestors();
    }

    /// <summary>A small JSON API on a loopback port: GET /items lists one item, POST and PUT /items answer 201 with the
    /// body sent, GET /moved redirects to /items and GET /elsewhere to it under the name localhost, GET and POST
    /// /other-port to <see cref="RedirectRoot"/>'s /items (a 302 and a 307), GET /slow answers only as the test ends,
    /// anything else 404. It keeps every request with the connection it came on, and counts connections made and still
    /// open, so a test sees whether a client was kept.</summary>
    private sealed class StandInApi : IDisposable
    {
        public sealed record Request(int Connection, string Line, IReadOnlyDictionary<string, string> Headers, string Body)
        {
            public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
        }

        private readonly StandInHttp http;
        private readonly List<Request> received = [];
        private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StandInApi() => http = new StandInHttp(AnswerAsync, keepAlive: true);

        /// <summary>Its port, which GET /elsewhere sends the client to under another host's name, localhost.</summary>
        public int Port => http.Port;

        public string Root => http.Root;

        /// <summary>Where GET and POST /other-port send the client: another stand-in's root, at another port of this host.</summary>
        public string? RedirectRoot { get; set; }

        public int Connections => http.Connections;

        public int Open => http.Open;

        public IReadOnlyList<Request> Received
        {
            get
            {
                lock (received)
                {
                    return [.. received];
                }
            }
        }

        public void Dispose()
        {
            held.TrySetResult();
            http.Dispose();
        }

        private async Task AnswerAsync(StandInHttp.Exchange exchange)
        {
            Request request = new(exchange.Connection, exchange.Line, exchange.Headers, Encoding.UTF8.GetString(exchange.Body));
            lock (received)
            {
                received.Add(request);
            }
            if (request.Line == "GET /slow")
            {
                // Held until the test is done with it, as a server that never answers.
                await held.Task.WaitAsync(exchange.Ending);
            }
            string? location = request.Line switch
            {
                "GET /moved" => "/items",
                "GET /elsewhere" => $"http://localhost:{Port}/items",
                "GET /other-port" or "POST /other-port" => $"{RedirectRoot}/items",
                _ => null,
            };
            (int status, string body) = Answer(request);
            await exchange.ReplyAsync(status, "application/json; charset=utf-8", body, location is null ? string.Empty : $"Location: {location}\r\n");
        }

        private static (int, string) Answer(Request request) => request.Line switch
        {
            "GET /items" => (200, """{"items":[{"id":1,"name":"Ada"}]}"""),
            "GET /moved" or "GET /elsewhere" or "GET /other-port" => (302, string.Empty),
            "POST /other-port" => (307, string.Empty),
            "GET /slow" => (200, "{}"),
            "GET /huge" => (200, string.Concat(Enumerable.Repeat(new string('a', 99) + "\n", (EndpointClient.MostRead / 100) + 10_000))),
            "POST /items" or "PUT /items" => (201, request.Body),
            _ => (404, """{"error":"not found"}"""),
        };
    }
}
