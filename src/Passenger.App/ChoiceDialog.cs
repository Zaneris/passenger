using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>Asks which of several ways to go on, as Rider's Reload or Convert does: what each does, a button for each (the
/// last the default) and Cancel, closing with the words of the button chosen, or null for Cancel. Escape cancels.</summary>
public sealed class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string text, IReadOnlyList<string> choices)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Chrome"));

        Button cancel = new() { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        List<Button> answers = [];
        for (int i = 0; i < choices.Count; i++)
        {
            string choice = choices[i];
            Button button = new() { Content = choice, IsDefault = i == choices.Count - 1 };
            if (button.IsDefault)
            {
                button.Classes.Add("primary");
            }
            button.Click += (_, _) => Close(choice);
            answers.Add(button);
        }
        StackPanel buttons = answers.Count == 0
            ? DialogFooter.Of(cancel, null)
            : DialogFooter.OfChoices(answers[^1], cancel, [.. answers.Take(answers.Count - 1).Reverse()]);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
    }
}
