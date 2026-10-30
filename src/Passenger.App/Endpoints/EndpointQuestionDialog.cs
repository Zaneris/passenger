using Avalonia.Controls;

namespace Passenger.App.Endpoints;

/// <summary>The question a request asks before it takes something from the person's computer to a host: the host, and
/// each environment variable (one not set said so, as it goes empty) and file outside the folder it reads. Allow (the
/// default) or Don't allow, and Always for this folder at the footer's left, which keeps an Allow in the folder's state
/// (never a no). Escape and closing answer no.</summary>
public sealed class EndpointQuestionDialog : IdeWindow
{
    public const string Allow = "Allow";
    public const string DontAllow = "Don't allow";

    public EndpointQuestionDialog(string request, string host, IReadOnlyList<EndpointRead> reads)
    {
        Reads = reads;
        AllowButton = new Button { Content = Allow, IsDefault = true, Classes = { "primary" } };
        DontAllowButton = new Button { Content = DontAllow, IsCancel = true };
        AllowButton.Click += (_, _) => Close(true);
        DontAllowButton.Click += (_, _) => Close(false);

        StackPanel listed = new() { Spacing = 4 };
        foreach (EndpointRead read in reads)
        {
            listed.Children.Add(DialogFooter.Paragraph($"• {read}"));
        }
        DialogFooter.Question(this, $"Send to {host} what this request reads from your computer?",
        [
            DialogFooter.Paragraph($"“{request}” sends to {host}:"),
            listed,
            DialogFooter.Paragraph($"A folder's .http and env files can name anything on your computer. Allowed, these go to {host} from this "
                + "folder until it closes; Always for this folder keeps that, and anything else asks again."),
        ], DialogFooter.Answers(AllowButton, DontAllowButton), Always);
    }

    /// <summary>What the request reads, as the question lists it.</summary>
    public IReadOnlyList<EndpointRead> Reads { get; }

    public Button AllowButton { get; }

    public Button DontAllowButton { get; }

    /// <summary>Always for this folder, which keeps an Allow.</summary>
    public CheckBox Always { get; } = new() { Content = "Always for this folder" };
}
