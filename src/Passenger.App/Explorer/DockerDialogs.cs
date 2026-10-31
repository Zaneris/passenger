using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;

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
        Title = "Add Docker Support";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dockerfile = new RadioButton
        {
            GroupName = "kind",
            IsChecked = !hasDockerfile,
            IsEnabled = !hasDockerfile,
            Content = Words(hasDockerfile ? $"A Dockerfile ({project} has one already)" : "A Dockerfile beside the project, and a .dockerignore at the solution's folder"),
        };
        sdk = new RadioButton
        {
            GroupName = "kind",
            IsChecked = hasDockerfile,
            Content = Words($"The .NET SDK's container support: dotnet publish /t:{MSBuildNames.PublishContainer} builds the image, with no Dockerfile"),
        };
        foreach (RadioButton choice in new[] { dockerfile, sdk })
        {
            choice.Margin = new Thickness(0, 0, 0, 8);
            choice.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        TextBlock heading = new() { Text = $"How {project}'s image is built", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Accept();
        StackPanel page = new() { Margin = new Thickness(16), Children = { heading, dockerfile, sdk } };
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
    private static TextBlock Words(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}

/// <summary>A project or a backing service Add > Docker Compose offers, checked while it is to be in compose.yaml.</summary>
public sealed class ComposeChoice(string name, string detail, bool chosen, object value) : Observable, ICheckChoice
{
    private bool isChosen = chosen;

    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public bool CanChoose => true;

    public object Value { get; } = value;

    public bool IsChosen
    {
        get => isChosen;
        set => Set(ref isChosen, value);
    }
}

/// <summary>Add > Docker Compose on the solution: its projects that run, each checked, a project with no Dockerfile given
/// one by Docker Support as it is written; and the databases, caches and brokers their packages point to, each checked.
/// Closes with true for OK, null when cancelled; what is checked is read from <see cref="Projects"/> and
/// <see cref="Services"/>.</summary>
public sealed class DockerComposeDialog : IdeWindow
{
    public DockerComposeDialog(string solution, IReadOnlyList<ComposeChoice> projects, IReadOnlyList<ComposeChoice> services)
    {
        Projects = projects;
        Services = services;
        Title = "Add Docker Compose";
        Width = 560;
        Height = 460;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Close((bool?)true);
        Grid page = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        page.Children.Add(Heading($"The services of {solution}'s compose.yaml", 0));
        page.Children.Add(List(projects, 1));
        page.Children.Add(Heading("Beside them, from their packages", 2));
        Control backing = services.Count == 0
            ? new TextBlock { Classes = { StyleClasses.EmptyText }, Text = "No database, cache or broker among their packages", Margin = new Thickness(0, 0, 0, 4) }
            : List(services, 3);
        Grid.SetRow(backing, 3);
        page.Children.Add(backing);
        Content = DialogFooter.Page(page, DialogFooter.OkCancel(ok, () => Close(null)));
    }

    public IReadOnlyList<ComposeChoice> Projects { get; }

    public IReadOnlyList<ComposeChoice> Services { get; }

    private static TextBlock Heading(string text, int row)
    {
        TextBlock heading = new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 8) };
        Grid.SetRow(heading, row);
        return heading;
    }

    private static Border List(IReadOnlyList<ComposeChoice> choices, int row)
    {
        ListBox list = new() { Classes = { StyleClasses.Tree }, ItemsSource = choices };
        ReusedRows.Use(list, CheckRows.Of);
        Border framed = new() { Classes = { StyleClasses.Framed }, Child = list, MaxHeight = row == 3 ? 140 : double.PositiveInfinity };
        Grid.SetRow(framed, row);
        return framed;
    }
}
