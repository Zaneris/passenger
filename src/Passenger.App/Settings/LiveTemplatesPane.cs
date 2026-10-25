using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App.CSharp;
using Passenger.Languages.CSharp;

namespace Passenger.App.Settings;

/// <summary>The Editor | Live Templates | C# page: the templates down the left, each its shortcut in bold and its
/// description, sorted by name, under New Template, Clone And Edit Template and Delete Template; the chosen one's
/// editor on the right (Shortcut, Description, Options, Availability, Use in, Mnemonic, Edit variables, then its text).
/// Each row's check box turns a template off or on again. Every change goes back to the settings being edited at once,
/// kept as differences from the built-in templates.</summary>
public sealed class LiveTemplatesPane : DockPanel
{
    /// <summary>The names of the places a template is offered: short in its editor's Availability, long in its
    /// Availability configuration.</summary>
    public static readonly (TemplateAvailability Place, string Short, string Long)[] Places =
    [
        (TemplateAvailability.Statements, "C# statements", "In C# where statement is allowed"),
        (TemplateAvailability.TypeMembers, "C# type members", "In C# where type member declaration is allowed"),
        (TemplateAvailability.TypesAndNamespaces, "C# types and namespaces", "In C# where namespace declaration is allowed"),
        (TemplateAvailability.Expressions, "C# expressions", "In C# where expression is allowed"),
        (TemplateAvailability.LineStart, "At line start", "At line start"),
        (TemplateAvailability.Everywhere, "Everywhere", "Everywhere"),
    ];

    private readonly Action<IReadOnlyList<LiveTemplate>> changed;
    private readonly List<LiveTemplate> templates;
    private readonly ListBox list = new() { Classes = { "tree" } };
    private readonly Control form;
    private readonly TextBlock nothing = new() { Classes = { "muted" }, Text = "No template is chosen", Margin = new Thickness(16, 10) };
    private readonly TextBlock mnemonicSaid = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock variablesSaid = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Set while the fields show a template, so what they hold then is no edit of it.</summary>
    private bool showing;

    public LiveTemplatesPane(IReadOnlyList<LiveTemplate> templates, Action<IReadOnlyList<LiveTemplate>> changed)
    {
        FormRows.Scope(this);
        this.templates = [.. templates];
        this.changed = changed;
        Height = 560;
        NewButton = ToolButtons.Of(IconKeys.Add, "New Template");
        CloneButton = ToolButtons.Of(IconKeys.Copy, "Clone And Edit Template");
        DeleteButton = ToolButtons.Of(IconKeys.Minimize, "Delete Template");
        RestoreButton = ToolButtons.Of(IconKeys.Rollback, "Restore Default");
        NewButton.Click += (_, _) => New();
        CloneButton.Click += (_, _) => Clone();
        DeleteButton.Click += (_, _) => Delete();
        RestoreButton.Click += (_, _) => Restore();
        list.ItemTemplate = new FuncDataTemplate<LiveTemplate>((template, _) => template is null ? new Panel() : RowView(template), supportsRecycling: false);
        list.SelectionChanged += (_, _) => ShowChosen();

        ShortcutBox = Field();
        DescriptionBox = Field();
        ShortcutBox.TextChanged += (_, _) => Edit(t => t with { Abbreviation = ShortcutBox.Text?.Trim() ?? string.Empty }, relist: true);
        DescriptionBox.TextChanged += (_, _) => Edit(t => t with { Description = DescriptionBox.Text ?? string.Empty }, relist: true);
        ReformatBox = new CheckBox { Content = "Reformat" };
        ToolTip.SetTip(ReformatBox, "Its lines indented as the code it goes in, a tab in its text the file's indentation; otherwise it goes in as written");
        ReformatBox.IsCheckedChanged += (_, _) => Edit(t => t with { Reformat = ReformatBox.IsChecked == true });
        AvailabilityText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        AvailabilityButton = new Button { Content = "…", Padding = new Thickness(8, 1), Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(AvailabilityButton, "Availability configuration");
        AvailabilityButton.Click += (_, _) => IdeErrors.Observe(ConfigureAvailability());
        GenerationButton = Use("Generation", TemplateUse.Generation);
        SurroundButton = Use("Surround", TemplateUse.Surround);
        BothButton = Use("Both", TemplateUse.Both);
        MnemonicBox = new TextBox { Classes = { "search" }, Width = 44 };
        MnemonicBox.TextChanged += (_, _) => Mnemonic();
        EditVariablesButton = new Button { Content = "Edit variables" };
        EditVariablesButton.Click += (_, _) => IdeErrors.Observe(EditVariables());
        TextBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Fonts.Mono,
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Top,
            Classes = { "search" },
            Margin = new Thickness(0, 10, 0, 0),
        };
        TextBox.TextChanged += (_, _) => Edit(t => WithText(t, (TextBox.Text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)));

        StackPanel rows = new()
        {
            Spacing = 8,
            Children =
            {
                Row("Shortcut:", ShortcutBox),
                Row("Description:", DescriptionBox),
                Row("Options:", ReformatBox),
                Row("Availability:", new DockPanel { Children = { Docked(AvailabilityButton, Dock.Right), AvailabilityText } }),
                Row("Use in:", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { GenerationButton, SurroundButton, BothButton } }),
                Row("Mnemonic:", new StackPanel { Orientation = Orientation.Horizontal, Children = { MnemonicBox, mnemonicSaid } }),
                Row(string.Empty, new DockPanel { Children = { Docked(EditVariablesButton, Dock.Left), variablesSaid } }),
            },
        };
        variablesSaid.Margin = new Thickness(10, 0, 0, 0);
        DockPanel editor = new() { Margin = new Thickness(16, 0, 0, 0) };
        SetDock(rows, Dock.Top);
        editor.Children.Add(rows);
        editor.Children.Add(TextBox);
        form = editor;

        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 4), Children = { NewButton, CloneButton, DeleteButton, RestoreButton } };
        Border framed = new() { Classes = { "framed" }, Child = list };
        DockPanel left = new() { Width = 260 };
        SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(framed);
        SetDock(left, Dock.Left);
        Children.Add(left);
        Children.Add(new Panel { Children = { form, nothing } });
        List();
        list.SelectedIndex = 0;
        ShowChosen();
    }

    public Button NewButton { get; }

    public Button CloneButton { get; }

    public Button DeleteButton { get; }

    /// <summary>Puts a built-in template back as it ships.</summary>
    public Button RestoreButton { get; }

    public TextBox ShortcutBox { get; }

    public TextBox DescriptionBox { get; }

    public CheckBox ReformatBox { get; }

    /// <summary>Where the chosen template is offered, in words.</summary>
    public TextBlock AvailabilityText { get; }

    /// <summary>Opens the Availability configuration for the chosen template.</summary>
    public Button AvailabilityButton { get; }

    public RadioButton GenerationButton { get; }

    public RadioButton SurroundButton { get; }

    public RadioButton BothButton { get; }

    public TextBox MnemonicBox { get; }

    public Button EditVariablesButton { get; }

    public TextBox TextBox { get; }

    /// <summary>The templates as listed, by shortcut.</summary>
    public IReadOnlyList<LiveTemplate> Rows => list.ItemsSource as IReadOnlyList<LiveTemplate> ?? [];

    /// <summary>The template chosen in the list, as the fields show it.</summary>
    public LiveTemplate? Chosen => list.SelectedItem as LiveTemplate;

    /// <summary>Chooses a template in the list by its shortcut, scrolled into view; false when none has it.</summary>
    public bool Choose(string shortcut)
    {
        if (Rows.FirstOrDefault(t => t.Abbreviation == shortcut) is not { } row)
        {
            return false;
        }
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        return true;
    }

    /// <summary>A template's check box in the list, which turns it off or on again.</summary>
    public CheckBox? EnabledBox(string shortcut)
    {
        LiveTemplate? row = Rows.FirstOrDefault(t => t.Abbreviation == shortcut);
        return row is null ? null : (list.ContainerFromItem(row) as ListBoxItem)?.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
    }

    /// <summary>The words for where a template is offered: the places' short names, the first two and a count past
    /// three, "nowhere" for none.</summary>
    public static string Said(TemplateAvailability where)
    {
        string[] names = [.. Places.Where(p => where.HasFlag(p.Place)).Select(p => p.Short)];
        return names.Length switch
        {
            0 => "nowhere",
            <= 3 => string.Join(", ", names),
            _ => $"{names[0]}, {names[1]}, and {names.Length - 2} more…",
        };
    }

    /// <summary>New Template: an empty one to fill in, typed where a statement goes, its shortcut ready to
    /// type.</summary>
    private void New()
    {
        LiveTemplate added = new(Unique("template"), string.Empty, TemplateScope.Statement, string.Empty, []) { Key = "user-" + Guid.NewGuid().ToString("N") };
        templates.Add(added);
        Changed();
        List();
        Choose(added.Abbreviation);
        Dispatcher.UIThread.Post(() =>
        {
            ShortcutBox.Focus();
            ShortcutBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Clone And Edit Template: a copy of the chosen one as one of the person's own.</summary>
    private void Clone()
    {
        if (Chosen is not { } source)
        {
            return;
        }
        LiveTemplate copy = source with { Key = "user-" + Guid.NewGuid().ToString("N"), BuiltIn = false, Abbreviation = Unique(source.Abbreviation), Mnemonic = null };
        templates.Add(copy);
        Changed();
        List();
        Choose(copy.Abbreviation);
    }

    /// <summary>Delete Template, for one of the person's own; a built-in one is turned off by its check box instead, so
    /// it can be turned on again.</summary>
    private void Delete()
    {
        if (Chosen is not { BuiltIn: false } removed)
        {
            return;
        }
        int at = list.SelectedIndex;
        templates.Remove(removed);
        Changed();
        List();
        list.SelectedIndex = Math.Min(at, Rows.Count - 1);
    }

    /// <summary>A built-in template as it ships, the person's changes to it dropped.</summary>
    private void Restore()
    {
        if (Chosen is not { BuiltIn: true } chosen || LiveTemplates.BuiltIn.FirstOrDefault(t => t.Key == chosen.Key) is not { } shipped)
        {
            return;
        }
        templates[templates.FindIndex(t => t.Key == chosen.Key)] = shipped;
        Changed();
        List();
        Choose(shipped.Abbreviation);
    }

    private string Unique(string stem)
    {
        string name = stem;
        for (int n = 1; templates.Any(t => t.Abbreviation == name); n++)
        {
            name = stem + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return name;
    }

    /// <summary>Changes the chosen template as a field says, unless the fields are only showing it.</summary>
    private void Edit(Func<LiveTemplate, LiveTemplate> change, bool relist = false)
    {
        if (showing || Chosen is not { } chosen)
        {
            return;
        }
        LiveTemplate after = change(chosen);
        templates[templates.FindIndex(t => t.Key == chosen.Key)] = after;
        Changed();
        // The list shows the change as it is made; the fields hold what they show already.
        showing = true;
        try
        {
            if (relist)
            {
                List();
            }
            else
            {
                list.ItemsSource = (LiveTemplate[])[.. Rows.Select(t => t.Key == after.Key ? after : t)];
            }
            list.SelectedItem = Rows.FirstOrDefault(t => t.Key == after.Key);
        }
        finally
        {
            showing = false;
        }
        ShowState(after);
    }

    /// <summary>A template with new text: its variables follow it, those it still names keeping their macros.</summary>
    private static LiveTemplate WithText(LiveTemplate template, string text)
    {
        List<string> named = LiveTemplates.Names(text);
        return template with { Text = text, Variables = [.. template.Variables.Where(v => named.Contains(v.Name))] };
    }

    private void Changed() => changed(templates);

    private void Mnemonic()
    {
        string typed = MnemonicBox.Text?.Trim() ?? string.Empty;
        bool fits = typed.Length == 0 || typed is [char digit] && char.IsAsciiDigit(digit);
        mnemonicSaid.Text = fits ? string.Empty : "Mnemonic could be single digit only";
        if (fits)
        {
            Edit(t => t with { Mnemonic = typed.Length == 0 ? null : typed[0] });
        }
    }

    /// <summary>The Availability configuration, over the settings: the places the chosen template is offered.</summary>
    private async Task ConfigureAvailability()
    {
        if (Chosen is not { } chosen || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        (TemplateAvailability where, TemplateUse use) = LiveTemplates.Availability(chosen);
        AvailabilityDialog dialog = new(where, use);
        if (await dialog.ShowDialog<bool>(owner) && Chosen is { } still && still.Key == chosen.Key)
        {
            Edit(t => LiveTemplates.Available(t, dialog.Availability, LiveTemplates.Availability(t).Use));
            ShowChosen();
        }
    }

    /// <summary>Edit variables, over the settings: what the chosen template's variables take their values from, whether
    /// Tab stops at each, and in what order.</summary>
    private async Task EditVariables()
    {
        if (Chosen is not { } chosen || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        TemplateVariablesDialog dialog = new(chosen.VariablesInText());
        if (await dialog.ShowDialog<bool>(owner) && Chosen is { } still && still.Key == chosen.Key)
        {
            Edit(t => t with { Variables = dialog.Variables });
        }
    }

    private RadioButton Use(string name, TemplateUse use)
    {
        RadioButton button = new() { Content = name, GroupName = "use" + GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture) };
        button.IsCheckedChanged += (_, _) =>
        {
            if (button.IsChecked == true)
            {
                Edit(t => LiveTemplates.Available(t, LiveTemplates.Availability(t).Availability, use));
            }
        };
        return button;
    }

    /// <summary>Lists the templates again, sorted by shortcut, ordinal.</summary>
    private void List() => list.ItemsSource = (LiveTemplate[])[.. templates.OrderBy(t => t.Abbreviation.Length > 0 ? t.Abbreviation : t.Description, StringComparer.Ordinal)];

    /// <summary>Shows the chosen template in the fields, or says none is chosen.</summary>
    private void ShowChosen()
    {
        if (showing)
        {
            return;
        }
        LiveTemplate? shown = Chosen;
        form.IsVisible = shown is not null;
        nothing.IsVisible = shown is null;
        CloneButton.IsEnabled = shown is not null;
        if (shown is null)
        {
            DeleteButton.IsEnabled = RestoreButton.IsEnabled = false;
            return;
        }
        showing = true;
        try
        {
            ShortcutBox.Text = shown.Abbreviation;
            DescriptionBox.Text = shown.Description;
            TextBox.Text = shown.Text;
            ReformatBox.IsChecked = shown.Reformat;
            MnemonicBox.Text = shown.Mnemonic?.ToString() ?? string.Empty;
            mnemonicSaid.Text = string.Empty;
            TemplateUse use = LiveTemplates.Availability(shown).Use;
            GenerationButton.IsChecked = use == TemplateUse.Generation;
            SurroundButton.IsChecked = use == TemplateUse.Surround;
            BothButton.IsChecked = use == TemplateUse.Both;
        }
        finally
        {
            showing = false;
        }
        ShowState(shown);
    }

    /// <summary>What follows from a template as it stands: where it is offered, its variables, and which buttons apply.</summary>
    private void ShowState(LiveTemplate shown)
    {
        AvailabilityText.Text = Said(LiveTemplates.Availability(shown).Availability);
        IReadOnlyList<TemplateVariable> variables = shown.VariablesInText();
        variablesSaid.Text = variables.Count == 0 ? "No variables" : string.Join(", ", variables.Select(v => v.Name));
        EditVariablesButton.IsEnabled = variables.Count > 0;
        MnemonicBox.IsEnabled = LiveTemplates.Availability(shown).Use != TemplateUse.Generation;
        DeleteButton.IsEnabled = !shown.BuiltIn;
        ToolTip.SetTip(DeleteButton, shown.BuiltIn ? "A built-in template is turned off by its check box, so it can be turned on again" : "Delete Template");
        RestoreButton.IsEnabled = shown.BuiltIn && LiveTemplates.BuiltIn.FirstOrDefault(t => t.Key == shown.Key) is { } shipped && !shipped.SameAs(shown);
    }

    /// <summary>A list row: the check box that turns the template off or on, its shortcut in bold and its description.</summary>
    private Control RowView(LiveTemplate template)
    {
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        CheckBox enabled = new() { IsChecked = template.Enabled, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(enabled, template.Enabled ? "On: turn it off" : "Off: turn it on");
        enabled.IsCheckedChanged += (_, _) => Turn(template.Key, enabled.IsChecked == true);
        line.Children.Add(enabled);
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        string title = template.Abbreviation.Length > 0 ? template.Abbreviation : template.Description.Length > 0 ? template.Description : "<No Name>";
        name.Inlines!.Add(new Avalonia.Controls.Documents.Run(title) { FontWeight = FontWeight.Bold });
        if (template.Abbreviation.Length > 0 && template.Description.Length > 0)
        {
            name.Inlines.Add(new Avalonia.Controls.Documents.Run($" ({template.Description})"));
        }
        if (!template.Enabled)
        {
            name.Classes.Add("muted");
        }
        line.Children.Add(name);
        return line;
    }

    /// <summary>Turns a template on or off, as its check box says.</summary>
    private void Turn(string key, bool on)
    {
        int at = templates.FindIndex(t => t.Key == key);
        if (at < 0 || templates[at].Enabled == on)
        {
            return;
        }
        templates[at] = templates[at] with { Enabled = on };
        Changed();
        string? chosen = Chosen?.Key;
        showing = true;
        try
        {
            List();
            list.SelectedItem = Rows.FirstOrDefault(t => t.Key == chosen);
        }
        finally
        {
            showing = false;
        }
        if (Chosen is { } shown)
        {
            ShowState(shown);
        }
    }

    private static Control Row(string label, Control content) => FormRows.Of(label, content);

    private static Control Docked(Control control, Dock dock)
    {
        SetDock(control, dock);
        return control;
    }

    private static TextBox Field() => new() { Classes = { "search" } };
}
