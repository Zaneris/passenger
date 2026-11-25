using Avalonia.Media.Imaging;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Languages.WinForms;
using Passenger.Text;

namespace Passenger.App.WinForms;

/// <summary>One form's or user control's design view: its designer code read as it stands (open or on disk) and drawn by
/// Windows Forms' designer (<see cref="FormDesignHost"/>) from its project's last build, followed as its files change; what
/// the pane shows, whatever draws it: the last frame, each component's box in it, what the designer could not make of a
/// statement (at its line), or why nothing is drawn. The designer runs only on Windows, where Windows Forms draws, and only
/// for a trusted folder, as it runs the project's code; it rests while the design is not shown. A UI-thread object.</summary>
public sealed class FormDesign : IDisposable
{
    /// <summary>The namespaces a Windows Forms project's code reads names in when it says none of its own: the SDK's implicit
    /// usings for one.</summary>
    private static readonly string[] ImplicitUsings =
        ["System", "System.Collections.Generic", "System.Drawing", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks", "System.Windows.Forms"];

    private readonly Func<string, TextSnapshot?> texts;
    private readonly Func<FormFiles, string?> built;
    private readonly Func<FormFiles, IReadOnlyList<string>> usings;
    private FormDesignHost? host;
    private int sequence;
    private int starts;
    private bool resting;
    private bool disposed;

    /// <summary>What each request sent read, by its sequence, so an answer's notes are placed at their lines.</summary>
    private readonly Dictionary<int, (TextSnapshot Text, DesignerSource Source)> sent = [];

    /// <param name="texts">A file's text as the editor has it, else as on disk; null for one not there.</param>
    /// <param name="built">The project's assembly from its last build, null for none; read off the window's thread.</param>
    /// <param name="usings">The namespaces the project's global usings bring, none where it has none written yet; read off
    /// the window's thread.</param>
    public FormDesign(FormFiles files, Func<string, TextSnapshot?> texts, Func<FormFiles, string?> built, Func<FormFiles, IReadOnlyList<string>> usings)
    {
        Files = files;
        this.texts = texts;
        this.built = built;
        this.usings = usings;
    }

    public FormFiles Files { get; }

    /// <summary>The frame, its status, its error, its components or its notes changed; raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The last frame drawn: its pixels, one a unit of the code.</summary>
    public Bitmap? Frame { get; private set; }

    /// <summary>Each component of the last frame, parents before what they hold, those in the tray last.</summary>
    public IReadOnlyList<DesignedBox> Components { get; private set; } = [];

    /// <summary>What the design is doing, or why it shows nothing.</summary>
    public string Status { get; private set; } = "Starting the designer…";

    /// <summary>What the designer could not make of the code, each at its line ("line 12: …"); none where it made all of it.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>Whether a designer runs for the form, rather than the pane saying why none does.</summary>
    public bool Running => host is not null;

    /// <summary>The designer started on the project's newest build, once the folder is trusted, on Windows; or says why not.</summary>
    public async Task StartAsync()
    {
        UiThread.Check();
        int start = ++starts;
        if (!OperatingSystem.IsWindows())
        {
            Say(Refusals.FormsDrawOnWindows);
            return;
        }
        if (!WorkspaceTrust.Trusts(Files.Designer))
        {
            Say(Refusals.Untrusted);
            return;
        }
        FormFiles files = Files;
        string? assembly = await BackgroundWork.Run(() => built(files));
        if (disposed || resting || start != starts)
        {
            return;
        }
        FormDesignHost? started = await FormDesignHost.StartAsync(assembly, Say, Answered, Ended);
        if (disposed || resting || start != starts)
        {
            started?.Dispose();
            return;
        }
        host = started;
        IdeErrors.Observe(UpdateAsync());
    }

    /// <summary>The designer code as it stands, read off the window's thread and sent to the designer to draw.</summary>
    public async Task UpdateAsync()
    {
        UiThread.Check();
        if (disposed || resting || host is null)
        {
            return;
        }
        if (texts(Files.Designer) is not { } designer || texts(Files.Code) is not { } code)
        {
            Say($"{Path.GetFileName(Files.Designer)} or {Path.GetFileName(Files.Code)} can no longer be read");
            return;
        }
        int asked = ++sequence;
        FormFiles files = Files;
        FormDesignRequest? request = await BackgroundWork.Run(() =>
        {
            IReadOnlyList<string> global = usings(files);
            if (DesignerCode.Read(designer.ToString(), files.Class) is not { } source)
            {
                return null;
            }
            (string Base, IReadOnlyList<string> Usings)? based = DesignerCode.BaseOf(code.ToString(), files.Class) ?? DesignerCode.BaseOf(designer.ToString(), files.Class);
            lock (sent)
            {
                sent[asked] = (designer, source);
            }
            return new FormDesignRequest
            {
                Sequence = asked,
                Class = files.Class,
                Base = based?.Base,
                Usings = [.. source.Usings, .. based?.Usings ?? [], .. global.Count > 0 ? global : ImplicitUsings],
                Fields = source.Fields,
                Statements = source.Statements,
            };
        });
        if (request is null)
        {
            Say($"{Path.GetFileName(Files.Designer)} no longer gives {Files.Class} an InitializeComponent");
            return;
        }
        host?.Send(request);
    }

    /// <summary>The designer started again on the project's newest build, the last frame shown until the new one comes.</summary>
    public Task RestartAsync()
    {
        UiThread.Check();
        if (disposed || resting)
        {
            return Task.CompletedTask;
        }
        Stop();
        return StartAsync();
    }

    /// <summary>Whether a build since the designer started changed the build it draws from (or gave it one where it had none).</summary>
    public bool Outdated(string? newest) =>
        host is not null && (host.Build is { } drawn ? newest is null || !PathNames.Same(drawn.Assembly, newest) || PreviewedBuild.Of(newest, configured: false).Written != drawn.Written : newest is not null);

    /// <summary>The design is no longer shown: its designer ends, the last frame kept for when it shows again.</summary>
    public void Rest()
    {
        UiThread.Check();
        if (disposed || resting)
        {
            return;
        }
        resting = true;
        starts++;
        Stop();
        Say("The designer rests while the design is hidden");
    }

    /// <summary>The design is shown again: a designer that rested starts on the project's newest build.</summary>
    public Task WakeAsync()
    {
        UiThread.Check();
        if (disposed || !resting)
        {
            return Task.CompletedTask;
        }
        resting = false;
        Say("Starting the designer…");
        return StartAsync();
    }

    private void Answered(FormDesignReply answer)
    {
        if (disposed)
        {
            return;
        }
        (TextSnapshot Text, DesignerSource Source)? read;
        lock (sent)
        {
            read = sent.TryGetValue(answer.Sequence, out (TextSnapshot, DesignerSource) found) ? found : null;
            foreach (int answered in sent.Keys.Where(k => k <= answer.Sequence).ToList())
            {
                sent.Remove(answered);
            }
        }
        switch (answer.Kind)
        {
            case PreviewKinds.Frame when answer.Png is { } png:
                using (MemoryStream bytes = new(png))
                {
                    Frame = new Bitmap(bytes);
                }
                Components = answer.Components ?? [];
                Notes = [.. (answer.Notes ?? []).Select(n => Placed(n, read))];
                Status = Notes.Count == 0 ? "Live" : $"Live. {Notes.Count} statement{(Notes.Count == 1 ? string.Empty : "s")} not drawn";
                break;
            case PreviewKinds.Error:
                Notes = [answer.Message ?? "The form did not load"];
                Status = "The form did not load";
                break;
            default:
                Frame = null;
                Components = [];
                Notes = [];
                Status = answer.Message ?? "Nothing to draw";
                break;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A note as the pane says it: at its statement's line in the designer code as it was sent.</summary>
    private static string Placed(DesignNote note, (TextSnapshot Text, DesignerSource Source)? read) =>
        note.Statement is int index && read is { } sent && index < sent.Source.Statements.Count
            ? $"Line {sent.Text.GetPosition(sent.Source.Statements[index].At).Line + 1}: {note.Message}"
            : note.Message;

    private void Ended(string words)
    {
        if (disposed)
        {
            return;
        }
        Stop();
        Say(words);
    }

    /// <summary>Says what the design is doing, or why it shows nothing.</summary>
    private void Say(string status)
    {
        if (disposed)
        {
            return;
        }
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Stop()
    {
        host?.Dispose();
        host = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Stop();
    }
}
