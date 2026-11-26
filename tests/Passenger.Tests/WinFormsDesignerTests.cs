using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.State;
using Passenger.App.ToolWindows;
using Passenger.App.WinForms;
using Passenger.Core;
using Passenger.Editor;
using static Passenger.Tests.XamlTests;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>Windows Forms' designer through the window: tests/Debuggees/FormsTreeApp's form (a menu, a button, labels, a
/// name box, a panel and a list) written into a folder of its own with a project file for the tests' own target. Its code
/// file and its designer file show its design by the form layout, drawn on Windows by Windows Forms itself from the
/// designer code as it is typed, and elsewhere saying why nothing draws it; its edits (the Toolbox, the arrows, Delete,
/// the Structure window's properties and events, (Name)) written into the code as the designer writes it, Undo taking each
/// back.</summary>
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
        Flow.StepApart("the structure window lists the components and a property typed or reset there is written", () => new WinFormsDesignerTests(app), t => t.The_structure_window_lists_the_components_and_a_property_typed_or_reset_there_is_written());
        Flow.StepApart("a double click goes to the default events handler and an event chosen is hooked and declared in the code", () => new WinFormsDesignerTests(app), t => t.A_double_click_goes_to_the_default_events_handler_and_an_event_chosen_is_hooked_and_declared_in_the_code());
        Flow.StepApart("a name given as (Name) renames the components field its uses its Name and its section", () => new WinFormsDesignerTests(app), t => t.A_name_given_as_Name_renames_the_components_field_its_uses_its_Name_and_its_section());
        Flow.StepApart("only what the code sets is bold and a values part a flag and a colour are written", () => new WinFormsDesignerTests(app), t => t.Only_what_the_code_sets_is_bold_and_a_values_part_a_flag_and_a_colour_are_written());
        Flow.StepApart("a timer from the toolbox goes to the tray and is chosen there", () => new WinFormsDesignerTests(app), t => t.A_timer_from_the_toolbox_goes_to_the_tray_and_is_chosen_there());
        Flow.StepApart("a lists items are written a line an item and a menu item is added under the help menu", () => new WinFormsDesignerTests(app), t => t.A_lists_items_are_written_a_line_an_item_and_a_menu_item_is_added_under_the_help_menu());
        Flow.StepApart("a split container from the toolbox takes a button into its second panel", () => new WinFormsDesignerTests(app), t => t.A_split_container_from_the_toolbox_takes_a_button_into_its_second_panel());
        Flow.StepApart("a tab page chosen in the outline is shown and takes a button", () => new WinFormsDesignerTests(app), t => t.A_tab_page_chosen_in_the_outline_is_shown_and_takes_a_button());
    }

    private void A_lists_items_are_written_a_line_an_item_and_a_menu_item_is_added_under_the_help_menu()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Structure);
        DesignedBox list = Component(pane, "List");
        Ui.Click(pane.Frame, new Point(list.X + (list.Width / 2), list.Y + (list.Height / 2)));
        FormProperties properties = window.FormProperties ?? throw new Xunit.Sdk.XunitException("The Structure window shows no properties under its outline");
        Ui.WaitFor(() => properties.Rows.Any(r => r.Property is { Name: "Items", Lines: not null }), () => $"List's Items to be offered a line an item, not \"{properties.Said}\"");
        Ui.Click(properties.EditorOf("Items") ?? throw new Xunit.Sdk.XunitException("List's Items show no button to edit them"));
        ItemsPopup popup = properties.EditingItems ?? throw new Xunit.Sdk.XunitException("Edit Items opened no popup");
        Ui.WaitFor(() => popup.Lines.IsFocused, "the items' box to take the keys");
        window.KeyTextInput("One");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Two");
        Ui.ClickButton(popup, "OK");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("List.Items.AddRange(new object[] { \"One\", \"Two\" });", StringComparison.Ordinal),
            () => $"List's items to be written, where the status bar says {window.Status}");
        DesignedBox help = Component(pane, "HelpItem");
        Ui.Click(pane.Frame, new Point(help.X + (help.Width / 2), help.Y + (help.Height / 2)));
        Ui.WaitFor(() => properties.EditorOf("DropDownItems") is ComboBox, () => $"HelpItem's DropDownItems to offer items to add, not \"{properties.Said}\", {pane.Chosen?.Field} chosen, "
            + $"its row {properties.Rows.FirstOrDefault(r => r.Name == "DropDownItems")?.Property?.ToString() ?? "missing"} among [{string.Join(", ", properties.Rows.Select(r => r.Name))}]");
        ComboBox adds = (ComboBox)properties.EditorOf("DropDownItems")!;
        Pick(adds, "System.Windows.Forms.ToolStripMenuItem,");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("HelpItem.DropDownItems.AddRange(new ToolStripItem[] { toolStripMenuItem1 });", StringComparison.Ordinal),
            () => $"a menu item to be added under Help, where the status bar says \"{window.Status}\", the design \"{pane.Design.Status}\" ({string.Join(" | ", pane.Design.Notes)}), "
                + $"{(pane.Design.Editing ? "an edit under way" : "no edit under way")}, the list's choice {adds.SelectedItem ?? "none"}");
        string added = File.ReadAllText(file).ReplaceLineEndings("\n");
        Assert.Contains("        toolStripMenuItem1 = new ToolStripMenuItem();\n", added);
        Assert.Contains("        toolStripMenuItem1.Text = \"toolStripMenuItem1\";\n", added);
        Ui.WaitFor(() => window.StructureView.Rows.Any(r => r is { Name: "toolStripMenuItem1", Depth: 3 }),
            () => $"the new menu item to be outlined under Help, not [{string.Join(", ", window.StructureView.Rows.Select(r => $"{r.Name}@{r.Depth}"))}]");
        Ui.WaitFor(() => pane.Chosen?.Field == "toolStripMenuItem1", () => $"the new menu item to be chosen, not {pane.Chosen?.Field ?? "nothing"}");
        AddMenuItemBeside(window, pane, properties, "HelpItem");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("HelpItem.DropDownItems.AddRange(new ToolStripItem[] { toolStripMenuItem1, toolStripMenuItem2 });", StringComparison.Ordinal),
            () => $"a second menu item to be added after the first, where the status bar says {window.Status}");
        Assert.Equal("toolStripMenuItem2", pane.Chosen?.Field);
        pane.Focus();
        Ui.Settle();
        Assert.True(window.Perform(AppActions.FormMoveUp), "Move Up did not run on the chosen menu item");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("HelpItem.DropDownItems.AddRange(new ToolStripItem[] { toolStripMenuItem2, toolStripMenuItem1 });", StringComparison.Ordinal),
            () => $"Move Up to put the second item first, where the status bar says {window.Status}");
        window.Close();
    }

    /// <summary>Another menu item added under a menu item, from that item's properties.</summary>
    private static void AddMenuItemBeside(MainWindow window, FormDesignPane pane, FormProperties properties, string menu)
    {
        window.StructureView.ChooseRow(r => r.Name == menu);
        ListBox outline = window.StructureView.GetVisualDescendants().OfType<ListBox>().First();
        Ui.Click((Control)outline.ContainerFromItem(window.StructureView.Rows.First(r => r.Name == menu))!);
        Ui.WaitFor(() => properties.EditorOf("DropDownItems") is ComboBox, () => $"{menu}'s DropDownItems to offer items to add, {pane.Chosen?.Field} chosen");
        Pick((ComboBox)properties.EditorOf("DropDownItems")!, "System.Windows.Forms.ToolStripMenuItem,");
    }

    /// <summary>A drop-down's item chosen by the start of its words, as a person's choice from its list sets it.</summary>
    private static void Pick(ComboBox box, string start)
    {
        object item = box.Items.Cast<object>().FirstOrDefault(i => i is string words && words.StartsWith(start, StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"The list offers nothing of {start}: [{string.Join(", ", box.Items.Cast<object>())}]");
        box.SelectedItem = item;
        Ui.Settle();
    }

    private void A_split_container_from_the_toolbox_takes_a_button_into_its_second_panel()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Structure);
        window.ShowToolWindow(ToolWindowIds.Toolbox);
        AddFromToolbox(window, "SplitContainer");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("splitContainer1 = new SplitContainer();", StringComparison.Ordinal), () => $"the SplitContainer to be written, where the status bar says {window.Status}");
        string written = File.ReadAllText(file).ReplaceLineEndings("\n");
        Assert.True(written.Contains("        ((System.ComponentModel.ISupportInitialize)splitContainer1).BeginInit();\n", StringComparison.Ordinal),
            $"The SplitContainer's set-up was not begun as the SDK's designer begins it, its lines: [{string.Join(" | ", written.Split('\n').Where(l => l.Contains("splitContainer1", StringComparison.Ordinal)))}]");
        Ui.WaitFor(() => pane.Design.Components.Any(c => c is { Field: "splitContainer1.Panel2", Holds: true, Parent: "splitContainer1" }),
            () => $"the SplitContainer's second panel to be drawn as a place to hold controls, not [{string.Join(", ", pane.Design.Components.Select(c => c.Field))}]");
        DesignedBox panel = Component(pane, "splitContainer1.Panel2");
        Ui.Click(pane.Frame, new Point(panel.X + (panel.Width / 2), panel.Y + (panel.Height / 2)));
        Assert.Equal("splitContainer1.Panel2", pane.Chosen?.Field);
        AddFromToolbox(window, "Button");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("splitContainer1.Panel2.Controls.Add(button1);", StringComparison.Ordinal),
            () => $"the Button to be written into the second panel, where the status bar says {window.Status}");
        Ui.WaitFor(() => pane.Design.Components.Any(c => c is { Field: "button1", Parent: "splitContainer1.Panel2" }), "the Button to be drawn in the second panel");
        window.Close();
    }

    private void A_tab_page_chosen_in_the_outline_is_shown_and_takes_a_button()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Structure);
        AddFromToolbox(window, "TabControl");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("tabControl1.Controls.Add(tabPage2);", StringComparison.Ordinal), () => $"the TabControl and its two pages to be written, where the status bar says {window.Status}");
        Ui.WaitFor(() => pane.Design.Components.Any(c => c is { Field: "tabPage2", Width: 0 }), "the second page to be outlined, not shown");
        window.ShowToolWindow(ToolWindowIds.Structure);
        Ui.WaitFor(() => window.StructureView.Rows.Any(r => r.Name == "tabPage2"), "the Structure window to outline the second page");
        ListBox outline = window.StructureView.GetVisualDescendants().OfType<ListBox>().First();
        Ui.Click((Control)outline.ContainerFromItem(window.StructureView.Rows.First(r => r.Name == "tabPage2"))!);
        Ui.WaitFor(() => pane.Design.Components.Any(c => c is { Field: "tabPage2", Width: > 0 }) && pane.Chosen?.Field == "tabPage2",
            () => $"the second page to be shown and chosen, not {pane.Chosen?.Field ?? "nothing"}");
        AddFromToolbox(window, "Button");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("tabPage2.Controls.Add(button1);", StringComparison.Ordinal),
            () => $"the Button to be written into the second page, where the status bar says {window.Status}");
        window.Close();
    }

    /// <summary>A Toolbox item added by Enter on its row, into the container chosen in the design.</summary>
    private static void AddFromToolbox(MainWindow window, string item)
    {
        window.ShowToolWindow(ToolWindowIds.Toolbox);
        ToolboxView tools = window.ToolboxWindow;
        Ui.WaitFor(() => tools.Rows.Any(r => r.Entry?.Name == "Button"), () => $"the Toolbox to list Windows Forms' controls, not \"{tools.Said}\"");
        tools.Choose(item);
        TreeList list = tools.GetVisualDescendants().OfType<TreeList>().Single();
        Ui.Settle();
        Assert.True(ListKeys.Hold(list), $"The Toolbox's {item} row could not take the keys");
        Ui.Press(window, "ENTER");
    }

    private void Only_what_the_code_sets_is_bold_and_a_values_part_a_flag_and_a_colour_are_written()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        string before = File.ReadAllText(file);
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        DesignedBox count = Component(pane, "Count");
        window.ShowToolWindow(ToolWindowIds.Structure);
        Ui.Click(pane.Frame, new Point(count.X + (count.Width / 2), count.Y + (count.Height / 2)));
        FormProperties properties = window.FormProperties ?? throw new Xunit.Sdk.XunitException("The Structure window shows no properties under its outline");
        Ui.WaitFor(() => properties.Rows.Any(r => r.Name == "Location"), () => $"Count's properties to be listed, not \"{properties.Said}\"");
        Assert.True(properties.Rows.First(r => r.Name == "DataBindings").Property is { Set: false }, "(DataBindings) shows set though the code sets nothing of it");
        Assert.True(properties.Rows.First(r => r.Name == "Text").Property is { Set: true }, "Count's Text shows unset though the code sets it");
        properties.Toggle(properties.Rows.First(r => r.Name == "Location"));
        Assert.Equal(["Location.X", "Location.Y"], properties.Rows.Where(r => r.Name.StartsWith("Location.", StringComparison.Ordinal)).Select(r => r.Name));
        Type(window, properties, "Location.X", "20");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("Count.Location = new Point(20, 36);", StringComparison.Ordinal), () => $"Location's X to be written, where the status bar says {window.Status}");
        Ui.WaitFor(() => properties.Rows.Any(r => r.Property is { Name: "Location", Text: "20, 36" }), "the properties to show Count's new Location");
        properties.Choose("Anchor");
        Ui.Settle();
        Ui.WaitFor(() => properties.EditorOf("Anchor") is WrapPanel, "Anchor's flags to show as toggles");
        WrapPanel anchor = (WrapPanel)properties.EditorOf("Anchor")!;
        Assert.Equal(["Top", "Left"], anchor.Children.OfType<ToggleButton>().Where(t => t.IsChecked == true).Select(t => (string)t.Content!));
        ToggleButton bottom = anchor.Children.OfType<ToggleButton>().First(t => (string?)t.Content == "Bottom");
        Ui.Click(bottom);
        string? said = window.Status;
        Ui.WaitFor(() => File.ReadAllText(file).Contains("Count.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;", StringComparison.Ordinal),
            () => $"Anchor's Bottom to be written with Top and Left, where the status bar said \"{said}\" as it was pressed and says \"{window.Status}\" now, Bottom "
                + $"{(bottom.IsChecked == true ? "on" : "off")}, Anchor {properties.Rows.FirstOrDefault(r => r.Name == "Anchor")?.Property?.Text}");
        Ui.WaitFor(() => properties.Rows.Any(r => r.Property is { Name: "Anchor", Text: "Top, Bottom, Left" }), "the properties to show Count's new Anchor");
        Assert.IsType<Button>(properties.EditorOf("BackColor"));
        string? typed = Type(window, properties, "BackColor", "255, 128, 1");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("Count.BackColor = ", StringComparison.Ordinal),
            () => $"BackColor to be written, where the status bar said \"{typed}\" as it was typed, its row reading {properties.Rows.FirstOrDefault(r => r.Name == "BackColor")?.Property?.Text}");
        Assert.Equal("        Count.BackColor = Color.FromArgb(255, 128, 1);", File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n').First(l => l.Contains("Count.BackColor", StringComparison.Ordinal)));
        // A button given a colour of its own stops drawing the system's, as Windows Forms has it: that line is written again too.
        Assert.Equal(["        Count.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;", "        Count.BackColor = Color.FromArgb(255, 128, 1);",
            "        Count.Location = new Point(20, 36);", "        Count.UseVisualStyleBackColor = false;"],
            File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n').Except(before.ReplaceLineEndings("\n").Split('\n')));
        window.Close();
    }

    private void A_timer_from_the_toolbox_goes_to_the_tray_and_is_chosen_there()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Structure);
        window.ShowToolWindow(ToolWindowIds.Toolbox);
        ToolboxView tools = window.ToolboxWindow;
        Ui.WaitFor(() => tools.Rows.Any(r => r.Entry?.Name == "Button"), () => $"the Toolbox to list Windows Forms' controls, not \"{tools.Said}\"");
        tools.Choose("Timer");
        TreeList list = tools.GetVisualDescendants().OfType<TreeList>().Single();
        Ui.Settle();
        Assert.True(ListKeys.Hold(list), "The Toolbox's Timer row could not take the keys");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("timer1 = new System.Windows.Forms.Timer(components);", StringComparison.Ordinal),
            () => $"the Timer to be written by its full name, Timer being System.Threading's too, where the status bar says {window.Status}");
        string added = File.ReadAllText(file).ReplaceLineEndings("\n");
        Assert.Contains("        components = new System.ComponentModel.Container();\n", added);
        Assert.Contains("    private System.Windows.Forms.Timer timer1;\n", added);
        Ui.WaitFor(() => pane.Chosen?.Field == "timer1", () => $"the Timer to be chosen, not {pane.Chosen?.Field ?? "nothing"}");
        ToggleButton held = pane.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Tag as string == "timer1");
        Assert.True(held.IsChecked == true && held.IsEffectivelyVisible, "The Timer is not shown chosen in the component tray");
        Ui.WaitFor(() => window.FormProperties?.Rows.Any(r => r.Name == "Interval") == true, () => $"the Timer's properties to be listed, not \"{window.FormProperties?.Said}\"");
        window.ShowToolWindow(ToolWindowIds.Structure);
        Ui.WaitFor(() => window.StructureView.Rows.Any(r => r is { Name: "timer1", Depth: 1 }),
            () => $"the Structure window to outline the Timer under the form, not [{string.Join(", ", window.StructureView.Rows.Select(r => $"{r.Name}@{r.Depth}"))}]");
        window.Close();
    }

    /// <summary>A property's row given the keys, its value typed and Enter pressed to write it; what the status bar says then.</summary>
    private static string? Type(MainWindow window, FormProperties properties, string property, string text)
    {
        properties.Choose(property);
        ListBox rows = properties.GetVisualDescendants().OfType<ListBox>().First();
        Keyed(rows, properties.Chosen!);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => properties.Typing is { IsFocused: true }, $"Enter to open {property} to type");
        window.KeyTextInput(text);
        Assert.Equal(text, properties.Typing?.Text);
        Ui.Press(window, "ENTER");
        return window.Status;
    }

    private void The_structure_window_lists_the_components_and_a_property_typed_or_reset_there_is_written()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string file = app.PathOf("MainForm.Designer.cs");
        string before = File.ReadAllText(file);
        MainWindow window = app.Open(app.PathOf("MainForm.cs"), languageServer: false);
        FormDesignPane pane = Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Structure);
        StructureView structure = window.StructureView;
        Ui.WaitFor(() => structure.Rows.Any(r => r.Name == "Inside" && r.Depth == 2), () => $"the Structure window to outline the form's components, not [{string.Join(", ", structure.Rows.Select(r => $"{r.Name}{r.Detail}@{r.Depth}"))}]");
        Assert.Equal(("MainForm", ": Form", 0), (structure.Rows[0].Name, structure.Rows[0].Detail, structure.Rows[0].Depth));
        ListBox outline = structure.GetVisualDescendants().OfType<ListBox>().First();
        Ui.Click((Control)outline.ContainerFromItem(structure.Rows.First(r => r.Name == "Count"))!);
        Assert.Equal("Count", pane.Chosen?.Field);
        FormProperties properties = window.FormProperties ?? throw new Xunit.Sdk.XunitException("The Structure window shows no properties under its outline");
        Ui.WaitFor(() => properties.Rows.Any(r => r.Name == "Text"), () => $"Count's properties to be listed, not \"{properties.Said}\"");
        Assert.Contains("(Name)", properties.Rows.TakeWhile(r => r.Property!.Parenthesized).Select(r => r.Shown));
        Assert.True(properties.Rows.First(r => r.Name == "Text").Property is { Text: "Count", Set: true }, "Count's Text is not shown as the code sets it");
        Assert.True(properties.Rows.First(r => r.Name == "Dock").Property is { Set: false, Exclusive: true, Choices.Count: > 1 }, "Count's Dock offers no choices at its default");
        properties.Choose("Text");
        ListBox rows = properties.GetVisualDescendants().OfType<ListBox>().First();
        Keyed(rows, properties.Chosen!);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => properties.Typing is { IsFocused: true }, "Enter to open Text's value to type");
        window.KeyTextInput("Add");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => File.ReadAllText(file).Contains("Count.Text = \"Add\";", StringComparison.Ordinal), () => $"the typed Text to be written, where the status bar says {window.Status}");
        Assert.Equal(before.Replace("Count.Text = \"Count\";", "Count.Text = \"Add\";", StringComparison.Ordinal), File.ReadAllText(file));
        Ui.WaitFor(() => properties.Rows.Any(r => r.Property is { Name: "Text", Text: "Add" }), "the properties to show Count's new Text");
        properties.Choose("Text");
        Ui.ChooseFromMenu(rows, "Reset");
        Ui.WaitFor(() => !File.ReadAllText(file).Contains("Count.Text", StringComparison.Ordinal), () => $"Reset to take Count's Text line out, where the status bar says {window.Status}");
        Assert.Equal(before.Replace("        Count.Text = \"Count\";\n", string.Empty, StringComparison.Ordinal).ReplaceLineEndings("\n"), File.ReadAllText(file).ReplaceLineEndings("\n"));
        window.Close();
    }

    private void A_double_click_goes_to_the_default_events_handler_and_an_event_chosen_is_hooked_and_declared_in_the_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string designer = app.PathOf("MainForm.Designer.cs");
        string code = app.PathOf("MainForm.cs");
        (string designed, string written) before = (File.ReadAllText(designer), File.ReadAllText(code));
        MainWindow window = app.Open(code, languageServer: false);
        FormDesignPane pane = Drawn(window);
        DesignedBox count = Component(pane, "Count");
        Ui.DoubleClick(pane.Frame, new Point(count.X + (count.Width / 2), count.Y + (count.Height / 2)));
        Ui.WaitFor(() => window.EditorArea.FormLayout.View == PaneView.Editor, () => $"a double click on Count to show its handler's code, where the status bar says {window.Status}");
        int onCount = Array.FindIndex(before.written.Split('\n'), l => l.Contains("void OnCount(", StringComparison.Ordinal));
        Assert.Equal(onCount + 2, window.EditorArea.CodeEditor.CaretPosition.Line);
        Assert.Equal(before, (File.ReadAllText(designer), File.ReadAllText(code)));
        Ui.Press(window, "shift F7");
        pane = Drawn(window);
        window.ShowToolWindow(ToolWindowIds.Structure);
        Ui.Click(pane.Frame, new Point(count.X + (count.Width / 2), count.Y + (count.Height / 2)));
        FormProperties properties = window.FormProperties ?? throw new Xunit.Sdk.XunitException("The Structure window shows no properties under its outline");
        properties.ShowsEvents = true;
        Ui.WaitFor(() => properties.Rows.Any(r => r.Name == "MouseEnter"), () => $"Count's events to be listed, not \"{properties.Said}\"");
        Assert.Equal("OnCount", properties.Rows.First(r => r.Name == "Click").Event?.Handler);
        properties.Choose("MouseEnter");
        ListBox rows = properties.GetVisualDescendants().OfType<ListBox>().First();
        Keyed(rows, properties.Chosen!);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => File.ReadAllText(designer).Contains("Count.MouseEnter += Count_MouseEnter;", StringComparison.Ordinal), () => $"MouseEnter's handler to be hooked, where the status bar says {window.Status}");
        Assert.Contains("        Count.Click += OnCount;\n        Count.MouseEnter += Count_MouseEnter;\n", File.ReadAllText(designer).ReplaceLineEndings("\n"));
        string handled = window.EditorArea.TextOf(code)!.ToString().ReplaceLineEndings("\n");
        Assert.True(handled.Contains("    private void Count_MouseEnter(object? sender, EventArgs e)\n    {\n\n    }\n}", StringComparison.Ordinal),
            $"MouseEnter's handler is not declared at MainForm's end, which reads:\n{handled[Math.Max(0, handled.Length - 400)..]}");
        Ui.WaitFor(() => window.EditorArea.FormLayout.View == PaneView.Editor, "the new handler's code to be shown");
        window.Close();
    }

    private void A_name_given_as_Name_renames_the_components_field_its_uses_its_Name_and_its_section()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        string designer = app.PathOf("MainForm.Designer.cs");
        string code = app.PathOf("MainForm.cs");
        MainWindow window = app.Open(code);
        FormDesignPane pane = Drawn(window);
        DesignedBox counter = Component(pane, "Counter");
        window.ShowToolWindow(ToolWindowIds.Structure);
        Ui.Click(pane.Frame, new Point(counter.X + 2, counter.Y + 2));
        FormProperties properties = window.FormProperties ?? throw new Xunit.Sdk.XunitException("The Structure window shows no properties under its outline");
        Ui.WaitFor(() => properties.Rows.Any(r => r.Naming && r.Property!.Text == "Counter"), () => $"Counter's (Name) to be listed, not \"{properties.Said}\"");
        properties.Choose("Name");
        ListBox rows = properties.GetVisualDescendants().OfType<ListBox>().First();
        Keyed(rows, properties.Chosen!);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => properties.Typing is { IsFocused: true }, "Enter to open (Name) to type");
        window.KeyTextInput("Tally");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.EditorArea.TextOf(code)?.ToString().Contains("Tally.Text = clicks == 1", StringComparison.Ordinal) == true,
            () => $"the rename to reach the form's code, where the status bar says {window.Status}", TimeSpan.FromMinutes(3));
        string renamed = (window.EditorArea.TextOf(designer)?.ToString() ?? File.ReadAllText(designer)).ReplaceLineEndings("\n");
        Assert.Contains("        // \n        // Tally\n        // \n        Tally.AutoSize = true;", renamed);
        Assert.Contains("        Tally.Name = \"Tally\";\n", renamed);
        Assert.Contains("        Controls.Add(Tally);\n", renamed);
        Assert.DoesNotContain("Counter", renamed, StringComparison.Ordinal);
        Ui.WaitFor(() => pane.Chosen?.Field == "Tally", () => $"the renamed component to stay chosen, not {pane.Chosen?.Field ?? "nothing"}");
        window.Close();
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

    /// <summary>A list given the keys with its row chosen, scrolled into view, as a click on it leaves them.</summary>
    private static void Keyed(ListBox list, object row)
    {
        Assert.Same(row, list.SelectedItem);
        Assert.True(ListKeys.Hold(list), $"The row of {row} could not take the keys ({(list.IsEffectivelyVisible ? $"its list shown at {list.Bounds}" : "its list not shown")})");
        Ui.Settle();
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
