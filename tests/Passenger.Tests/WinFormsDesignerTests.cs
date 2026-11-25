using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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
/// designer code as it is typed, and elsewhere saying why nothing draws it.</summary>
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
