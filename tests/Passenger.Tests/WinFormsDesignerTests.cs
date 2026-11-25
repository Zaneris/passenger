using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.State;
using Passenger.App.WinForms;
using Passenger.Core;
using Passenger.Editor;
using static Passenger.Tests.XamlTests;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>Windows Forms' designer through the window: tests/Debuggees/FormsTreeApp's form (a menu, a button, labels, a
/// name box, a panel and a list) written into a folder of its own with a project file for the tests' own target. Its code
/// file and its designer file show its design by the form layout, drawn on Windows by Windows Forms itself from the
/// designer code as it is typed, and elsewhere saying why nothing draws it; its edits (the Toolbox, the arrows, Delete)
/// written into the designer file as the designer writes them, Undo taking each back.</summary>
public sealed class WinFormsDesignerTests : IClassFixture<WinFormsDesignerTests.Forms>
{
    private const string OnlyOnWindows = "Windows Forms draws only on Windows, so its designer runs only there";

    private readonly Forms app;

    public WinFormsDesignerTests(Forms app)
    {
        this.app = app;
        app.Reset();
    }

    /// <summary>A form's design: shown alone in place of its code on Windows (its code alone elsewhere, its design there saying
    /// why nothing draws it), View Code and View Designer switching; a hand edit of its designer code drawn, what it cannot
    /// make of a statement said at the statement's line; and a component chosen by a click, then by Tab and Escape.</summary>
    [AvaloniaFact]
    public void A_forms_design_is_drawn_from_its_designer_code_follows_its_edits_and_chooses_its_components()
    {
        Flow.StepApart("a forms code shows its design by the form layout and view code and view designer switch it", () => new WinFormsDesignerTests(app), t => t.A_forms_code_shows_its_design_by_the_form_layout_and_view_code_and_view_designer_switch_it());
        Flow.StepApart("a hand edit of the designer code is drawn and what it cannot draw is said at its line", () => new WinFormsDesignerTests(app), t => t.A_hand_edit_of_the_designer_code_is_drawn_and_what_it_cannot_draw_is_said_at_its_line());
        Flow.StepApart("a click chooses the component under it and tab and escape go from one to another", () => new WinFormsDesignerTests(app), t => t.A_click_chooses_the_component_under_it_and_tab_and_escape_go_from_one_to_another());
        Flow.StepApart("a toolbox item added by enter is written where the designer writes it and undo takes it back", () => new WinFormsDesignerTests(app), t => t.A_toolbox_item_added_by_enter_is_written_where_the_designer_writes_it_and_undo_takes_it_back());
        Flow.StepApart("the arrows move and size the chosen control a unit a line and delete takes it with its lines", () => new WinFormsDesignerTests(app), t => t.The_arrows_move_and_size_the_chosen_control_a_unit_a_line_and_delete_takes_it_with_its_lines());
    }

    private void A_toolbox_item_added_by_enter_is_written_where_the_designer_writes_it_and_undo_takes_it_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        string before = File.ReadAllText(file);
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Toolbox);
        ToolboxView tools = window.ToolboxWindow;
        Ui.WaitFor(() => tools.Rows.Any(r => r.Entry?.Name == "Button"), () => $"the Toolbox to list a Button, not \"{tools.Said}\"");
        Assert.Contains(tools.Rows, r => r.Entry is null && r.Group == "Containers");
        tools.Choose("Button");
        TreeList list = tools.GetVisualDescendants().OfType<TreeList>().Single();
        Ui.Settle();
        Assert.True(list.ContainerFromItem(tools.Rows.First(r => r.Entry?.Name == "Button"))?.Focus() == true, "The Toolbox's Button row could not take the keys");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("button1", StringComparison.Ordinal), () => $"the Button to be written into the designer file, where the status bar says {window.Status}");
        string added = File.ReadAllText(file);
        Assert.Contains("        List = new ListBox();\n        button1 = new Button();\n", added.ReplaceLineEndings("\n"));
        Assert.Contains("        List.TabIndex = 6;\n        // \n        // button1\n        // \n        button1.Location = ", added.ReplaceLineEndings("\n"));
        Assert.Contains("        button1.Name = \"button1\";\n        button1.Size = new Size(75, 23);\n        button1.TabIndex = 7;\n        button1.Text = \"button1\";\n        button1.UseVisualStyleBackColor = true;\n        // \n        // MainForm\n",
            added.ReplaceLineEndings("\n"));
        Assert.Contains("        ClientSize = new Size(480, 280);\n        Controls.Add(button1);\n        Controls.Add(List);\n", added.ReplaceLineEndings("\n"));
        Assert.Contains("    private ListBox List;\n    private Button button1;\n", added.ReplaceLineEndings("\n"));
        Ui.WaitFor(() => pane.Chosen?.Field == "button1", () => $"the new button to be chosen in the design, not {pane.Chosen?.Field ?? "nothing"}");
        pane.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => File.ReadAllText(file) == before, () => $"Undo to take the Button back out of the designer file, where the status bar says {window.Status}");
        Ui.WaitFor(() => pane.Design.Components.All(c => c.Field != "button1"), "the design to draw the form without the Button again");
        window.Close();
    }

    private void The_arrows_move_and_size_the_chosen_control_a_unit_a_line_and_delete_takes_it_with_its_lines()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        string before = File.ReadAllText(file);
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        DesignedBox count = Component(pane, "Count");
        Ui.Click(pane.Frame, new Point(count.X + (count.Width / 2), count.Y + (count.Height / 2)));
        Ui.Press(window, "RIGHT");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("Count.Location = new Point(13, 36);", StringComparison.Ordinal), () => $"Right to move Count a unit, where the status bar says {window.Status}");
        Assert.Equal(before.Replace("Count.Location = new Point(12, 36);", "Count.Location = new Point(13, 36);", StringComparison.Ordinal), File.ReadAllText(file));
        Ui.WaitFor(() => Component(pane, "Count").X == count.X + 1, "the design to draw Count a unit to the right");
        Ui.Press(window, "shift DOWN");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("Count.Size = new Size(100, 31);", StringComparison.Ordinal), () => $"Shift+Down to make Count a unit taller, where the status bar says {window.Status}");
        Ui.WaitFor(() => Component(pane, "Count").Height == count.Height + 1, "the design to draw Count a unit taller");
        Ui.Press(window, "DELETE");
        Ui.WaitFor(() => !System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), @"\bCount\b"), () => $"Delete to take Count's every line out, its field and section too, where the status bar says {window.Status}");
        Assert.Contains("        // \n        // Counter\n", File.ReadAllText(file).ReplaceLineEndings("\n"));
        Ui.WaitFor(() => pane.Design.Components.All(c => c.Field != "Count"), "the design to draw the form without Count");
        for (int i = 0; i < 3; i++)
        {
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
        }
        Ui.WaitFor(() => File.ReadAllText(file) == before, () => $"three Undos to put Count back as it was, where the status bar says {window.Status}");
        window.Close();
    }

    private void A_forms_code_shows_its_design_by_the_form_layout_and_view_code_and_view_designer_switch_it()
    {
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        EditorGroup group = window.EditorArea.ActiveGroup;
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(window.EditorArea.FormLayout.View == PaneView.Editor && group.Side is not FormDesignPane, "A form's code showed its design where nothing draws it");
            Ui.Click(Layout(group, AppActions.FormCodeAndDesign));
            FormDesignPane elsewhere = Assert.IsType<FormDesignPane>(group.Side);
            Assert.Equal(Refusals.FormsDrawOnWindows, elsewhere.Design.Status);
            Assert.Skip(OnlyOnWindows);
        }
        Assert.Equal(PaneView.Preview, window.EditorArea.FormLayout.View);
        FormDesignPane pane = Drawn(window);
        DesignedBox count = Component(pane, "Count");
        Assert.True(count is { Type: "Button", Width: 100, Height: 30, Parent: "" }, $"The button is drawn as {count}");
        Assert.Contains(pane.Design.Components, c => c is { Field: "HelpItem", Type: "ToolStripMenuItem", Parent: "Strip" });
        Ui.Press(window, "F7");
        Assert.True(window.EditorArea.FormLayout.View == PaneView.Editor && group.Side is null, "View Code left the design shown");
        Ui.Press(window, "shift F7");
        Assert.True(window.EditorArea.FormLayout.View == PaneView.Preview && group.Side is FormDesignPane, "View Designer did not show the design");
        window.Close();
    }

    private void A_hand_edit_of_the_designer_code_is_drawn_and_what_it_cannot_draw_is_said_at_its_line()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = app.Open(app.PathOf("MainForm.Designer.cs"), languageServer: false);
        Ui.Click(Layout(window.EditorArea.ActiveGroup, AppActions.FormCodeAndDesign));
        FormDesignPane pane = Drawn(window);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Replace(editor, "Count.Location = new Point(12, 36);", "Count.Location = new Point(40, 36);");
        Ui.WaitFor(() => Component(pane, "Count").X == 40, $"the design to draw the button where the code now puts it, not at {Component(pane, "Count").X}", TimeSpan.FromSeconds(30));
        Replace(editor, "Count.Name = \"Count\";", "Count.Name = \"Count\";\n        Count.Nonsense = 3;");
        int line = Array.FindIndex(editor.Document.Snapshot.ToString().Split('\n'), l => l.Contains("Count.Nonsense", StringComparison.Ordinal)) + 1;
        Ui.WaitFor(() => pane.Design.Notes.Any(n => n.StartsWith($"Line {line}: ", StringComparison.Ordinal) && n.Contains("Nonsense", StringComparison.Ordinal)),
            $"the design to say at line {line} that a button has no Nonsense, not \"{string.Join(" | ", pane.Design.Notes)}\"", TimeSpan.FromSeconds(30));
        Assert.Equal(40, Component(pane, "Count").X);
        window.Close();
    }

    private void A_click_chooses_the_component_under_it_and_tab_and_escape_go_from_one_to_another()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        DesignedBox count = Component(pane, "Count");
        Ui.Click(pane.Frame, new Point(count.X + (count.Width / 2), count.Y + (count.Height / 2)));
        Assert.Equal("Count", pane.Chosen?.Field);
        Assert.True(pane.IsKeyboardFocusWithin, "A click on the design left the keys elsewhere");
        DesignedBox inside = Component(pane, "Inside");
        Ui.Click(pane.Frame, new Point(inside.X + 2, inside.Y + 2));
        Assert.Equal("Inside", pane.Chosen?.Field);
        Ui.Press(window, "ESCAPE");
        Assert.Equal("Bold", pane.Chosen?.Field);
        Ui.Press(window, "TAB");
        Assert.Equal("Inside", pane.Chosen?.Field);
        window.Close();
    }

    /// <summary>A form layout's button on a group's tab strip, by the action it runs.</summary>
    private static ToggleButton Layout(EditorGroup group, string action) =>
        group.TabStrip.GetLogicalDescendants().OfType<ToggleButton>().Single(b => ActionTips.ActionOf(b) == action);

    /// <summary>The design shown beside or in place of the active file, once the designer has drawn it.</summary>
    private static FormDesignPane Drawn(MainWindow window)
    {
        FormDesignPane pane = Assert.IsType<FormDesignPane>(window.EditorArea.ActiveGroup.Side);
        Ui.WaitFor(() => pane.Design.Frame is not null, () => $"the designer to draw the form, not \"{pane.Design.Status}\" ({string.Join(" | ", pane.Design.Notes)})", TimeSpan.FromMinutes(2));
        Assert.True(pane.Design.Notes.Count == 0, $"The designer did not draw all of the form: {string.Join(" | ", pane.Design.Notes)}");
        return pane;
    }

    private static DesignedBox Component(FormDesignPane pane, string field) =>
        pane.Design.Components.FirstOrDefault(c => c.Field == field) ?? throw new Xunit.Sdk.XunitException($"The design has no {field}: {string.Join(", ", pane.Design.Components.Select(c => c.Field))}");

    /// <summary>FormsTreeApp, written into the class's folder and restored where Windows Forms draws it.</summary>
    public sealed class Forms : SharedProject
    {
        public Forms()
            : base("winformsdesigner-")
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("FormsTreeApp")))
            {
                File.Copy(file, PathOf(Path.GetFileName(file)));
            }
            WpfApp.Project(Folder, "FormsTreeApp", forms: true);
        }

        public override async ValueTask InitializeAsync() => await PrepareAsync(restore: OperatingSystem.IsWindows());
    }
}
