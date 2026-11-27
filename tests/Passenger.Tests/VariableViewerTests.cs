using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Tables;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

[Collection(Alone.Name)]
public sealed class VariableViewerTests(VariableViewerTests.Project project) : IClassFixture<VariableViewerTests.Project>
{
    [AvaloniaFact]
    public void A_long_string_and_collection_open_in_small_viewers_at_a_stop()
    {
        project.Reset();
        MainWindow window = project.Open(project.PathOf("Program.cs"), languageServer: false, width: 1200, height: 820);
        try
        {
            string source = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
            int stop = source.IndexOf("Console.WriteLine", StringComparison.Ordinal);
            window.EditorArea.CodeEditor.Selections = SelectionSet.Single(stop);
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Tree.Rows.Any(row => row.Name == "orders"), "the sample's locals at the stop");

            CheckText(window);
            Window dataSet = CheckTables(window);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => !dataSet.IsVisible && !window.Debugger.IsDebugging, "the viewer to close when the program runs on");
        }
        finally
        {
            window.Close();
        }
    }

    private static void CheckText(MainWindow window)
    {
        string original = "first\r\n\t" + new string('x', 1200) + "\\nend";
        VariablesList hover = DebuggerHoverTests.Hover(window, "payload.Length");
        Assert.Single(hover.Tree.Rows);
        Assert.True(window.Assist.HoverText!.Length < 160);
        ClickViewerButton(window, hover, "CopyDebugText");
        Assert.Equal(original, Ui.Clipboard(window));
        hover = DebuggerHoverTests.Hover(window, "payload.Length");
        ClickViewerButton(window, hover, "ViewDebugValue");
        Window hoverText = Owned(window, "DebugTextViewer");
        CodeEditor hovered = Control<CodeEditor>(hoverText, "DebugValueText");
        Assert.Equal(original, hovered.Document.Snapshot.ToString());
        hovered.Selections = SelectionSet.Single(new Selection(0, 5));
        Ui.Press(hoverText, window.Actions.Keymap.ShortcutsFor(EditorActions.Copy).First().Notation());
        Ui.WaitFor(() => Ui.Clipboard(hoverText) == "first", "the selected part of the text on the clipboard");
        hoverText.Close();

        VariablesList variables = window.DebugWindow.VariablesList;
        variables.SelectedItem = Named(variables, "payload");
        Ui.ChooseFromMenu(variables, "Copy Text");
        Assert.Equal(original, Ui.Clipboard(window));
        Ui.ChooseFromMenu(variables, "View Text");
        Window text = Owned(window, "DebugTextViewer");
        CodeEditor editor = Control<CodeEditor>(text, "DebugValueText");
        Assert.Equal(original, editor.Document.Snapshot.ToString());
        Ui.Click(Control<Button>(text, "EditDebugValue"));
        Ui.Press(text, window.Actions.Keymap.ShortcutsFor(EditorActions.SelectAll).First().Notation());
        text.KeyTextInput("changed");
        Assert.Equal("changed", editor.Document.Snapshot.ToString());
        Ui.Click(Control<Button>(text, "ApplyDebugValue"));
        Ui.WaitFor(() => Named(variables, "payload").Value == "\"changed\"", "the applied text in the locals");
        text.Close();
    }

    private static Window CheckTables(MainWindow window)
    {
        VariablesList variables = window.DebugWindow.VariablesList;
        Assert.Equal("0", IdReads());
        Window list = OpenTable(Named(variables, "orders"));
        CellGrid grid = Control<CellGrid>(list, "DebugValueGrid");
        Ui.WaitFor(() => grid.Table is { RowCount: 12 }, "the first 12 list rows");
        Assert.Equal(new[] { "Id", "Status" }, Enumerable.Range(0, grid.Table!.ColumnCount).Select(grid.Table.Header));
        Assert.Equal("1", grid.Table.Cell(0, 0));
        Assert.Equal("Paid", grid.Table.Cell(0, 1));
        Assert.Equal("12", IdReads());
        Ui.Click(Control<Button>(list, "NextDebugRows"));
        Ui.WaitFor(() => grid.Table is { RowCount: 3 } page && page.RowNumber(0) == 13, "the second page");
        Assert.Equal("13", grid.Table!.Cell(0, 0));
        Assert.Equal("15", IdReads());
        Ui.Click(Control<Button>(list, "InspectDebugCell"));
        Window cell = Owned(list, "DebugTextViewer");
        Assert.Equal("13", Control<CodeEditor>(cell, "DebugValueText").Document.Snapshot.ToString());
        cell.Close();

        VariablesList hover = DebuggerHoverTests.Hover(window, "array.Length");
        Assert.Equal("array", Assert.Single(hover.Tree.Rows).Name);
        ClickViewerButton(window, hover, "ViewDebugValue");
        Assert.False(list.IsVisible);
        Window array = Owned(window, "DebugTableViewer");
        CellGrid arrayGrid = Control<CellGrid>(array, "DebugValueGrid");
        Ui.WaitFor(() => arrayGrid.Table?.Cell(0, 1) == "Paid", "the array's values");

        Window tableWindow = OpenTable(Named(variables, "ordersTable"));
        CellGrid gridTable = Control<CellGrid>(tableWindow, "DebugValueGrid");
        Ui.WaitFor(() => gridTable.Table?.Cell(0, 1) == "Sent", "the DataTable's values");

        variables.SelectedItem = Named(variables, "data");
        Ui.ChooseFromMenu(variables, "Add to Watches");
        Ui.WaitFor(() => variables.Tree.Rows.Any(row => row is { Name: "data", Role: VariableRole.Watch }), "the DataSet watch");
        Window dataSet = OpenTable(Named(variables, "data", VariableRole.Watch));
        CellGrid gridSet = Control<CellGrid>(dataSet, "DebugValueGrid");
        Ui.WaitFor(() => gridSet.Table?.Cell(0, 1) == "Sent", "the DataSet's first table");
        Control<ComboBox>(dataSet, "DebugValueTables").SelectedIndex = 1;
        Ui.WaitFor(() => gridSet.Table?.Cell(0, 0) == "29", "the DataSet's second table");
        return dataSet;

        string IdReads() => Ui.Await(window.Debugger.Session!.EvaluateAsync("Order.Reads", window.BottomPanel.Debug.SelectedFrame!.Id, EvaluateContexts.Watch), "the number of Id getters called").Result;

        Window OpenTable(VariableNode row)
        {
            variables.SelectedItem = row;
            Ui.ChooseFromMenu(variables, "View as Table");
            return Owned(window, "DebugTableViewer");
        }
    }

    private static VariableNode Named(VariablesList values, string name, VariableRole role = VariableRole.Value) =>
        Assert.Single(values.Tree.Rows, row => row.Name == name && row.Role == role);

    private static void ClickViewerButton(MainWindow window, VariablesList values, string name) =>
        DebuggerHoverTests.Click(window, Assert.Single(values.GetVisualDescendants().OfType<Button>(), button => button.Name == name));

    private static Window Owned(Window owner, string name) => Assert.Single(owner.OwnedWindows, window => window.Name == name && window.IsVisible);

    private static T Control<T>(Window window, string name) where T : Control => Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    public sealed class Project : SharedProject
    {
        public Project() : base("value-viewers-")
        {
            File.WriteAllText(PathOf("Viewers.csproj"), TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
            File.WriteAllText(PathOf("Program.cs"), """
                using System.Data;
                string payload = "first\r\n\t" + new string('x', 1200) + "\\nend";
                List<Order> orders = Enumerable.Range(1, 15).Select(i => new Order(i, "Paid")).ToList();
                Order[] array = orders.ToArray();
                using DataSet data = new();
                DataTable ordersTable = data.Tables.Add("Orders");
                ordersTable.Columns.Add("Id", typeof(int));
                ordersTable.Columns.Add("Status", typeof(string));
                ordersTable.Rows.Add(17, "Sent");
                DataTable returns = data.Tables.Add("Returns");
                returns.Columns.Add("Id", typeof(int));
                returns.Rows.Add(29);
                Console.WriteLine(payload.Length + orders.Count + array.Length + ordersTable.Rows.Count + data.Tables.Count);
                sealed class Order(int id, string status)
                {
                    public static int Reads;
                    public int Id { get { Reads++; return id; } }
                    public string Status { get; } = status;
                }
                """);
            Directory.CreateDirectory(PathOf("bin"));
        }
    }
}
