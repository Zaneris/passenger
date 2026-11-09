using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A folder holding two Godot projects of GDScript alone, client/ and server/, each with its main.tscn, and a Godot
/// editor on each connected through Passenger's addon, stood in for here as its wire speaks (no Godot needed): the window
/// takes both, and what each holds is read against its own project's files alone.</summary>
public sealed class GodotEditorProjectsTests
{
    private const string Scene = "[gd_scene format=3]\n\n[node name=\"Main\" type=\"Node\"]\n";

    [AvaloniaFact]
    public void A_scene_unsaved_in_one_projects_editor_holds_its_writes_and_not_the_other_projects_scene_of_its_name()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-two-editors-"));
        string client = Project(folder, "client");
        string server = Project(folder, "server");
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = TestFolders.Create("godot-two-editors-state-"), Autosaves = false };
        window.Show();
        window.Confirm = (_, _, _) => Task.FromResult(false);
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            using StandIn clientEditor = Connect(client, 1);
            using StandIn serverEditor = Connect(server, 2);
            Ui.WaitFor(() => window.GodotEditorsConnected == 2, () => $"both projects' editors to be taken ({window.GodotEditorsConnected} are)");
            Ui.Await(Waiting.Apart(() => clientEditor.TellAsync("scenes", new JsonObject { ["open"] = new JsonArray("res://main.tscn"), ["unsaved"] = new JsonArray(), ["sure"] = true })),
                "the client's editor to say it has its main.tscn open and saved");
            Ui.Await(Waiting.Apart(() => serverEditor.TellAsync("scenes", new JsonObject { ["open"] = new JsonArray("res://main.tscn"), ["unsaved"] = new JsonArray("res://main.tscn"), ["sure"] = true })),
                "the server's editor to say it holds its main.tscn unsaved");
            string held = Path.Combine(server, "main.tscn");
            Ui.WaitFor(() => window.HeldByGodot(held)?.Contains("unsaved edits", StringComparison.Ordinal) == true,
                () => $"the server's main.tscn to be held unsaved ({window.HeldByGodot(held) ?? "nothing held"}): Passenger would write over what server/'s editor holds");

            Assert.True(window.HeldByGodot(Path.Combine(client, "main.tscn")) is null && window.GodotHasOpen(Path.Combine(client, "main.tscn")),
                $"client/'s main.tscn, open and saved in its own editor, is held as {window.HeldByGodot(Path.Combine(client, "main.tscn"))}: a scene of one project was read against the other's editor");
        }
        finally
        {
            window.Close();
        }

        static StandIn Connect(string project, int editor)
        {
            Ui.WaitFor(() => StandIn.ListingOf(project) is not null, () => $"the window to list itself for {Path.GetFileName(project)}'s editors");
            JsonElement listing = StandIn.ListingOf(project)!.Value;
            return Ui.Await(Waiting.Apart(() => StandIn.ConnectAsync(listing, project, editor)), $"{Path.GetFileName(project)}'s editor to connect");
        }
    }

    /// <summary>A Godot project of GDScript alone in a folder of the open one, its main.tscn beside its project.godot.</summary>
    private static string Project(string folder, string name)
    {
        string project = Path.Combine(folder, name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, GodotProject.FileName), $"config_version=5\n\n[application]\n\nconfig/name=\"{name}\"\nrun/main_scene=\"res://main.tscn\"\n");
        File.WriteAllText(Path.Combine(project, "main.tscn"), Scene);
        return project;
    }

    /// <summary>Passenger's addon in one Godot editor as the window hears it: connected by the listing naming its project,
    /// its secret and hello said, then what it holds told.</summary>
    private sealed class StandIn : IDisposable
    {
        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;

        private StandIn(TcpClient client)
        {
            this.client = client;
            NetworkStream stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        }

        /// <summary>The listing this process wrote naming a project among its folders; null before there is one.</summary>
        public static JsonElement? ListingOf(string project)
        {
            string listings = Path.Combine(StateStore.DefaultRoot, "godot-editors");
            if (!Directory.Exists(listings))
            {
                return null;
            }
            foreach (string file in Directory.EnumerateFiles(listings, "*.json"))
            {
                using JsonDocument read = JsonDocument.Parse(File.ReadAllText(file));
                if (JsonMembers.Number(read.RootElement, "process") == Environment.ProcessId
                    && JsonMembers.Texts(read.RootElement, "folders").Any(f => PathNames.Same(f, project)))
                {
                    return read.RootElement.Clone();
                }
            }
            return null;
        }

        public static async Task<StandIn> ConnectAsync(JsonElement listing, string project, int editor)
        {
            TcpClient connected = new();
            await connected.ConnectAsync(System.Net.IPAddress.Loopback, JsonMembers.Number(listing, "port") ?? 0);
            StandIn standIn = new(connected);
            await standIn.writer.WriteLineAsync(JsonMembers.Text(listing, "token"));
            string? reply = await standIn.reader.ReadLineAsync();
            Assert.Equal(JsonMembers.Text(listing, "reply"), reply);
            await standIn.writer.WriteLineAsync(new JsonObject
            {
                ["id"] = 1,
                ["method"] = "hello",
                ["params"] = new JsonObject { ["folder"] = project, ["editor"] = editor, ["godot"] = "4.7.2", ["addon"] = 99 },
            }.ToJsonString());
            using JsonDocument answer = JsonDocument.Parse(await standIn.reader.ReadLineAsync() ?? "{}");
            Assert.True(JsonMembers.Text(answer.RootElement, "error") is null, $"The window refused {Path.GetFileName(project)}'s editor: {JsonMembers.Text(answer.RootElement, "error")}");
            return standIn;
        }

        public Task TellAsync(string method, JsonObject parameters) => writer.WriteLineAsync(new JsonObject { ["method"] = method, ["params"] = parameters }.ToJsonString());

        public void Dispose()
        {
            reader.Dispose();
            writer.Dispose();
            client.Dispose();
        }
    }
}
