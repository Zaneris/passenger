using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.CSharp;

namespace Passenger.App.Settings;

/// <summary>Edit variables: the template's variables down the left in the order Tab visits them, Up and Down to move
/// one; for the one chosen, its macro (or "No macro selected"), the macro's argument when it takes one, whether it is
/// Editable, and Change macro to choose another. Done hands them back.</summary>
public sealed class TemplateVariablesDialog : Window
{
    private readonly List<TemplateVariable> variables;
    private readonly ListBox names = new() { Classes = { "tree" } };
    private readonly TextBlock macro = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock parameterName = new() { VerticalAlignment = VerticalAlignment.Center, Width = 120 };
    private readonly DockPanel parameterRow;

    /// <summary>Set while the fields show a variable, so what they hold then is no edit of it.</summary>
    private bool showing;

    public TemplateVariablesDialog(IReadOnlyList<TemplateVariable> variables)
    {
        this.variables = [.. variables];
        Title = "Edit variables";
        Width = 620;
        Height = 360;
        MinWidth = 480;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Button up = ToolButtons.Of(IconKeys.ArrowUp, "Up");
        Button down = ToolButtons.Of(IconKeys.ArrowDown, "Down");
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        names.SelectionChanged += (_, _) => ShowChosen();
        ParameterBox = new TextBox { Classes = { "search" } };
        ParameterBox.TextChanged += (_, _) => Edit(v => v with { Parameter = ParameterBox.Text ?? string.Empty });
        EditableBox = new CheckBox { Content = "Editable" };
        EditableBox.IsCheckedChanged += (_, _) => Edit(v => v with { Editable = EditableBox.IsChecked == true });
        ChangeMacroButton = new Button { Content = "Change macro" };
        ChangeMacroButton.Click += (_, _) => IdeErrors.Observe(ChangeMacro());
        DoneButton = new Button { Content = "Done", IsDefault = true, Classes = { "primary" } };
        DoneButton.Click += (_, _) => Close(true);

        parameterRow = new DockPanel { Children = { parameterName, ParameterBox } };
        DockPanel.SetDock(parameterName, Dock.Left);
        StackPanel chosen = new()
        {
            Spacing = 10,
            Margin = new Thickness(16, 0, 0, 0),
            Children = { macro, parameterRow, EditableBox, ChangeMacroButton },
        };
        StackPanel moves = new() { Orientation = Orientation.Horizontal, Spacing = 2, Children = { up, down } };
        DockPanel left = new() { Width = 180 };
        DockPanel.SetDock(moves, Dock.Bottom);
        left.Children.Add(moves);
        Border framed = new() { Classes = { "framed" }, Child = names };
        left.Children.Add(framed);
        DockPanel body = new() { Margin = new Thickness(16, 14, 16, 8) };
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(chosen);
        StackPanel buttons = DialogFooter.Of(DoneButton, null);
        Border bottom = DialogFooter.Bar(buttons);
        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Border page = new() { Child = root };
        Content = page;
        List();
        names.SelectedIndex = 0;
    }

    public TextBox ParameterBox { get; }

    public CheckBox EditableBox { get; }

    public Button ChangeMacroButton { get; }

    public Button DoneButton { get; }

    /// <summary>The variables as they stand, in their order.</summary>
    public IReadOnlyList<TemplateVariable> Variables => variables;

    /// <summary>What the chosen variable's macro says it is, as the dialog shows it.</summary>
    public string MacroText => macro.Text ?? string.Empty;

    /// <summary>Chooses a variable by name; false when there is none.</summary>
    public bool Choose(string name)
    {
        int at = variables.FindIndex(v => v.Name == name);
        names.SelectedIndex = at;
        return at >= 0;
    }

    private TemplateVariable? Chosen => names.SelectedIndex >= 0 && names.SelectedIndex < variables.Count ? variables[names.SelectedIndex] : null;

    private void Edit(Func<TemplateVariable, TemplateVariable> change)
    {
        if (!showing && Chosen is { } chosen)
        {
            variables[names.SelectedIndex] = change(chosen);
        }
    }

    private void Move(int step)
    {
        int at = names.SelectedIndex;
        int to = at + step;
        if (at < 0 || to < 0 || to >= variables.Count)
        {
            return;
        }
        (variables[at], variables[to]) = (variables[to], variables[at]);
        List();
        names.SelectedIndex = to;
    }

    private void List() => names.ItemsSource = (string[])[.. variables.Select(v => v.Name)];

    /// <summary>Choose macro, for the chosen variable: none keeps it empty, to be typed.</summary>
    private async Task ChangeMacro()
    {
        if (Chosen is not { } chosen)
        {
            return;
        }
        ChooseMacroDialog dialog = new(chosen.Macro);
        if (await dialog.ShowDialog<bool>(this) && Chosen is { } still && still.Name == chosen.Name)
        {
            Edit(v => v with { Macro = dialog.Macro?.Name ?? string.Empty, Parameter = dialog.Macro?.Name == v.Macro ? v.Parameter : string.Empty });
            ShowChosen();
        }
    }

    private void ShowChosen()
    {
        TemplateVariable? shown = Chosen;
        TemplateMacro? its = shown is null ? null : LiveTemplates.Macros.FirstOrDefault(m => m.Name == shown.Macro);
        showing = true;
        try
        {
            macro.Text = its?.Description ?? "No macro selected";
            parameterRow.IsVisible = its?.Parameter is not null;
            parameterName.Text = its?.Parameter is { } named ? char.ToUpperInvariant(named[0]) + named[1..] + ":" : string.Empty;
            ParameterBox.Text = shown?.Parameter ?? string.Empty;
            EditableBox.IsChecked = shown?.Editable ?? true;
        }
        finally
        {
            showing = false;
        }
    }

}

/// <summary>Choose macro: "(No macro)" and every macro by what it does; OK, or a double click, chooses.</summary>
public sealed class ChooseMacroDialog : Window
{
    /// <summary>What "(No macro)" is called in the list.</summary>
    public const string NoMacro = "(No macro)";

    private readonly ListBox list = new() { Classes = { "tree" } };

    public ChooseMacroDialog(string current)
    {
        Title = "Choose macro";
        Width = 460;
        Height = 380;
        MinWidth = 360;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        list.ItemsSource = (string[])[NoMacro, .. LiveTemplates.Macros.Select(m => m.Description)];
        list.SelectedIndex = LiveTemplates.Macros.ToList().FindIndex(m => m.Name == current) + 1;
        list.DoubleTapped += (_, _) => Close(true);
        OkButton = new Button { Content = "OK", IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        OkButton.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        StackPanel buttons = DialogFooter.Of(OkButton, cancel);
        Border bottom = DialogFooter.Bar(buttons);
        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Classes = { "framed" }, Margin = new Thickness(12), Child = list });
        Border page = new() { Child = root };
        Content = page;
    }

    public Button OkButton { get; }

    /// <summary>The macro chosen, null for none.</summary>
    public TemplateMacro? Macro => list.SelectedIndex > 0 ? LiveTemplates.Macros[list.SelectedIndex - 1] : null;

    /// <summary>Chooses an entry by what the list says; false when it has none.</summary>
    public bool Choose(string description)
    {
        int at = list.Items.Cast<string>().ToList().IndexOf(description);
        list.SelectedIndex = at;
        return at >= 0;
    }
}
