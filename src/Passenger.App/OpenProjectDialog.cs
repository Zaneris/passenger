using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>Rider's Open Solution question, asked as a solution or folder is opened from a window that has one: "Where
/// would you like to open the solution 'Name'?", This Window, New Window (the default) and Cancel, and Don't ask again,
/// which makes the answer Settings' Open project in. Escape cancels.</summary>
public sealed class OpenProjectDialog : Window
{
    public const string ThisWindow = "This Window";
    public const string NewWindow = "New Window";

    private readonly CheckBox remember = new() { Content = "Don't ask again" };

    public OpenProjectDialog(string name)
    {
        Title = "Open Solution";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("Passenger.Chrome"));
        Question = $"Where would you like to open the solution '{name}'?";

        Button cancel = new() { Content = "Cancel", IsCancel = true };
        Button here = new() { Content = ThisWindow };
        Button apart = new() { Content = NewWindow, IsDefault = true, Classes = { "primary" } };
        cancel.Click += (_, _) => Close();
        here.Click += (_, _) => Choose(OpenTarget.ThisWindow);
        apart.Click += (_, _) => Choose(OpenTarget.NewWindow);

        StackPanel choices = DialogFooter.OfChoices(apart, cancel, here);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = Title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = Question, TextWrapping = TextWrapping.Wrap },
                DialogFooter.WithLeading(remember, choices),
            },
        };
    }

    /// <summary>What it asks.</summary>
    public string Question { get; }

    /// <summary>Where the person chose to open it, or null once cancelled (or while it is still asking).</summary>
    public OpenTarget? Chosen { get; private set; }

    /// <summary>Whether Don't ask again is ticked, which keeps the answer, never a Cancel.</summary>
    public bool Remember => remember.IsChecked == true;

    private void Choose(OpenTarget target)
    {
        Chosen = target;
        Close();
    }
}
