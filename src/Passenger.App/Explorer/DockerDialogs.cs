using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Explorer;

/// <summary>How Add > Docker Support gives a project an image: a Dockerfile beside it, or the .NET SDK's own container
/// support (dotnet publish /t:PublishContainer), set in its project file.</summary>
public enum DockerSupportKind
{
    Dockerfile,
    Sdk,
}

/// <summary>Add > Docker Support on a project: a Dockerfile (with a .dockerignore at the solution's folder, the build's
/// context), or the SDK's container support; closes with the choice, or null when cancelled.</summary>
public sealed class DockerSupportDialog : IdeWindow
{
    private readonly RadioButton dockerfile;
    private readonly RadioButton sdk;

    public DockerSupportDialog(string project, bool hasDockerfile)
    {
        ShownWords.Set(this, TitleProperty, ActionTitles.AddDockerSupport.TrimmedEnd('…'));
        Width = DialogWidths.Form;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dockerfile = new RadioButton
        {
            GroupName = "kind",
            IsChecked = !hasDockerfile,
            IsEnabled = !hasDockerfile,
            Content = Wrapped(hasDockerfile ? ExplorerWords.DockerfileHad(Words.Verbatim(project)) : ExplorerWords.DockerfileBeside),
        };
        sdk = new RadioButton
        {
            GroupName = "kind",
            IsChecked = hasDockerfile,
            Content = Wrapped(ExplorerWords.SdkContainer(Words.Verbatim(MSBuildNames.PublishContainer))),
        };
        foreach (RadioButton choice in new[] { dockerfile, sdk })
        {
            choice.Margin = new Thickness(0, 0, 0, Gaps.Normal);
            choice.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        TextBlock heading = new() { Classes = { StyleClasses.PaneTitle }, [ShownWords.TextProperty] = ExplorerWords.ImageBuilt(Words.Verbatim(project)), Margin = new Thickness(0, 0, 0, Gaps.Wide) };
        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Accept();
        StackPanel page = new() { [DialogFooter.InsetProperty] = true, Children = { heading, dockerfile, sdk } };
        Content = DialogFooter.Page(page, DialogFooter.OkCancel(ok, () => Close(null)));
    }

    /// <summary>The way chosen now.</summary>
    public DockerSupportKind Chosen
    {
        get => sdk.IsChecked == true ? DockerSupportKind.Sdk : DockerSupportKind.Dockerfile;
        set => (value == DockerSupportKind.Sdk ? sdk : dockerfile).IsChecked = true;
    }

    /// <summary>Closes as OK does, with the way chosen.</summary>
    public void Accept() => Close((DockerSupportKind?)Chosen);

    /// <summary>A choice's words, wrapped within the dialog where they need more than a line.</summary>
    private static TextBlock Wrapped(Words text) => new() { [ShownWords.TextProperty] = text, TextWrapping = TextWrapping.Wrap };
}

/// <summary>Add > Docker Compose on the solution: its projects that run, each checked, a project with no Dockerfile given
/// one by Docker Support as it is written; and the databases, caches and brokers their packages point to, each checked,
/// what the engine makes of one said under them (<see cref="Note"/>). Closes with true for OK, null when cancelled; what is
/// checked is read from <see cref="Projects"/> and <see cref="Services"/>.</summary>
public sealed class DockerComposeDialog : IdeWindow
{
    private readonly TextBlock note = MutedText.Wrapped(string.Empty);

    public DockerComposeDialog(string solution, IReadOnlyList<CheckChoice> projects, IReadOnlyList<CheckChoice> services)
    {
        Projects = projects;
        Services = services;
        ShownWords.Set(this, TitleProperty, ActionTitles.AddDockerCompose.TrimmedEnd('…'));
        Width = DialogWidths.Form;
        Height = 460;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Close((bool?)true);
        Grid page = new() { [DialogFooter.InsetProperty] = true, RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto") };
        page.Children.Add(Heading(ExplorerWords.ComposeServices(Words.Verbatim(solution)), 0));
        page.Children.Add(List(projects, 1));
        page.Children.Add(Heading(ExplorerWords.BesidePackages, 2));
        Control backing = services.Count == 0
            ? new EmptyState(ExplorerWords.NoBacking)
            : List(services, 3);
        Grid.SetRow(backing, 3);
        page.Children.Add(backing);
        note.Margin = new Thickness(0, Gaps.Normal, 0, 0);
        note.IsVisible = false;
        Grid.SetRow(note, 4);
        page.Children.Add(note);
        Content = DialogFooter.Page(page, DialogFooter.OkCancel(ok, () => Close(null)));
    }

    public IReadOnlyList<CheckChoice> Projects { get; }

    public IReadOnlyList<CheckChoice> Services { get; }

    /// <summary>What is said under the backing services of one of them (SQL Server's image on an Arm engine); null for nothing.</summary>
    public Words? Note
    {
        get => note.IsVisible ? ShownWords.Of(note, TextBlock.TextProperty) : null;
        set
        {
            ShownWords.Set(note, TextBlock.TextProperty, value);
            note.IsVisible = value is not null;
        }
    }

    private static TextBlock Heading(Words text, int row)
    {
        TextBlock heading = new() { Classes = { StyleClasses.PaneTitle }, [ShownWords.TextProperty] = text, Margin = new Thickness(0, row == 0 ? 0 : Gaps.Wide, 0, Gaps.Normal) };
        Grid.SetRow(heading, row);
        return heading;
    }

    private static Border List(IReadOnlyList<CheckChoice> choices, int row)
    {
        Border framed = new() { Classes = { StyleClasses.Framed }, Child = CheckRows.List(choices), MaxHeight = row == 3 ? 140 : double.PositiveInfinity };
        Grid.SetRow(framed, row);
        return framed;
    }
}
