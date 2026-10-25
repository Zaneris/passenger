using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Core;

namespace Passenger.App.CSharp;

/// <summary>The frame of the refactoring dialogs: the refactoring's name in the title bar, the page's title and what to
/// do above its options, and Cancel and Next below, with why the options will not do beside them. Next makes the
/// refactoring with the options as they stand and closes the dialog with true once it is made; refused, the dialog
/// stays open saying why. Escape cancels.</summary>
public abstract class RefactoringDialog : Window
{
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Border body = new() { Padding = new Thickness(20, 14, 20, 16) };
    private bool running;

    protected RefactoringDialog(string title, string page, string description, double width = 560)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        NextButton = new Button { Content = "Next", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        NextButton.Click += (_, _) => IdeErrors.Observe(Next());
        cancel.Click += (_, _) => Close(false);
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));

        StackPanel header = new()
        {
            Margin = new Thickness(20, 16, 20, 12),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = page, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
            },
        };
        Border top = new() { BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        top.Bind(Border.BorderBrushProperty, top.GetResourceObservable(TokenKeys.Divider));

        Border bottom = DialogFooter.Bar(DialogFooter.Of(NextButton, cancel), problem);
        // Inset as the page above it is, so Next ends where its fields do and a problem starts where its labels do.
        bottom.Padding = new Thickness(20, 10);

        DockPanel root = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>Makes the refactoring with the options as they stand.</summary>
    public Button NextButton { get; }

    /// <summary>Why the options will not do, or why the refactoring was refused; empty when neither.</summary>
    public string Problem => problem.Text ?? string.Empty;

    /// <summary>The page's options, between its title and its buttons.</summary>
    protected Control? Options
    {
        get => body.Child;
        set => body.Child = value;
    }

    /// <summary>Why the options as they stand will not do, checked as they change; null when they will.</summary>
    protected abstract string? Check();

    /// <summary>The refactoring made with the options as they stand: null once it is, else why not.</summary>
    protected abstract Task<string?> MakeAsync();

    /// <summary>Checks the options again, for a dialog whose options just changed: Next refuses them while they will not do,
    /// saying why once the person has had a hand in them (the options it opens with say nothing yet).</summary>
    protected void Changed()
    {
        string? why = Check();
        Refuse(IsVisible ? why : null);
        NextButton.IsEnabled = why is null && !running;
    }

    private void Refuse(string? why)
    {
        problem.Text = why;
        NextButton.IsEnabled = why is null && !running;
    }

    /// <summary>Puts the keys in a control once the dialog shows, a text box's text chosen to type over.</summary>
    protected void FocusOnOpen(Control control) => Opened += (_, _) => Dispatcher.UIThread.Post(() =>
    {
        control.Focus();
        if (control is TextBox box)
        {
            box.SelectAll();
        }
    }, DispatcherPriority.Loaded);

    private async Task Next()
    {
        if (running)
        {
            return;
        }
        if (Check() is { } wrong)
        {
            Refuse(wrong);
            return;
        }
        running = true;
        NextButton.IsEnabled = false;
        BackgroundWork.Begin();
        string? refused;
        try
        {
            refused = await MakeAsync();
        }
        finally
        {
            running = false;
            BackgroundWork.End();
        }
        if (refused is null)
        {
            Close(true);
            return;
        }
        Refuse(refused);
    }

    /// <summary>A label and what it names, the label in a column of its own.</summary>
    protected static Control Row(string label, Control editor, double labelWidth = 130)
    {
        TextBlock name = new() { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center };
        DockPanel row = new();
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        row.Children.Add(editor);
        return row;
    }

    protected static TextBox Field(string text) => new() { Classes = { "search" }, Text = text };
}
