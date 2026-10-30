using Avalonia.Controls;

namespace Passenger.App.Containers;

/// <summary>The question a container run asks when its Compose project has containers of the services it would start that
/// this Passenger did not make (the person's own <c>docker compose up</c>'s): Compose would make them again with Passenger's
/// labels and settings, and Stop would remove them. Replace Them (the default) lets it, after which they are the run's;
/// Cancel runs nothing and leaves them as they are, as Escape and closing do.</summary>
public sealed class ReplaceContainersDialog : IdeWindow
{
    public const string Replace = "Replace Them";

    public ReplaceContainersDialog(string configuration, string project, IReadOnlyList<string> containers)
    {
        ReplaceButton = DialogFooter.Default(Replace);
        CancelButton = DialogFooter.Cancel(() => Close(false));
        ReplaceButton.Click += (_, _) => Close(true);

        bool one = containers.Count == 1;
        DialogFooter.Question(this, one ? "A container Passenger did not make" : "Containers Passenger did not make",
        [
            DialogFooter.Paragraph($"{configuration} would replace {string.Join(", ", containers)} of the Compose project {project}: Compose makes "
                + $"{(one ? "it" : "them")} again with Passenger's settings for this run."),
            DialogFooter.Paragraph($"Replace Them runs it, and {(one ? "the container is" : "the containers are")} then the run's: Stop removes "
                + $"{(one ? "it" : "them")}. Cancel runs nothing and leaves {(one ? "it" : "them")} as {(one ? "it is" : "they are")}."),
        ], DialogFooter.Answers(ReplaceButton, CancelButton));
    }

    public Button ReplaceButton { get; }

    public Button CancelButton { get; }
}
