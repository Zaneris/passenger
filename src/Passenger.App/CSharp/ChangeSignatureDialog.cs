using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>Change Signature: the method's name and return type, its parameters in a table of their modifier, type,
/// name and default value, which Add, Remove, Move Up and Move Down change, and the signature as it will read. A new
/// parameter's default value is what every call passes for it; a kept one's is its declaration's. A GDScript function's
/// (<see cref="GDScriptKind"/>) has no modifiers, and a type left empty is none.</summary>
public sealed class ChangeSignatureDialog : RefactoringDialog
{
    private const string Columns = "110,*,*,*";

    /// <summary>The parameters' columns' titles, which name their cells for a screen reader too.</summary>
    private static readonly Words[] Titles = ["Modifier", "Type", "Name", "Default value"];

    /// <summary>The signature's kind for a GDScript function.</summary>
    public const string GDScriptKind = "gdscript";

    /// <summary>The signature's kind for a GDScript signal, which returns nothing and whose parameters take no defaults.</summary>
    public const string GDScriptSignalKind = "gdscript signal";
    private static readonly string[] ModifierChoices = ["", "ref", "out", "in", "params"];

    private readonly Signature signature;
    private readonly Func<string, string, IReadOnlyList<ParameterChange>, Task<string?>> make;
    private readonly StackPanel table = new();
    private readonly List<ParameterRow> rows = [];
    private readonly TextBlock preview = new TextBlock { Classes = { StyleClasses.Code }, TextWrapping = TextWrapping.Wrap };
    private ParameterRow? chosen;

    public ChangeSignatureDialog(Signature signature, Func<string, string, IReadOnlyList<ParameterChange>, Task<string?>> make)
        : base("Change Signature", "Define signature changes",
            "Specify a new name, return type, and parameters, then ensure the signature preview shows the correct signature", DialogWidths.Wide)
    {
        this.signature = signature;
        this.make = make;
        bool method = signature.Kind is "method" or GDScriptKind or GDScriptSignalKind;
        NameBox = Field(signature.Name);
        NameBox.IsEnabled = method;
        ReturnTypeBox = Field(method ? signature.ReturnType : string.Empty);
        ReturnTypeBox.IsEnabled = method && signature.Kind != GDScriptSignalKind;
        NameBox.TextChanged += (_, _) => Update();
        ReturnTypeBox.TextChanged += (_, _) => Update();

        AddButton = ToolButtons.Of(IconKeys.Add, "Add");
        RemoveButton = ToolButtons.Of(IconKeys.Minimize, "Remove");
        UpButton = ToolButtons.Of(IconKeys.ArrowUp, "Move Up");
        DownButton = ToolButtons.Of(IconKeys.ArrowDown, "Move Down");
        AddButton.Click += (_, _) => Add();
        RemoveButton.Click += (_, _) => Remove();
        UpButton.Click += (_, _) => Shift(-1);
        DownButton.Click += (_, _) => Shift(1);

        Border tableFrame = new()
        {
            Classes = { StyleClasses.Framed },
            ClipToBounds = true,
            Child = new DockPanel
            {
                Children = { ColumnTitles.Of(Columns, Titles), new ScrollViewer { Content = table, MaxHeight = 220, MinHeight = 120 } },
            },
        };

        Border previewFrame = new() { Classes = { StyleClasses.Framed }, Padding = new Thickness(Gaps.Wide, Gaps.Normal), Child = preview };
        previewFrame.Bind(Border.BackgroundProperty, previewFrame.GetResourceObservable(TokenKeys.Editor_Background));

        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            Parameter parameter = signature.Parameters[i];
            rows.Add(MakeRow(i, parameter.Modifier, parameter.Type, parameter.Name, parameter.DefaultValue));
        }
        Options = new StackPanel
        {
            Spacing = FormRows.Between,
            Children =
            {
                Row("Name", NameBox),
                Row("Return type", ReturnTypeBox),
                Row("Parameters", new StackPanel
                {
                    Spacing = Gaps.Tight,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Hair, HorizontalAlignment = HorizontalAlignment.Right, Children = { AddButton, RemoveButton, UpButton, DownButton } },
                        tableFrame,
                    },
                }, top: true),
                Row("Signature preview", previewFrame, top: true),
            },
        };
        Lay();
        Choose(rows.FirstOrDefault());
        FocusOnOpen(method ? NameBox : (Control?)rows.FirstOrDefault()?.TypeBox ?? AddButton);
    }

    public TextBox NameBox { get; }

    public TextBox ReturnTypeBox { get; }

    public Button AddButton { get; }

    public Button RemoveButton { get; }

    public Button UpButton { get; }

    public Button DownButton { get; }

    /// <summary>The table's rows, in order.</summary>
    public IReadOnlyList<ParameterRow> Parameters => rows;

    /// <summary>The row the buttons act on.</summary>
    public ParameterRow? Chosen => chosen;

    /// <summary>The signature as it will read.</summary>
    public string Preview => preview.Text ?? string.Empty;

    /// <summary>Makes a row the one the buttons act on, as a click or the keys in one of its cells do.</summary>
    public void Choose(ParameterRow? row)
    {
        chosen = row;
        foreach (ParameterRow each in rows)
        {
            each.View.Classes.Set(StyleClasses.Chosen, each == row);
        }
        Update();
    }

    protected override string? Check()
    {
        if (!GDScript)
        {
            return RefactoringChecks.WhyNotSignature(signature, Written(NameBox), Written(ReturnTypeBox), Asked());
        }
        if (!IsGDScriptName(NameBox.Text))
        {
            return string.IsNullOrWhiteSpace(NameBox.Text) ? "Method name can not be empty" : $"'{NameBox.Text}' is not a valid method name";
        }
        if (Written(ReturnTypeBox) != "void" && !IsGDScriptType(ReturnTypeBox.Text) && !string.IsNullOrWhiteSpace(ReturnTypeBox.Text))
        {
            return "The return type is invalid";
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ParameterRow row in rows)
        {
            string name = Written(row.NameBox);
            if (!IsGDScriptType(row.TypeBox.Text) && !string.IsNullOrWhiteSpace(row.TypeBox.Text))
            {
                return "A parameter has an invalid type";
            }
            if (name.Length == 0)
            {
                return "Parameter name can not be empty";
            }
            if (!IsGDScriptName(name))
            {
                return "A parameter has an invalid name";
            }
            if (!names.Add(name))
            {
                return "Parameters should have different names";
            }
        }
        return null;
    }

    protected override Task<string?> MakeAsync() => make(Written(NameBox), Written(ReturnTypeBox), Asked());

    private bool GDScript => signature.Kind is GDScriptKind or GDScriptSignalKind;

    /// <summary>The parameters as the table asks for them: a kept one's value its declaration's default, a new one's what
    /// every call passes.</summary>
    private IReadOnlyList<ParameterChange> Asked() => [.. rows.Select(r => new ParameterChange(r.From, r.Modifier, Written(r.TypeBox), Written(r.NameBox),
        r.From >= 0 ? Written(r.DefaultBox) : string.Empty, r.From < 0 ? Written(r.DefaultBox) : string.Empty))];

    private static string Written(TextBox box) => box.Text?.Trim() ?? string.Empty;

    private static bool IsGDScriptName(string? text) => text is { } name && Passenger.Workspace.GDScript.GDScriptRefactorings.WhyNotName(name.Trim()) is null;

    private static bool IsGDScriptType(string? text) => !string.IsNullOrWhiteSpace(text) && Passenger.Workspace.GDScript.GDScriptRefactorings.IsType(text.Trim());

    /// <summary>A new parameter after the chosen one, its type to type first.</summary>
    private void Add()
    {
        int at = chosen is null ? rows.Count : rows.IndexOf(chosen) + 1;
        ParameterRow row = MakeRow(-1, string.Empty, string.Empty, string.Empty, string.Empty);
        rows.Insert(at, row);
        Lay();
        Choose(row);
        row.TypeBox.Focus();
    }

    private void Remove()
    {
        if (chosen is not { } row || IsReceiver(row))
        {
            return;
        }
        int at = rows.IndexOf(row);
        rows.Remove(row);
        Lay();
        Choose(rows.Count == 0 ? null : rows[Math.Min(at, rows.Count - 1)]);
    }

    private void Shift(int by)
    {
        if (chosen is not { } row)
        {
            return;
        }
        int at = rows.IndexOf(row);
        int to = at + by;
        if (to < 0 || to >= rows.Count || IsReceiver(row) || IsReceiver(rows[to]))
        {
            return;
        }
        rows.RemoveAt(at);
        rows.Insert(to, row);
        Lay();
        Choose(row);
    }

    /// <summary>An extension method's first parameter, the type it extends, which stays first.</summary>
    private bool IsReceiver(ParameterRow row) => signature.IsExtension && row.From == 0;

    private void Lay()
    {
        table.Children.Clear();
        foreach (ParameterRow row in rows)
        {
            table.Children.Add(row.View);
        }
    }

    private void Update()
    {
        int at = chosen is null ? -1 : rows.IndexOf(chosen);
        RemoveButton.IsEnabled = chosen is not null && !IsReceiver(chosen);
        UpButton.IsEnabled = at > 0 && !IsReceiver(chosen!) && !IsReceiver(rows[at - 1]);
        DownButton.IsEnabled = at >= 0 && at < rows.Count - 1 && !IsReceiver(chosen!);
        IEnumerable<string> parameters = rows.Select(r =>
        {
            string text = GDScript ? r.NameBox.Text?.Trim() + (r.TypeBox.Text?.Trim() is { Length: > 0 } typed ? $": {typed}" : string.Empty)
                : $"{r.Modifier} {r.TypeBox.Text?.Trim()} {r.NameBox.Text?.Trim()}".Trim();
            return r.From >= 0 && r.DefaultBox.Text?.Trim() is { Length: > 0 } value ? $"{text} = {value}" : text;
        });
        if (GDScript)
        {
            string returned = ReturnTypeBox.Text?.Trim() is { Length: > 0 } written ? $" -> {written}" : string.Empty;
            preview.Text = signature.Kind == GDScriptSignalKind ? $"signal {NameBox.Text?.Trim()}({string.Join(", ", parameters)})"
                : $"func {NameBox.Text?.Trim()}({string.Join(", ", parameters)}){returned}";
            Changed();
            return;
        }
        string head = signature.Kind == "method"
            ? $"{signature.Modifiers} {ReturnTypeBox.Text?.Trim()} {NameBox.Text?.Trim()}"
            : $"{signature.Modifiers} {signature.Container}";
        preview.Text = $"{head.Trim()}({string.Join(", ", parameters)})";
        Changed();
    }

    private ParameterRow MakeRow(int from, string modifier, string type, string name, string defaultValue)
    {
        ComboBox modifiers = new() { Classes = { StyleClasses.Cell }, HorizontalAlignment = HorizontalAlignment.Stretch };
        bool receiver = signature.IsExtension && from == 0;
        foreach (string choice in receiver ? [modifier] : ModifierChoices.Contains(modifier) ? ModifierChoices : [.. ModifierChoices, modifier])
        {
            modifiers.Items.Add(choice);
        }
        modifiers.SelectedItem = modifier;
        modifiers.IsEnabled = !receiver && !GDScript;
        TextBox typeBox = Cell(type);
        TextBox nameBox = Cell(name);
        TextBox defaultBox = Cell(defaultValue);
        defaultBox.PlaceholderText = from < 0 ? "Value passed by every call" : null;
        Grid cells = new() { ColumnDefinitions = new ColumnDefinitions(Columns) };
        Control[] parts = [modifiers, typeBox, nameBox, defaultBox];
        for (int i = 0; i < parts.Length; i++)
        {
            // A cell is heard by its column's title, the words a person sees over it.
            ShownWords.Set(parts[i], AutomationProperties.NameProperty, Titles[i]);
            Grid.SetColumn(parts[i], i);
            cells.Children.Add(parts[i]);
        }
        Border view = new();
        ChosenMark.Hold(view, cells);
        ParameterRow row = new(from, modifiers, typeBox, nameBox, defaultBox, view);
        view.AddHandler(GotFocusEvent, (_, _) =>
        {
            if (chosen != row)
            {
                Choose(row);
            }
        }, handledEventsToo: true);
        view.AddHandler(PointerPressedEvent, (_, _) => Choose(row), handledEventsToo: true);
        modifiers.SelectionChanged += (_, _) => Update();
        foreach (TextBox box in new[] { typeBox, nameBox, defaultBox })
        {
            box.TextChanged += (_, _) => Update();
        }
        return row;
    }

    private static TextBox Cell(string text) => new() { Classes = { StyleClasses.Cell }, Text = text };

    /// <summary>A parameter's row: the one it was in the old signature (-1 for a new one) and its cells.</summary>
    public sealed class ParameterRow(int from, ComboBox modifierBox, TextBox typeBox, TextBox nameBox, TextBox defaultBox, Border view)
    {
        public int From { get; } = from;

        public ComboBox ModifierBox { get; } = modifierBox;

        public TextBox TypeBox { get; } = typeBox;

        public TextBox NameBox { get; } = nameBox;

        /// <summary>A kept parameter's default value in its declaration; a new one's value at every call.</summary>
        public TextBox DefaultBox { get; } = defaultBox;

        public Border View { get; } = view;

        public string Modifier => ModifierBox.SelectedItem as string ?? string.Empty;
    }
}
