using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Migrations;
using Passenger.App.Packages;
using Passenger.App.Settings;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Packages;
using Passenger.Roslyn.Extension;
using Passenger.Vcs;
using Passenger.Xaml.Features;

namespace Passenger.Tests;

/// <summary>The window as a screen reader's walk meets it: through the automation peers, the one tree Windows' UI
/// Automation, Linux's AT-SPI and macOS's accessibility all read. A walk goes down from a window's peer as a platform's
/// bridge does, finds what it wants by the name a person would hear, and presses it through the peer's own provider, never
/// through the control.</summary>
[AboutMacOS]
public sealed class AccessibilityTests : IDisposable
{
    private readonly string folder = TestFolders.Create("accessibility-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A question, what the window tells, the colour picker, the toolbar's icons and the regions the keys go
    /// round are each named and heard as a screen reader asks, as one flow, each step the check one test made in an
    /// instance of its own.</summary>
    [AvaloniaFact]
    public void What_the_window_tells_and_asks_reaches_a_screen_reader_by_name()
    {
        Flow.StepApart<AccessibilityTests>("a question is a window by its title saying its words and pressing Cancel by name answers it", t => t.A_question_is_a_window_by_its_title_saying_its_words_and_pressing_Cancel_by_name_answers_it());
        Flow.StepApart<AccessibilityTests>("what the window tells is heard once a refusal at once and a progress tick never", t => t.What_the_window_tells_is_heard_once_a_refusal_at_once_and_a_progress_tick_never());
        Flow.StepApart<AccessibilityTests>("the colour picker a swatch opens is a group called colour its hex a named field", t => t.The_colour_picker_a_swatch_opens_is_a_group_called_colour_its_hex_a_named_field());
        Flow.StepApart<AccessibilityTests>("the toolbar and stripes icons are named by what they do their keys left in the tip", t => t.The_toolbar_and_stripes_icons_are_named_by_what_they_do_their_keys_left_in_the_tip());
        Flow.StepApart<AccessibilityTests>("Next and Previous Region take the keys round the window saying each region in every keymap", t => t.Next_and_Previous_Region_take_the_keys_round_the_window_saying_each_region_in_every_keymap());
        Flow.StepApart<AccessibilityTests>("Under titled tabs an edge of hidden tool windows is a region of tabs named as buttons Tab goes along and Enter opens over the editors saying so", t => t.Under_titled_tabs_an_edge_of_hidden_tool_windows_is_a_region_of_tabs_named_as_buttons_Tab_goes_along_and_Enter_opens_over_the_editors_saying_so());
    }

    /// <summary>A tree's rows say their words, level, place and state, telling a reader as they change, and every field
    /// of a form or dialog is named by its label, as one flow, each step in an instance of its own.</summary>
    [AvaloniaFact]
    public void Trees_and_forms_name_each_row_and_field_for_a_screen_reader()
    {
        Flow.StepApart<AccessibilityTests>("an Explorer row is named by its words never by its items type", t => t.An_Explorer_row_is_named_by_its_words_never_by_its_items_type());
        Flow.StepApart<AccessibilityTests>("a trees row says its level its place and whether it is open telling a reader as that changes", t => t.A_trees_row_says_its_level_its_place_and_whether_it_is_open_telling_a_reader_as_that_changes());
        Flow.StepApart<AccessibilityTests>("a trees row reused from a folder for a file answers as a file and tells a reader nothing it cannot ask", t => t.A_trees_row_reused_from_a_folder_for_a_file_answers_as_a_file_and_tells_a_reader_nothing_it_cannot_ask());
        Flow.StepApart<AccessibilityTests>("a settings field is named by its label and the project button by the project", t => t.A_settings_field_is_named_by_its_label_and_the_project_button_by_the_project());
        Flow.StepApart<AccessibilityTests>("every field of a dialogs form is named by the label beside it", t => t.Every_field_of_a_dialogs_form_is_named_by_the_label_beside_it());
        Flow.StepApart<AccessibilityTests>("every field of a refactorings dialog is named by the label beside it", t => t.Every_field_of_a_refactorings_dialog_is_named_by_the_label_beside_it());
    }

    private void A_question_is_a_window_by_its_title_saying_its_words_and_pressing_Cancel_by_name_answers_it()
    {
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "kept\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Task<bool> deleting = window.DeleteAsync(PathNames.Canonical(notes));
            Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(d => d.IsVisible), "the delete's question");
            ConfirmDialog question = window.OwnedWindows.OfType<ConfirmDialog>().Single(d => d.IsVisible);

            AutomationPeer asked = ControlAutomationPeer.CreatePeerForElement(question);
            Assert.True(asked.GetAutomationControlType() == AutomationControlType.Window && asked.GetName() == "Delete",
                $"A screen reader meets the question as a {asked.GetAutomationControlType()} named \"{asked.GetName()}\", not as the window Delete");
            Assert.True(asked.GetHelpText() == "Move notes.txt to the Trash?", $"The question's window tells a screen reader \"{asked.GetHelpText()}\", not what it asks");
            AutomationPeer[] met = [.. Walk(asked)];
            Assert.True(met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.Text && peer.GetName() == "Move notes.txt to the Trash?"),
                $"A walk of the question never reads what it asks, only: {Heard(met)}");
            Assert.True(Button(asked, "Delete") is not null, $"A walk of the question finds no button named Delete, only: {Heard(met)}");

            AutomationPeer cancel = Button(asked, "Cancel") ?? throw new Xunit.Sdk.XunitException($"A walk of the question finds no button named Cancel, only: {Heard(met)}");
            IInvokeProvider press = cancel.GetProvider<IInvokeProvider>() ?? throw new Xunit.Sdk.XunitException("Cancel can be found but not pressed by what reads the window");
            press.Invoke();

            Assert.False(Ui.Await(deleting, "the question's answer"), "Cancel pressed through accessibility did not answer no");
            Assert.True(File.Exists(notes), "Cancel pressed through accessibility still deleted the file");
            Assert.False(question.IsVisible, "The question stays open after Cancel is pressed through accessibility");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What the window tells the person, as a screen reader hears it: the status bar's words are a live region,
    /// and what is told is heard through the window's announcing region, whose name change is the live-region event each
    /// system's bridge passes on: a refusal at once, a work's stage and its end once each, a progress tick never, and a
    /// window that comes without the keys by its title and words.</summary>
    private void What_the_window_tells_is_heard_once_a_refusal_at_once_and_a_progress_tick_never()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            List<(string Words, AutomationLiveSetting Live)> heard = Listen(window);
            AutomationPeer region = ControlAutomationPeer.CreatePeerForElement(Announcer.Of(window)!.Region);
            AutomationPeer[] met = [.. Walk(ControlAutomationPeer.CreatePeerForElement(window))];
            Assert.True(met.Contains(region) && region.GetAutomationControlType() == AutomationControlType.Text,
                $"A walk of the window never meets its announcing region, so no reader hears it: {Heard(met)}");
            AutomationPeer statusWords = met.Single(peer => peer is ControlAutomationPeer { Owner.Name: "StatusText" });
            Assert.True(statusWords.GetLiveSetting() == AutomationLiveSetting.Polite,
                $"The status bar's words read as a {statusWords.GetLiveSetting()} live region, not a polite one");

            string why = window.Actions.WhyNot(AppActions.GoToDeclaration, window.Actions.Context())
                ?? throw new Xunit.Sdk.XunitException("Go to Declaration is not refused with no file open");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => heard.Count > 0, "the refusal heard");
            Assert.True(heard is [(var words, AutomationLiveSetting.Assertive)] && words == why,
                $"Refused, Go to Declaration was heard as {Said(heard)}, not once at once as \"{why}\"");
            Assert.True(statusWords.GetLiveSetting() == AutomationLiveSetting.Assertive, "The status bar's refusal does not read as an assertive live region");

            heard.Clear();
            Working work = window.Actions.Work.Begin("test.export", "Exporting Game: packing (10%)") ?? throw new Xunit.Sdk.XunitException("The work did not begin");
            Ui.Settle();
            work.Say("Exporting Game: packing (40%)");
            Ui.Settle();
            work.Say("Exporting Game: signing");
            work.Tell(false, "Export failed: the signing key is missing");
            work.Dispose();
            Ui.Settle();
            Assert.True(heard.SequenceEqual([("Exporting Game: packing (10%)", AutomationLiveSetting.Polite), ("Exporting Game: signing", AutomationLiveSetting.Polite),
                ("Export failed: the signing key is missing", AutomationLiveSetting.Assertive)]),
                $"A work's stages and end were heard as {Said(heard)}, not each once with the tick left out and the failure at once");

            heard.Clear();
            ConfirmDialog question = new("Delete", "Move notes.txt to the Trash?", "Delete") { ShowActivated = false };
            question.Show(window);
            Ui.Settle();
            question.Close();
            Ui.Settle();
            Assert.True(heard is [("Delete. Move notes.txt to the Trash?", AutomationLiveSetting.Polite)],
                $"A question shown without the keys was heard as {Said(heard)}, not by its title and words");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What a screen reader hears through a window's announcing region from now on, each as it was told, every
    /// settle waiting for what it gathered to be heard.</summary>
    internal static List<(string Words, AutomationLiveSetting Live)> Listen(TopLevel window)
    {
        Announcer announcer = Announcer.Of(window) ?? throw new Xunit.Sdk.XunitException("The window has no announcer");
        Ui.Hears(window, announcer);
        AutomationPeer region = ControlAutomationPeer.CreatePeerForElement(announcer.Region);
        List<(string Words, AutomationLiveSetting Live)> heard = [];
        region.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.NameProperty)
            {
                heard.Add((region.GetName(), region.GetLiveSetting()));
            }
        };
        return heard;
    }

    internal static string Said(IEnumerable<(string Words, AutomationLiveSetting Live)> heard) =>
        heard.Any() ? string.Join(", ", heard.Select(h => $"\"{h.Words}\" ({h.Live})")) : "nothing";

    /// <summary>The colour picker a swatch opens is met as a group called Colour, placed at its line as every popup there
    /// is, its hex a field named so.</summary>
    private void The_colour_picker_a_swatch_opens_is_a_group_called_colour_its_hex_a_named_field()
    {
        string palette = TestFolders.Write(folder, "Palette.cs", "class Palette\n{\n    static object Brand = Avalonia.Media.Color.FromRgb(0x33, 0x66, 0x99);\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(palette);
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument shown = window.EditorArea.Active!;
            Ui.WaitFor(() => shown.ColourMarks.Any(m => m.Line == 2), "the swatch of the colour FromRgb makes");
            Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Marks, 2));
            Ui.Settle();
            Assert.Equal("#336699", window.PickingColour);
            AutomationPeer[] met = [.. Walk(ControlAutomationPeer.CreatePeerForElement(window))];
            AutomationPeer picker = met.FirstOrDefault(peer => peer.GetAutomationControlType() == AutomationControlType.Group && peer.GetName() == "Colour")
                ?? throw new Xunit.Sdk.XunitException($"A walk of the window meets no group called Colour with the picker open, only: {Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.Group))}");
            AutomationPeer[] fields = [.. Walk(picker).Where(peer => peer.GetAutomationControlType() == AutomationControlType.Edit)];
            Assert.True(fields is [{ } hex] && hex.GetName() == "Hex", $"The picker's fields are met as {Heard(fields)}, not one named Hex");
        }
        finally
        {
            window.Close();
        }
    }

    private void The_toolbar_and_stripes_icons_are_named_by_what_they_do_their_keys_left_in_the_tip()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AutomationPeer top = ControlAutomationPeer.CreatePeerForElement(window);
            foreach (string words in (string[])[.. OperatingSystem.IsMacOS() ? (string[])[] : ["Main menu"], "Build solution", "Run", "Debug", "Profile", "Stop", "Search everywhere", "Settings", "Explorer", "Terminal", "Problems"])
            {
                Assert.True(Button(top, words) is not null, $"The window has no button named {words}, only: {Heard(Walk(top).Where(IsButton))}");
            }
            AutomationPeer run = Assert.Single(Walk(top), peer => peer is ControlAutomationPeer { Owner.Name: "RunButton" });
            Assert.True(run.GetName() == "Run" && run.GetHelpText() == ToolTip.GetTip(((ControlAutomationPeer)run).Owner) as string,
                $"The toolbar's Run is named \"{run.GetName()}\" with \"{run.GetHelpText()}\" as its tip");
        }
        finally
        {
            window.Close();
        }
    }

    private void An_Explorer_row_is_named_by_its_words_never_by_its_items_type()
    {
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "kept\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AutomationPeer[] rows = [.. Walk(ControlAutomationPeer.CreatePeerForElement(window)).Where(peer => peer.GetAutomationControlType() == AutomationControlType.TreeItem)];
            Assert.True(rows.Any(row => row.GetName().StartsWith("notes.txt, level 2", StringComparison.Ordinal)), $"No row is named notes.txt with its level, only: {Heard(rows)}");
            Assert.False(rows.Any(row => row.GetName().StartsWith("Passenger.", StringComparison.Ordinal)), $"A row is named by its item's type: {Heard(rows)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A tree's row as a screen reader meets it: a tree item of a tree, named by its words with its level, and its
    /// place among the rows beside it where the system's own bridge does not say it (all but Linux's AT-SPI, which reads the
    /// place the row gives the system); open or closed where rows go under it, neither where none do, telling a reader as
    /// it opens by its key and as the reader itself closes and opens it, its rows then said one level deeper.</summary>
    private void A_trees_row_says_its_level_its_place_and_whether_it_is_open_telling_a_reader_as_that_changes()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Models"));
        foreach (string name in (string[])["Order.cs", "Price.cs", "Stock.cs"])
        {
            File.WriteAllText(Path.Combine(folder, "Models", name), $"// {name}\n");
        }
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes\n");
        File.WriteAllText(Path.Combine(folder, "Readme.md"), "# Readme\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            ExplorerTree model = window.ExplorerView.Tree;
            TreeList tree = window.GetVisualDescendants().OfType<TreeList>().First(list => ReferenceEquals(list.ItemsSource, model.Rows));
            AutomationPeer whole = ControlAutomationPeer.CreatePeerForElement(tree);
            Assert.True(whole.GetAutomationControlType() == AutomationControlType.Tree, $"A screen reader meets the Explorer as a {whole.GetAutomationControlType()}, not a tree");
            ExplorerNode models = model.Rows.Single(r => r.Name == "Models");
            TreeList.TreeRowPeer root = PeerOf(tree, model.Rows[0]);
            RowSays(root, Path.GetFileName(PathNames.Canonical(folder)), 1, 1, 1, ExpandCollapseState.Expanded);
            TreeList.TreeRowPeer folderRow = PeerOf(tree, models);
            RowSays(folderRow, "Models", 2, 1, 3, ExpandCollapseState.Collapsed);
            RowSays(PeerOf(tree, model.Rows.Single(r => r.Name == "Notes.txt")), "Notes.txt", 2, 2, 3, ExpandCollapseState.LeafNode);
            RowSays(PeerOf(tree, model.Rows.Single(r => r.Name == "Readme.md")), "Readme.md", 2, 3, 3, ExpandCollapseState.LeafNode);

            List<ExpandCollapseState> told = [];
            folderRow.PropertyChanged += (_, e) =>
            {
                if (e.Property == ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty && e.NewValue is ExpandCollapseState now)
                {
                    told.Add(now);
                }
            };
            tree.SelectedItem = models;
            tree.ContainerFromItem(models)!.Focus(NavigationMethod.Pointer);
            Ui.Press(window, "Right");
            Ui.Settle();
            Assert.True(models.IsExpanded && told.SequenceEqual([ExpandCollapseState.Expanded]),
                $"Right opened Models ({models.IsExpanded}) and a reader was told {string.Join(", ", told)}, not that it opened");
            RowSays(folderRow, "Models", 2, 1, 3, ExpandCollapseState.Expanded);
            RowSays(PeerOf(tree, model.Rows.Single(r => r.Name == "Order.cs")), "Order.cs", 3, 1, 3, ExpandCollapseState.LeafNode);
            RowSays(PeerOf(tree, model.Rows.Single(r => r.Name == "Stock.cs")), "Stock.cs", 3, 3, 3, ExpandCollapseState.LeafNode);
            RowSays(PeerOf(tree, model.Rows.Single(r => r.Name == "Readme.md")), "Readme.md", 2, 3, 3, ExpandCollapseState.LeafNode);

            IExpandCollapseProvider opens = folderRow.GetProvider<IExpandCollapseProvider>()
                ?? throw new Xunit.Sdk.XunitException("A reader finds no way to open or close the Models row");
            opens.Collapse();
            Ui.Settle();
            Assert.True(!models.IsExpanded && model.Rows.All(r => r.Name != "Order.cs") && told.SequenceEqual([ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed]),
                $"A reader's Collapse left Models open ({models.IsExpanded}) or was told {string.Join(", ", told)}");
            opens.Expand();
            Ui.Settle();
            Assert.True(models.IsExpanded && told[^1] == ExpandCollapseState.Expanded, $"A reader's Expand left Models closed, telling {string.Join(", ", told)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A tree's row reused from a folder for a file, as the list reuses a row a closing took away for one an opening
    /// brought, answers as a row with nothing under it from then on and tells a reader no open state it no longer has, and
    /// back again: on macOS, through the elements Avalonia's bridge makes for VoiceOver, it gives no AXExpanded while it is
    /// a file's, and no question or change of the bridge's throws through the system's call, which ends Passenger; on every
    /// system, each change of a row's open state comes while the row gives the pattern a reader asks it by.</summary>
    private void A_trees_row_reused_from_a_folder_for_a_file_answers_as_a_file_and_tells_a_reader_nothing_it_cannot_ask()
    {
        Directory.CreateDirectory(Path.Combine(folder, "Parts", "Inner"));
        File.WriteAllText(Path.Combine(folder, "Parts", "Inner", "Gear.cs"), "// Gear\n");
        Directory.CreateDirectory(Path.Combine(folder, "Shelf"));
        File.WriteAllText(Path.Combine(folder, "Shelf", "Notes.txt"), "notes\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        using MacBridge.Faults faults = new();
        using MacBridge.Pool? pool = OperatingSystem.IsMacOS() ? new() : null;
        List<string> refused = [];
        Dictionary<Control, nint> elements = [];
        try
        {
            ExplorerTree model = window.ExplorerView.Tree;
            TreeList tree = window.GetVisualDescendants().OfType<TreeList>().First(list => ReferenceEquals(list.ItemsSource, model.Rows));
            ExplorerNode parts = model.Rows.Single(r => r.Name == "Parts");
            ExplorerNode shelf = model.Rows.Single(r => r.Name == "Shelf");
            Key(parts, "Right");
            ExplorerNode inner = model.Rows.Single(r => r.Name == "Inner");
            foreach (Control container in tree.GetRealizedContainers())
            {
                TreeList.TreeRowPeer peer = Assert.IsType<TreeList.TreeRowPeer>(ControlAutomationPeer.CreatePeerForElement(container));
                peer.PropertyChanged += (_, e) =>
                {
                    if (e.Property == ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty && peer.GetProvider<IExpandCollapseProvider>() is null)
                    {
                        refused.Add($"{peer.GetName()}: {e.OldValue} to {e.NewValue}");
                    }
                };
                if (OperatingSystem.IsMacOS())
                {
                    elements[container] = MacBridge.ElementOf(container);
                }
            }
            Control reused = tree.ContainerFromItem(inner) ?? throw new Xunit.Sdk.XunitException("The tree shows no row for Inner");
            AsReaderHears(reused, "Inner", ExpandCollapseState.Collapsed);

            Key(parts, "Left");
            Key(shelf, "Right");
            ExplorerNode notes = model.Rows.Single(r => r.Name == "Notes.txt");
            Assert.True(ReferenceEquals(tree.ContainerFromItem(notes), reused),
                "The row that showed the folder Inner was not reused for the file Notes.txt, so the reuse a reader's question met is not what this holds");
            AsReaderHears(reused, "Notes.txt", ExpandCollapseState.LeafNode);

            Key(shelf, "Left");
            Key(parts, "Right");
            Assert.True(ReferenceEquals(tree.ContainerFromItem(inner), reused), "The row reused for Notes.txt was not reused for Inner again");
            AsReaderHears(reused, "Inner", ExpandCollapseState.Collapsed);
            if (OperatingSystem.IsMacOS())
            {
                MacBridge.Send(elements[reused], "setAccessibilityExpanded:", 1);
                Ui.Settle();
                Assert.True(inner.IsExpanded, "VoiceOver's opening of Inner left it closed");
                AsReaderHears(reused, "Inner", ExpandCollapseState.Expanded);
            }
            Assert.True(faults.Thrown.Length == 0, $"Avalonia's bridge met an exception asking a row, which ends a person's Passenger: {string.Join("; ", faults.Thrown)}");
            Assert.True(refused.Count == 0, $"A row told a reader its open state changed while it gave no way to ask it: {string.Join("; ", refused)}");

            void Key(ExplorerNode row, string key)
            {
                tree.SelectedItem = row;
                tree.ContainerFromItem(row)!.Focus(NavigationMethod.Pointer);
                Ui.Press(window, key);
                Ui.Settle();
            }
        }
        finally
        {
            window.Close();
        }

        void AsReaderHears(Control row, string words, ExpandCollapseState state)
        {
            TreeList.TreeRowPeer peer = Assert.IsType<TreeList.TreeRowPeer>(ControlAutomationPeer.CreatePeerForElement(row));
            Assert.True(peer.GetName().StartsWith(words + ",", StringComparison.Ordinal) && peer.ExpandCollapseState == state
                && (peer.GetProvider<IExpandCollapseProvider>() is null) == (state == ExpandCollapseState.LeafNode),
                $"The row says \"{peer.GetName()}\", {peer.ExpandCollapseState} (opened or closed by a reader: {peer.GetProvider<IExpandCollapseProvider>() is not null}), where it shows {words}, {state}");
            if (elements.TryGetValue(row, out nint element))
            {
                bool asked = MacBridge.Allows(element, "isAccessibilityExpanded");
                bool open = MacBridge.Expanded(element);
                Assert.True(asked == (state != ExpandCollapseState.LeafNode) && open == (state == ExpandCollapseState.Expanded),
                    $"VoiceOver hears {words} {(asked ? open ? "open" : "closed" : "neither open nor closed")}, where it is {state}");
            }
        }
    }

    /// <summary>The peer a reader asks of the row showing <paramref name="item"/>, brought into view.</summary>
    private static TreeList.TreeRowPeer PeerOf(TreeList tree, object item)
    {
        tree.ScrollIntoView(item);
        tree.UpdateLayout();
        Control row = tree.ContainerFromItem(item) ?? throw new Xunit.Sdk.XunitException($"The tree shows no row for {item}");
        return Assert.IsType<TreeList.TreeRowPeer>(ControlAutomationPeer.CreatePeerForElement(row));
    }

    /// <summary>Holds a row's peer to what a reader hears of it and what its system is given.</summary>
    private static void RowSays(TreeList.TreeRowPeer row, string words, int level, int position, int size, ExpandCollapseState state)
    {
        string heard = OperatingSystem.IsLinux() ? $"{words}, level {level}" : $"{words}, level {level}, {position} of {size}";
        Control owner = row.Owner;
        Assert.True(row.GetAutomationControlType() == AutomationControlType.TreeItem && row.GetName() == heard,
            $"A reader meets {words} as a {row.GetAutomationControlType()} named \"{row.GetName()}\", not a tree item named \"{heard}\"");
        Assert.True((row.Level, row.PositionInSet, row.SizeOfSet) == (level, position, size)
            && (AutomationProperties.GetPositionInSet(owner), AutomationProperties.GetSizeOfSet(owner)) == (position, size),
            $"{words} says level {row.Level}, {row.PositionInSet} of {row.SizeOfSet}, and gives the system {AutomationProperties.GetPositionInSet(owner)} of "
            + $"{AutomationProperties.GetSizeOfSet(owner)}, where it stands at level {level}, {position} of {size}");
        Assert.True(row.ExpandCollapseState == state && (row.GetProvider<IExpandCollapseProvider>() is null) == (state == ExpandCollapseState.LeafNode),
            $"{words} says it is {row.ExpandCollapseState} (opened or closed by a reader: {row.GetProvider<IExpandCollapseProvider>() is not null}), not {state}");
    }

    /// <summary>Next Region and Previous Region take the keys round the window's regions in one order, by their own keys in
    /// every keymap: each region told as it is entered, the keys landing inside it on something shown with its focus drawn
    /// (the ring, or a text field's edge), and round again to where they began.</summary>
    private void Next_and_Previous_Region_take_the_keys_round_the_window_saying_each_region_in_every_keymap()
    {
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\ntwo\n");
        TestGit.Init(folder);
        File.WriteAllText(Path.Combine(folder, "Draft.txt"), "draft\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile("Notes.txt");
            Button changes = window.FindControl<Button>("GitChangesButton")!;
            Ui.WaitFor(() => changes.IsEffectivelyVisible, "the status bar's changed files, something there to take the keys");
            DockHost dock = window.DockHost;
            dock.Move(dock[ToolWindowIds.Bookmarks], ToolAnchor.RightTop);
            dock.Show(ToolWindowIds.Bookmarks);
            dock.Show(ToolWindowIds.Problems);
            Ui.Settle();
            string[] shown = [.. dock.Windows.Where(w => w.IsShown && w.Mode == ToolMode.Docked).Select(w => w.Id).Order(StringComparer.Ordinal)];
            Assert.True(shown.SequenceEqual(new[] { ToolWindowIds.Bookmarks, ToolWindowIds.Explorer, ToolWindowIds.Problems }.Order(StringComparer.Ordinal)),
                $"The window shows the tool windows {string.Join(", ", shown)}, not just the Explorer, Bookmarks and Problems this test goes round");
            (string Said, Control Holder)[] round =
            [
                ("Notes.txt, editor", window.EditorArea),
                ($"{dock[ToolWindowIds.Bookmarks].Title}, tool window", dock[ToolWindowIds.Bookmarks].Content),
                ($"{dock[ToolWindowIds.Problems].Title}, tool window", dock[ToolWindowIds.Problems].Content),
                ("Status bar", window.FindControl<Border>("StatusBarFrame")!),
                ("Toolbar", window.FindControl<Border>("Toolbar")!),
                ($"{dock[ToolWindowIds.Explorer].Title}, tool window", dock[ToolWindowIds.Explorer].Content),
            ];
            List<(string Words, AutomationLiveSetting Live)> heard = Listen(window);
            (string Next, string Previous) keys = OperatingSystem.IsMacOS()
                ? ("control meta BACK_QUOTE", "control meta shift BACK_QUOTE")
                : ("control alt BACK_QUOTE", "control alt shift BACK_QUOTE");
            foreach (KeymapPreset preset in (KeymapPreset[])[KeymapPreset.VisualStudio, KeymapPreset.VSCode, KeymapPreset.IntelliJ])
            {
                window.ApplySettings(window.Settings with { Keymap = preset });
                window.EditorArea.CodeEditor.Focus();
                Ui.Settle();
                for (int step = 1; step <= round.Length; step++)
                {
                    Entered(window, keys.Next, round[step % round.Length], heard, $"{preset}'s Next Region");
                }
                for (int step = round.Length - 1; step >= 0; step--)
                {
                    Entered(window, keys.Previous, round[step], heard, $"{preset}'s Previous Region");
                }
            }
            window.EditorArea.CodeEditor.Focus();
            Ui.Settle();
            heard.Clear();
            window.Actions.Run(AppActions.NextRegion);
            window.Actions.Run(AppActions.NextRegion);
            Ui.Settle();
            Assert.True(heard is [(var last, _)] && last == round[2].Said, $"Next Region pressed twice in quick turn was heard as {Said(heard)}, not as the region it came to");
            Assert.True(window.Actions.Find("Next Region").Any(a => a.Id == AppActions.NextRegion) && window.Actions.Find("Previous Region").Any(a => a.Id == AppActions.PreviousRegion),
                "Find Action finds no Next Region and Previous Region");
            Assert.True(window.Actions.WhyNot(AppActions.NextRegion, window.Actions.Context().ForAgent()) == Refusals.PersonsKeys,
                "An agent's ide_action may move the person's keys to another region");
        }
        finally
        {
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            window.Close();
        }
    }

    /// <summary>Presses a region's key and holds the window to having entered <paramref name="region"/>: the keys inside it
    /// on something shown with its focus drawn, and the region told.</summary>
    private static void Entered(MainWindow window, string key, (string Said, Control Holder) region, List<(string Words, AutomationLiveSetting Live)> heard, string what)
    {
        heard.Clear();
        Ui.Press(window, key);
        Ui.Settle();
        Control focused = window.FocusManager?.GetFocusedElement() as Control
            ?? throw new Xunit.Sdk.XunitException($"{what} ({key}) left the keys on nothing, not in the region {region.Said}");
        Assert.True(focused == region.Holder || region.Holder.IsVisualAncestorOf(focused),
            $"{what} ({key}) left the keys on {focused.GetType().Name} ({focused.Name}), not in the region {region.Said}");
        Assert.True(focused.IsEffectivelyVisible && (Ringed(focused) || focused is TextBox { IsFocused: true }),
            $"{what} gave the keys to a {focused.GetType().Name} in {region.Said} that draws nothing to say it has them");
        Assert.True(heard is [(var words, AutomationLiveSetting.Polite)] && words == region.Said, $"{what} into {region.Said} was heard as {Said(heard)}");
    }

    private static bool Ringed(Control focused) => AdornerLayer.GetAdornerLayer(focused) is { } layer
        && layer.Children.OfType<FocusRing>().Any(r => r.IsVisible && AdornerLayer.GetAdornedElement(r) == focused);

    private void Under_titled_tabs_an_edge_of_hidden_tool_windows_is_a_region_of_tabs_named_as_buttons_Tab_goes_along_and_Enter_opens_over_the_editors_saying_so()
    {
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\ntwo\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ApplySettings(window.Settings with { ToolWindowLook = ToolWindowLook.TitledTabs });
            window.OpenFile("Notes.txt");
            Ui.Settle();
            DockHost dock = window.DockHost;
            string[] edge = [.. dock.Windows.Where(w => w.Anchor is ToolAnchor.LeftTop or ToolAnchor.LeftBottom && !w.IsShown && !w.IsClosed && w.Mode == ToolMode.Docked
                && !(w.Anchor == dock[ToolWindowIds.Explorer].Anchor && dock.IsShown(ToolWindowIds.Explorer))).OrderBy(w => w.Anchor).ThenBy(w => w.Order).Select(w => w.Id)];
            Assert.True(edge.Length >= 2, $"The left edge names {string.Join(", ", edge)}, too few to Tab between");
            List<(string Words, AutomationLiveSetting Live)> heard = Listen(window);
            window.EditorArea.CodeEditor.Focus();
            Ui.Settle();
            string previous = OperatingSystem.IsMacOS() ? "control meta shift BACK_QUOTE" : "control alt shift BACK_QUOTE";
            Ui.Press(window, previous);
            Ui.Press(window, previous);
            Ui.Settle();
            Control first = window.FocusManager?.GetFocusedElement() as Control ?? throw new Xunit.Sdk.XunitException("Previous Region twice from the editor left the keys on nothing");
            Assert.True(Equals(first.Tag, edge[0]) && Ringed(first) && heard.Any(h => h.Words == "Hidden tool windows, left edge"),
                $"Previous Region twice from the editor came to {first.GetType().Name} {first.Tag}, ringed {Ringed(first)}, heard as {Said(heard)}, not to {edge[0]}'s tab on the left edge");
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(first);
            Assert.True(peer.GetName() == dock[edge[0]].Title && peer.GetAutomationControlType() == AutomationControlType.Button,
                $"{edge[0]}'s edge tab is heard as \"{peer.GetName()}\", a {peer.GetAutomationControlType()}, not as a button of its name");

            Ui.Press(window, "TAB");
            Control second = window.FocusManager?.GetFocusedElement() as Control ?? first;
            Assert.True(Equals(second.Tag, edge[1]), $"Tab from {edge[0]}'s edge tab went to {second.GetType().Name} {second.Tag}, not to {edge[1]}'s");

            heard.Clear();
            Ui.Press(window, "ENTER");
            Assert.True(dock.Peeked == dock[edge[1]] && dock[edge[1]].Content.IsKeyboardFocusWithin && heard.Any(h => h.Words == $"{dock[edge[1]].Title}, tool window over the editors"),
                $"Enter on {edge[1]}'s edge tab opened {dock.Peeked?.Id} over the editors, its keys {(dock[edge[1]].Content.IsKeyboardFocusWithin ? "in it" : "elsewhere")}, heard as {Said(heard)}");
            heard.Clear();
            Ui.Press(window, "ESCAPE");
            Assert.True(dock.Peeked is null && window.EditorArea.CodeEditor.IsKeyboardFocusWithin && heard.Any(h => h.Words == $"{dock[edge[1]].Title} hidden"),
                $"Escape left {dock.Peeked?.Id} over the editors, or the keys out of the editor, heard as {Said(heard)}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void A_settings_field_is_named_by_its_label_and_the_project_button_by_the_project()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            AutomationPeer project = ControlAutomationPeer.CreatePeerForElement(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ProjectButton"));
            Assert.True(project.GetName() == Path.GetFileName(folder), $"The project's button is named \"{project.GetName()}\", not {Path.GetFileName(folder)}");

            SettingsDialog settings = window.ShowSettings();
            Ui.Settle();
            AutomationPeer[] met = [.. Walk(ControlAutomationPeer.CreatePeerForElement(settings))];
            Assert.True(met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.ComboBox && peer.GetName() == "Theme"),
                $"Settings' Theme box goes unnamed, among: {Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.ComboBox))}");
            // macOS's menu is the system's, so its page holds no check box to ask of.
            Assert.True(OperatingSystem.IsMacOS() || met.Any(peer => peer.GetAutomationControlType() == AutomationControlType.CheckBox && peer.GetName() == "Show main menu as a menu bar"),
                $"Settings' check box goes unnamed, among: {Heard(met.Where(peer => peer.GetAutomationControlType() == AutomationControlType.CheckBox))}");
            settings.Close();
            Ui.Settle();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The dialogs that ask for words, a choice or a box checked, as a screen reader meets each: every field named
    /// by the label beside it, so someone who cannot see the label still hears what the field is for.</summary>
    private void Every_field_of_a_dialogs_form_is_named_by_the_label_beside_it()
    {
        ConfigFile config = new(Path.Combine(folder, "nuget.config"), ConfigPlace.Folder, [], [], false, null, null, null);
        Named(new FeedDialog(config, null), "Name", "URL", "User", "Password", "Enabled", "Allow insecure connections");
        Named(new MigrationScriptDialog([new MigrationRow("1", "Initial", true)], "ShopContext"), "From", "To");
        Named(new GitQuestionDialog(new GitQuestion(GitQuestionKind.User, "Username for 'https://example.com': ", "https://example.com"), null), "User", "Password");
        Named(new GitQuestionDialog(new GitQuestion(GitQuestionKind.Passphrase, "Enter passphrase for key '/keys/id': ", "/keys/id"), null), "Passphrase");
        Named(new CommitSigningDialog(new SigningSetup(false, SigningFormat.Ssh, null), null, Path.Combine(folder, "allowed_signers")), "Key");
        Named(new CloneDialog(folder), "URL", "Directory");
        BreakpointProperties breakpoint = new();
        breakpoint.Show(new ExceptionBreakpoint("System.InvalidOperationException"));
        Named(new Window { Title = "Breakpoints", Width = 640, SizeToContent = SizeToContent.Height, Content = breakpoint }, "Condition", "Hit count", "Log message");
    }

    /// <summary>The refactorings that ask their options in a dialog, as a screen reader meets each: every field named by the
    /// label beside it, as every other form's is.</summary>
    private void Every_field_of_a_refactorings_dialog_is_named_by_the_label_beside_it()
    {
        Member name = new("P:Shop.Cart.Name", "Name: string", "property");
        Member total = new("M:Shop.Cart.Total", "Total(): int", "method");
        Named(new ExtractInterfaceDialog(new InterfaceSource(null, "Cart", "ICart", "ICart.cs", "Shop", [name, total]), (_, _, _) => Task.FromResult<string?>(null)),
            "Interface name", "File name");
        Named(MoveTypeDialog.ToFile(new TypeSource(null, "Cart", "class", "Shop", "Program.cs", "Cart.cs", 2), folder, _ => Task.FromResult<string?>(null)), "Target file name");
        Named(MoveTypeDialog.ToNamespace(new TypeSource(null, "Cart", "class", "Shop", "Program.cs", "Cart.cs", 2), _ => Task.FromResult<string?>(null)), "Target namespace");
        Named(MembersMoveDialog.PullUp(new Hierarchy(null, "Cart", [name, total], [new Member("T:Shop.Basket", "Basket", "class")], []), (_, _, _) => Task.FromResult<string?>(null)),
            "Base type");
        Named(new ChangeSignatureDialog(new Signature(null, "method", "Cart", "public", "Total", "int", [new Parameter(string.Empty, "int", "tax", string.Empty)], false),
            (_, _, _) => Task.FromResult<string?>(null)), "Name", "Return type");
        Named(new ExtractResourceDialog(new ExtractableValue("Background", "Red", new ResourceShape("https://github.com/avaloniaui", "SolidColorBrush", "Brush", true, '"'), "RedBrush", [(0, 3)]), [new ResourcePlace("App.axaml", "Application")],
            _ => null, (_, _, _) => Task.FromResult<string?>(null)), "Resource name", "Create in");
        Named(new ExtractStyleDialog(new StyleSource("Button", 0, [new StyleProperty("Margin", "12", false, 0, 1, "12")], "Spaced", true), [new ResourcePlace("App.axaml", "Application")],
            _ => null, (_, _, _) => Task.FromResult<string?>(null)), "Style class", "Create in");
    }

    /// <summary>Shows a dialog and walks it as a screen reader does, holding it to each label naming a field and to no field
    /// left unnamed.</summary>
    private static void Named(Window dialog, params string[] labels)
    {
        dialog.Show();
        try
        {
            Ui.Settle();
            string title = dialog.Title ?? dialog.GetType().Name;
            AutomationPeer[] fields = [.. Walk(ControlAutomationPeer.CreatePeerForElement(dialog))
                .Where(peer => peer.GetAutomationControlType() is AutomationControlType.Edit or AutomationControlType.ComboBox or AutomationControlType.CheckBox)];
            foreach (string label in labels)
            {
                Assert.True(fields.Any(field => field.GetName() == label), $"In {title} a screen reader hears no field named \"{label}\", the words beside it, only: {Heard(fields)}");
            }
            AutomationPeer[] unnamed = [.. fields.Where(field => field.GetName().Length == 0)];
            Assert.True(unnamed.Length == 0,
                $"In {title} a screen reader meets {unnamed.Length} fields named nothing ({string.Join(", ", unnamed.Select(field => field.GetAutomationControlType()))}), among: {Heard(fields)}");
        }
        finally
        {
            dialog.Close();
            Ui.Settle();
        }
    }

    internal static IEnumerable<AutomationPeer> Walk(AutomationPeer peer) => peer.GetChildren().SelectMany(Walk).Prepend(peer);

    private static AutomationPeer? Button(AutomationPeer within, string name) => Walk(within).FirstOrDefault(peer => IsButton(peer) && peer.GetName() == name);

    private static bool IsButton(AutomationPeer peer) => peer.GetAutomationControlType() == AutomationControlType.Button;

    /// <summary>What a walk met that has a name, for a failure to say.</summary>
    internal static string Heard(IEnumerable<AutomationPeer> met) =>
        string.Join(", ", met.Where(peer => peer.GetName().Length > 0).Select(peer => $"{peer.GetAutomationControlType()} \"{peer.GetName()}\""));
}
