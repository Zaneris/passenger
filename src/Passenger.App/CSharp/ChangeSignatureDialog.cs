using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Editor;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

/// <summary>Change Signature: the method's name and return type, its parameters in a table of their modifier, type,
/// name and default value, which Add, Remove, Move Up and Move Down change, and the signature as it will read. A new
/// parameter's default value is what every call passes for it; a kept one's is its declaration's.</summary>
public sealed class ChangeSignatureDialog : RefactoringDialog
{
    private const string Columns = "110,*,*,*";
    private static readonly string[] ModifierChoices = ["", "ref", "out", "in", "params"];

    private readonly Signature signature;
    private readonly Func<string, string, IReadOnlyList<ParameterChange>, Task<string?>> make;
    private readonly StackPanel table = new();
    private readonly List<ParameterRow> rows = [];
    private readonly TextBlock preview = new() { FontFamily = Fonts.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private ParameterRow? chosen;

    public ChangeSignatureDialog(Signature signature, Func<string, string, IReadOnlyList<ParameterChange>, Task<string?>> make)
        : base("Change Signature", "Define signature changes",
            "Specify a new name, return type, and parameters, then ensure the signature preview shows the correct signature", 720)
    {
        this.signature = signature;
        this.make = make;
        bool method = signature.Kind == "method";
        NameBox = Field(signature.Name);
        NameBox.IsEnabled = method;
        ReturnTypeBox = Field(method ? signature.ReturnType : string.Empty);
        ReturnTypeBox.IsEnabled = method;
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

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(0, 0, 0, 1) };
        string[] titles = ["Modifier", "Type", "Name", "Default value"];
        for (int i = 0; i < titles.Length; i++)
        {
            TextBlock title = new() { Text = titles[i], Classes = { "muted" }, Margin = new Thickness(8, 5) };
            Grid.SetColumn(title, i);
            header.Children.Add(title);
        }
        Border headerFrame = new() { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerFrame.Bind(Border.BorderBrushProperty, headerFrame.GetResourceObservable(TokenKeys.Separator));
        Border tableFrame = new()
        {
            Classes = { "framed" },
            ClipToBounds = true,
            Child = new DockPanel { Children = { Docked(headerFrame, Dock.Top), new ScrollViewer { Content = table, MaxHeight = 220, MinHeight = 120 } } },
        };

        Border previewFrame = new() { Classes = { "framed" }, Padding = new Thickness(10, 8), Child = preview };
        previewFrame.Bind(Border.BackgroundProperty, previewFrame.GetResourceObservable(TokenKeys.Editor_Background));

        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            Parameter parameter = signature.Parameters[i];
            rows.Add(MakeRow(i, parameter.Modifier, parameter.Type, parameter.Name, parameter.DefaultValue));
        }
        Options = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Row("Name:", NameBox),
                Row("Return type:", ReturnTypeBox),
                new DockPanel
                {
                    Children =
                    {
                        Docked(new TextBlock { Text = "Parameters:", VerticalAlignment = VerticalAlignment.Center }, Dock.Left),
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, Children = { AddButton, RemoveButton, UpButton, DownButton } },
                    },
                },
                tableFrame,
                new TextBlock { Text = "Signature preview:" },
                previewFrame,
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
            if (each == row)
            {
                each.View.Bind(Border.BackgroundProperty, each.View.GetResourceObservable(TokenKeys.RowSelected));
            }
            else
            {
                each.View.Background = Brushes.Transparent;
            }
        }
        Update();
    }

    protected override string? Check()
    {
        bool method = signature.Kind == "method";
        if (method && !IsName(NameBox.Text))
        {
            return string.IsNullOrWhiteSpace(NameBox.Text) ? "Method name can not be empty" : $"'{NameBox.Text}' is not a valid method name";
        }
        if (method && !IsType(ReturnTypeBox.Text))
        {
            return "The return type is invalid";
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ParameterRow row in rows)
        {
            string name = row.NameBox.Text?.Trim() ?? string.Empty;
            if (!IsType(row.TypeBox.Text))
            {
                return "A parameter has an invalid type";
            }
            if (name.Length == 0)
            {
                return "Parameter name can not be empty";
            }
            if (!IsName(name))
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

    protected override Task<string?> MakeAsync() => make(NameBox.Text?.Trim() ?? string.Empty, ReturnTypeBox.Text?.Trim() ?? string.Empty,
        [.. rows.Select(r => new ParameterChange(r.From, r.Modifier, r.TypeBox.Text?.Trim() ?? string.Empty, r.NameBox.Text?.Trim() ?? string.Empty,
            r.From >= 0 ? r.DefaultBox.Text?.Trim() ?? string.Empty : string.Empty,
            r.From < 0 ? r.DefaultBox.Text?.Trim() ?? string.Empty : string.Empty))]);

    private static bool IsName(string? text) => text is { } name && SyntaxFacts.IsValidIdentifier(name.Trim()) && SyntaxFacts.GetKeywordKind(name.Trim()) == SyntaxKind.None;

    private static bool IsType(string? text) => !string.IsNullOrWhiteSpace(text) && !SyntaxFactory.ParseTypeName(text.Trim()).ContainsDiagnostics;

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
            string text = $"{r.Modifier} {r.TypeBox.Text?.Trim()} {r.NameBox.Text?.Trim()}".Trim();
            return r.From >= 0 && r.DefaultBox.Text?.Trim() is { Length: > 0 } value ? $"{text} = {value}" : text;
        });
        string head = signature.Kind == "method"
            ? $"{signature.Modifiers} {ReturnTypeBox.Text?.Trim()} {NameBox.Text?.Trim()}"
            : $"{signature.Modifiers} {signature.Container}";
        preview.Text = $"{head.Trim()}({string.Join(", ", parameters)})";
        Changed();
    }

    private ParameterRow MakeRow(int from, string modifier, string type, string name, string defaultValue)
    {
        ComboBox modifiers = new() { Classes = { "cell" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        bool receiver = signature.IsExtension && from == 0;
        foreach (string choice in receiver ? [modifier] : ModifierChoices.Contains(modifier) ? ModifierChoices : [.. ModifierChoices, modifier])
        {
            modifiers.Items.Add(choice);
        }
        modifiers.SelectedItem = modifier;
        modifiers.IsEnabled = !receiver;
        TextBox typeBox = Cell(type);
        TextBox nameBox = Cell(name);
        TextBox defaultBox = Cell(defaultValue);
        defaultBox.PlaceholderText = from < 0 ? "Value passed by every call" : null;
        Grid cells = new() { ColumnDefinitions = new ColumnDefinitions(Columns) };
        Control[] parts = [modifiers, typeBox, nameBox, defaultBox];
        for (int i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], i);
            cells.Children.Add(parts[i]);
        }
        Border view = new() { Child = cells };
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

    private static TextBox Cell(string text) => new() { Classes = { "cell" }, Text = text };

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

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
