using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Templates;

/// <summary>A template's options as New Project and New Item show them: each a checkbox, a list of its choices or a box
/// for text, enabled as its condition allows with the others as they are, a value the person set kept by the next
/// template with the option; with what they come to on dotnet new's command line, and why one would be refused.</summary>
public sealed class TemplateOptions
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> byPerson = new(StringComparer.Ordinal);
    private readonly List<(TemplateOption Option, Control Row, Control Editor)> shown = [];

    /// <summary>The rest of a template's options where it picked some to show, opened by the person and kept open across
    /// templates.</summary>
    private readonly FormFold more = new();

    /// <summary>Set while it sets a list's choice itself, so the choice is not taken for the person's.</summary>
    private bool setting;

    /// <summary>Where the options are shown, one row each, but those under More options.</summary>
    public StackPanel Panel { get; } = new() { Spacing = 10 };

    /// <summary>More options and the rows it opens, shown where a template has some; its dialog places it last.</summary>
    public StackPanel Folded { get; }

    public TemplateOptions() => Folded = new StackPanel { Spacing = 10, IsVisible = false, Children = { more.Opener, more.Rows } };

    /// <summary>A value changed, or what is enabled with it: what the options come to has changed.</summary>
    public event Action? Changed;

    /// <summary>The labels of the options shown now.</summary>
    public IReadOnlyList<string> Labels => [.. shown.Select(s => s.Option.Label)];

    /// <summary>The control that sets an option, by its label, for a click; null when none is shown.</summary>
    public Control? Editor(string label) => shown.FirstOrDefault(s => s.Option.Label == label).Editor;

    /// <summary>A template variant's options, but those named in <paramref name="asked"/>, which the dialog asks itself.</summary>
    public void Show(TemplateVariant variant, IReadOnlySet<string>? asked = null)
    {
        Panel.Children.Clear();
        more.Rows.Children.Clear();
        shown.Clear();
        values.Clear();
        foreach (TemplateOption option in variant.Options.Where(o => asked?.Contains(o.Name) != true))
        {
            string value = byPerson.TryGetValue(option.Name, out string? kept) && (option.Kind != TemplateOptionKind.Choice || option.Choices.Any(c => c.Value == kept))
                ? kept : option.Default;
            values[option.Name] = value;
            // One of the rest says what it is under its field, where its switch or name alone may not.
            TextBlock? said = !option.Picked && option.Description is { Length: > 0 } description ? MutedText.Wrapped(description) : null;
            string label = option.NamedBySwitch ? option.Label : $"{option.Label}:";
            Control editor;
            Control row;
            switch (option.Kind)
            {
                case TemplateOptionKind.Bool:
                    CheckBox box = new() { Content = option.Label, IsChecked = IsTrue(value) != option.Inverted };
                    box.IsCheckedChanged += (_, _) => Set(option, (box.IsChecked == true) != option.Inverted ? "true" : "false");
                    editor = box;
                    row = Row(null, Described(box, said), top: said is not null);
                    break;
                case TemplateOptionKind.Choice:
                    ComboBox choices = new() { Classes = { StyleClasses.Form }, ItemsSource = option.Choices.Select(c => c.Label).ToList() };
                    choices.SelectedIndex = Math.Max(0, option.Choices.ToList().FindIndex(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));
                    choices.SelectionChanged += (_, _) =>
                    {
                        if (!setting && choices.SelectedIndex >= 0)
                        {
                            Set(option, option.Choices[choices.SelectedIndex].Value);
                        }
                    };
                    editor = choices;
                    row = Row(label, Described(choices, said), top: said is not null);
                    break;
                default:
                    TextBox text = new() { Classes = { StyleClasses.Search }, Text = value, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 240 };
                    text.TextChanged += (_, _) => Set(option, text.Text ?? string.Empty);
                    editor = text;
                    row = Row(label, Described(text, said), top: said is not null);
                    break;
            }
            if (said is null && option.Description is { Length: > 0 } tip)
            {
                ToolTip.SetTip(row, tip);
            }
            (option.More ? more.Rows : Panel).Children.Add(row);
            shown.Add((option, row, editor));
        }
        Folded.IsVisible = more.Rows.Children.Count > 0;
        more.Title = $"More options ({more.Rows.Children.Count})";
        // A value the person set among them stays in sight, as it goes to the command line.
        more.IsOpen |= shown.Any(s => s.Option.More && !string.Equals(values[s.Option.Name], s.Option.Default, StringComparison.OrdinalIgnoreCase));
        Enable();
    }

    /// <summary>The link that opens the options waiting under More options, while a template has some.</summary>
    public Button MoreOptions => more.Opener;

    /// <summary>A field with what it is said under it, where it is said.</summary>
    private static Control Described(Control editor, TextBlock? said)
    {
        if (said is null)
        {
            return editor;
        }
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Spacing = 4, Children = { editor, said } };
    }

    /// <summary>No options, as for a template that has none to show.</summary>
    public void Clear()
    {
        Panel.Children.Clear();
        more.Rows.Children.Clear();
        Folded.IsVisible = false;
        shown.Clear();
        values.Clear();
    }

    /// <summary>dotnet new's arguments for the options: each enabled and changed from its default, by the command line's
    /// name for it (or the one it takes it through, <see cref="TemplateOption.Through"/>).</summary>
    public IEnumerable<string> Arguments()
    {
        foreach ((TemplateOption each, _, Control editor) in shown)
        {
            if (editor.IsEnabled && values.TryGetValue(each.Name, out string? value) && !string.Equals(value, each.Default, StringComparison.OrdinalIgnoreCase))
            {
                if (each.Through is { } through)
                {
                    yield return $"--{through.Option}";
                    yield return through.Opposite ? (IsTrue(value) ? "false" : "true") : value;
                }
                else
                {
                    yield return $"--{each.Option}";
                    yield return value;
                }
            }
        }
    }

    /// <summary>Why the options would be refused: one needed left empty, a number that does not read; null when none.</summary>
    public string? WhyNot()
    {
        foreach ((TemplateOption option, _, _) in shown.Where(s => s.Editor.IsEnabled))
        {
            string value = values.GetValueOrDefault(option.Name, option.Default);
            if (option.Required && value.Length == 0)
            {
                return $"{option.Label} is needed";
            }
            if (option.Kind == TemplateOptionKind.Integer && value.Length > 0 && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return $"{option.Label}: please enter an integer number";
            }
            if (option.Kind == TemplateOptionKind.Float && value.Length > 0 && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return $"{option.Label}: please enter a float number";
            }
        }
        return null;
    }

    /// <summary>A row of a dialog's form: its label (none for a checkbox, which carries its own) and what sets it, the label
    /// at the top of a field with words under it.</summary>
    public static Control Row(string? label, Control editor, bool top = false)
    {
        if (editor is ComboBox)
        {
            editor.HorizontalAlignment = HorizontalAlignment.Left;
        }
        return FormRows.Of(label, editor, top);
    }

    /// <summary>What a project has of an option its template turns off, where that is not the option's value: VSTest for a
    /// test template's Test runner, as a test project runs on the Testing Platform only where its template sets that up.</summary>
    private static string? WhenOff(TemplateOption option) => option.Name.Equals("TestRunner", StringComparison.OrdinalIgnoreCase)
        ? option.Choices.FirstOrDefault(c => c.Value.Equals("VSTest", StringComparison.OrdinalIgnoreCase))?.Value
        : null;

    private void Set(TemplateOption option, string value)
    {
        values[option.Name] = value;
        byPerson[option.Name] = value;
        Enable();
    }

    /// <summary>Enables each option its template's condition allows with the others as they are now; a list turned off
    /// shows what the project will have (<see cref="WhenOff"/>), its value kept for when it is on again.</summary>
    private void Enable()
    {
        foreach ((TemplateOption option, Control row, Control editor) in shown)
        {
            editor.IsEnabled = ProjectTemplates.IsEnabled(option.EnabledWhen, values);
            row.Opacity = editor.IsEnabled ? 1 : 0.55;
            if (editor is ComboBox choices)
            {
                string value = (editor.IsEnabled ? null : WhenOff(option)) ?? values.GetValueOrDefault(option.Name, option.Default);
                int at = option.Choices.ToList().FindIndex(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
                bool was = setting;
                setting = true;
                choices.SelectedIndex = Math.Max(0, at);
                setting = was;
            }
        }
        Changed?.Invoke();
    }

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
