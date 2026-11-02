using System.Globalization;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Tables;
using Passenger.Editor;
using Passenger.Xaml.Localization;

namespace Passenger.Tests;

/// <summary>CSV, TSV and Excel files open as tables: their cells in rows and columns under a header, a CSV's Text a click
/// away and its edits in the Table, a workbook's sheets chosen above it with their numbers as Excel formats them, and the
/// chosen cells copied as Excel copies them.</summary>
public sealed class TablesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("tables-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_CSV_opens_as_its_table_under_its_first_rows_names_and_its_Text_edits_show_in_the_Table()
    {
        string path = Path.Combine(folder, "people.csv");
        File.WriteAllText(path, "Name,City,Note,Total\nAda,London,\"Wrote the \"\"first\"\" program\",\"1,234.50\"\nAlan,\"Wilmslow, Cheshire\",\"two\nlines\",980\n");
        MainWindow window = Open(path);

        CellGrid grid = Grid(window);
        Table table = grid.Table!;
        Assert.Equal(["Name", "City", "Note", "Total"], Enumerable.Range(0, table.ColumnCount).Select(table.Header));
        Assert.True(table.RowCount == 2, $"The header row should not be a row of the table, but it has {table.RowCount} rows");
        Assert.Equal("Wrote the \"first\" program", table.Cell(0, 2));
        Assert.Equal("Wilmslow, Cheshire", table.Cell(1, 1));
        Assert.Equal("two\nlines", table.Cell(1, 2));
        Assert.True(table.IsNumber(0, 3) && table.IsNumber(1, 3),
            "A number quoted for its thousands' commas should be drawn against the cell's right edge as an unquoted one is, as Excel reads both");
        Assert.False(table.IsNumber(1, 1), "Text that happens to be quoted should stay text");

        Ui.Click(Named(window, "ShowText"));
        Assert.Null(window.EditorArea.ActiveGroup.Table);
        Assert.True(window.EditorArea.CodeEditor.IsFocused, "Text should give the editor the focus, so typing edits the file");
        Ui.Type(window, "Z", "Z");

        Ui.Click(Named(window, "ShowTable"));
        Ui.WaitFor(() => Grid(window).Table?.Header(0) == "ZName", "the Table to show the header the Text was given");
        Assert.True(Grid(window).IsFocused, "The Table should take the focus back, so the keys move among its cells");
        window.Close();
    }

    /// <summary>A .resx opens as a table of its keys, values and comments, written in place as ResourceFiles writes them:
    /// a value written over, a key made in the row standing for a new one and given its value, a name another key has
    /// refused saying why, a key taken out from the menu, each one edit Undo takes back, the XML a click away.</summary>
    [AvaloniaFact]
    public void A_resx_opens_as_a_table_of_its_keys_whose_cells_written_and_keys_added_change_its_text_as_ResourceFiles_writes_it()
    {
        string path = Path.Combine(folder, "Strings.resx");
        const string Text = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <resheader name="resmimetype">
                <value>text/microsoft-resx</value>
              </resheader>
              <data name="Greeting" xml:space="preserve">
                <value>Hello</value>
                <comment>On the first page</comment>
              </data>
              <data name="Title" xml:space="preserve">
                <value>Shop</value>
              </data>
            </root>

            """;
        File.WriteAllText(path, Text);
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        Table table = grid.Table!;
        Assert.Equal(["Name", "Value", "Comment"], Enumerable.Range(0, table.ColumnCount).Select(table.Header));
        Assert.Equal([("Greeting", "Hello", "On the first page", "1"), ("Title", "Shop", "", "2"), ("", "", "", "+")],
            Enumerable.Range(0, table.RowCount).Select(row => (table.Cell(row, 0), table.Cell(row, 1), table.Cell(row, 2), table.RowLabel(row))));
        Point Cell(int row, int column) => grid.SortArrow(column).Center + new Vector(-20, ListRow.Height * (row + 1));
        string expected = Text;
        ResourceEntry EntryOf(string text, string key) => ResourceFiles.Parse(path, string.Empty, ResourceFormat.Resx, text).Entry(key)!;
        string Shown() => window.EditorArea.Active!.Text.Snapshot.ToString();

        Ui.Click(grid, Cell(0, 1));
        Ui.Press(window, "ENTER");
        TextBox box = grid.Writing ?? throw new Xunit.Sdk.XunitException("Enter on a value opened no box to write it in");
        Assert.Equal("Hello", box.Text);
        Assert.True(box.IsFocused, "The cell's box did not take the keys");
        box.SelectAll();
        window.KeyTextInput("Hello & welcome");
        Ui.Press(window, "ENTER");
        expected = ResourceFiles.WithValue(expected, EntryOf(expected, "Greeting"), "Hello & welcome")!;
        Assert.Equal(expected, Shown());
        Ui.WaitFor(() => grid.Table?.Cell(0, 1) == "Hello & welcome", "the table read again with the value written");
        Assert.Equal((1, 1), grid.Caret);

        Ui.Click(grid, Cell(2, 0));
        window.KeyTextInput("Farewell");
        Assert.Equal("Farewell", grid.Writing?.Text);
        Ui.Press(window, "ENTER");
        expected = ResourceFiles.WithEntry(expected, "Farewell", string.Empty)!;
        Assert.Equal(expected, Shown());
        Ui.WaitFor(() => grid.Table?.RowCount == 4 && grid.Table.Cell(2, 0) == "Farewell", "the key made in the new row, and a new row below it");
        Assert.Equal((2, 1), grid.Caret);
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Goodbye");
        Ui.Press(window, "ENTER");
        expected = ResourceFiles.WithValue(expected, EntryOf(expected, "Farewell"), "Goodbye")!;
        Assert.Equal(expected, Shown());
        Ui.WaitFor(() => grid.Table?.Cell(2, 1) == "Goodbye", "the new key's value in the table");

        Ui.Click(grid, Cell(1, 0));
        Ui.Press(window, "F2");
        grid.Writing!.SelectAll();
        window.KeyTextInput("Greeting");
        Ui.Press(window, "ENTER");
        Assert.Equal("'Greeting' is a key of Strings.resx already", window.Status);
        Assert.Equal(expected, Shown());

        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        Assert.Equal(ResourceFiles.WithEntry(ResourceFiles.WithValue(Text, EntryOf(Text, "Greeting"), "Hello & welcome")!, "Farewell", string.Empty), Shown());
        Ui.WaitFor(() => grid.Table?.Cell(2, 1) == string.Empty, "Undo's value in the table");
        Ui.Press(window, Ui.Shortcut(EditorActions.Redo));
        Assert.Equal(expected, Shown());

        Ui.RightClick(grid, Cell(1, 1));
        Ui.Choose(grid.Menu, "Delete Key");
        expected = ResourceFiles.Without(expected, [EntryOf(expected, "Title")])!;
        Assert.Equal(expected, Shown());
        Ui.WaitFor(() => grid.Table?.RowCount == 3 && grid.Table.Cell(1, 0) == "Farewell", "the table without the key taken out");

        Ui.Click(Named(window, "ShowText"));
        Assert.Null(window.EditorArea.ActiveGroup.Table);
        Assert.Contains("<value>Goodbye</value>", window.EditorArea.CodeEditor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.Click(Named(window, "ShowTable"));
        window.Perform(AppActions.SaveAll);
        Ui.Settle();
        window.Close();
        Assert.Equal(expected, File.ReadAllText(path));
    }

    /// <summary>A resource's XLIFF file opens as a table of its units, of which a target written is translated as
    /// ResourceFiles writes it, its state following; the source, written by the build from the .resx, is not written.</summary>
    [AvaloniaFact]
    public void An_XLIFF_files_units_open_as_a_table_whose_target_written_is_translated_as_ResourceFiles_writes_it()
    {
        Directory.CreateDirectory(Path.Combine(folder, "xlf"));
        string path = Path.Combine(folder, "xlf", "Strings.fr.xlf");
        const string Text = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2">
              <file datatype="xml" source-language="en" target-language="fr" original="../Strings.resx">
                <body>
                  <trans-unit id="Greeting">
                    <source>Hello</source>
                    <target state="new">Hello</target>
                    <note>On the first page</note>
                  </trans-unit>
                </body>
              </file>
            </xliff>

            """;
        File.WriteAllText(path, Text);
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        Table table = grid.Table!;
        Assert.Equal(["ID", "Source", "Target", "State", "Note"], Enumerable.Range(0, table.ColumnCount).Select(table.Header));
        Assert.Equal(["Greeting", "Hello", "Hello", "new", "On the first page"], Enumerable.Range(0, table.ColumnCount).Select(column => table.Cell(0, column)));
        Assert.Equal(1, table.RowCount);

        Ui.Click(grid, grid.SortArrow(1).Center + new Vector(-20, ListRow.Height));
        Ui.Press(window, "ENTER");
        Assert.Null(grid.Writing);
        Ui.Press(window, "RIGHT");
        Ui.Press(window, "ENTER");
        TextBox box = grid.Writing ?? throw new Xunit.Sdk.XunitException("Enter on a unit's target opened no box to write it in");
        box.SelectAll();
        window.KeyTextInput("Bonjour");
        Ui.Press(window, "ENTER");
        Assert.Equal(ResourceFiles.WithTarget(Text, "Greeting", "Bonjour", "Hello", "On the first page"), window.EditorArea.Active!.Text.Snapshot.ToString());
        Ui.WaitFor(() => grid.Table?.Cell(0, 3) == "translated", "the unit's state in the table once its target was written");
        window.Close();
    }

    /// <summary>A CSV sorted by a column's title, as a person clicks its arrow: a column of numbers by their values, then
    /// the other way, then in the file's order again, each row numbered as in the file; a column of dates by when they are;
    /// and the file's text never changed.</summary>
    [AvaloniaFact]
    public void A_tables_rows_sort_by_a_columns_title_numbers_and_dates_by_value_both_ways_and_back_leaving_the_file_as_it_was()
    {
        string path = Path.Combine(folder, "orders.csv");
        string text = "Name,Total,When\nAda,980,2024-03-01\nAlan,\"1,234.50\",2023-12-31\nGrace,75,2024-01-15\nEdsger,,2024-02-29\n";
        File.WriteAllText(path, text);
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        string[] Names() => [.. Enumerable.Range(0, grid.Table!.RowCount).Select(row => grid.Table.Cell(row, 0))];
        int[] Numbers() => [.. Enumerable.Range(0, grid.Table!.RowCount).Select(row => grid.Table.RowNumber(row))];
        void SortBy(int column, (int, bool)? sorted)
        {
            Ui.Click(grid, grid.SortArrow(column).Center);
            Ui.WaitFor(() => grid.Table?.Sort == sorted, $"the table sorted {sorted?.ToString() ?? "in the file's order"}");
        }

        SortBy(1, (1, false));
        Assert.Equal(["Grace", "Ada", "Alan", "Edsger"], Names());
        Assert.Equal([3, 1, 2, 4], Numbers());
        SortBy(1, (1, true));
        Assert.Equal(["Alan", "Ada", "Grace", "Edsger"], Names());
        SortBy(1, null);
        Assert.Equal(["Ada", "Alan", "Grace", "Edsger"], Names());
        Assert.Equal([1, 2, 3, 4], Numbers());
        SortBy(2, (2, false));
        Assert.Equal(["Alan", "Grace", "Edsger", "Ada"], Names());

        Assert.False(window.EditorArea.Active!.IsModified, "Sorting the table changed the file's text");
        window.Perform(AppActions.SaveAll);
        Ui.Settle();
        window.Close();
        Assert.Equal(text, File.ReadAllText(path));
    }

    /// <summary>The keys sort too: the menu key on a table offers the chosen cell's column's sorts, the one in force checked,
    /// as the header's menu does, and the rows sort as chosen and go back to the file's order.</summary>
    [AvaloniaFact]
    public void The_menu_key_sorts_by_the_chosen_cells_column_both_ways_and_back()
    {
        string path = Path.Combine(folder, "scores.csv");
        File.WriteAllText(path, "Name,Score\nAda,7\nAlan,12\nGrace,3\n");
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        Ui.Click(grid, grid.SortArrow(1).Center + new Vector(-20, ListRow.Height * 2));
        Assert.Equal(1, grid.Caret.Column);
        string[] Names() => [.. Enumerable.Range(0, grid.Table!.RowCount).Select(row => grid.Table.Cell(row, 0))];
        string[] Offered()
        {
            Ui.Run(window, AppActions.ShowContextMenu);
            ContextMenu menu = grid.Menu ?? throw new Xunit.Sdk.XunitException("The menu key on the table opened no menu");
            return [.. menu.Items.OfType<MenuItem>().Take(3).Select(i => $"{i.Header}{(i.IsChecked ? " (checked)" : "")}")];
        }

        Assert.Equal(["Sort Ascending", "Sort Descending", "File Order (checked)"], Offered());
        Ui.Choose(grid.Menu!, "Sort Descending");
        Ui.WaitFor(() => grid.Table?.Sort == (1, true), "the table sorted by Score, the most first");
        Assert.Equal(["Alan", "Ada", "Grace"], Names());
        Assert.Equal(["Sort Ascending", "Sort Descending (checked)", "File Order"], Offered());
        Ui.Choose(grid.Menu!, "File Order");
        Ui.WaitFor(() => grid.Table?.Sort is null, "the table in the file's order again");
        Assert.Equal(["Ada", "Alan", "Grace"], Names());
        window.Close();
    }

    [AvaloniaFact]
    public void A_workbook_shows_each_sheet_with_its_numbers_formatted_as_Excel_shows_them_and_is_never_written()
    {
        string path = Path.Combine(folder, "sales.xlsx");
        WriteWorkbook(path);
        byte[] before = File.ReadAllBytes(path);
        MainWindow window = Open(path);

        Table sales = Grid(window).Table!;
        CultureInfo culture = CultureInfo.CurrentCulture;
        Assert.Equal("A", sales.Header(0));
        Assert.Equal("Widget", sales.Cell(1, 0));
        Assert.Equal(1234.5.ToString("\\$#,##0.00", culture), sales.Cell(1, 1));
        Assert.True(sales.IsNumber(1, 1), "A price should be a number, drawn against its cell's right edge");
        Assert.Equal("2023-03-15", sales.Cell(1, 2));
        Assert.Equal(0.1234.ToString("0.00%", culture), sales.Cell(1, 3));
        Assert.Equal("TRUE", sales.Cell(3, 4));
        Assert.True(sales.RowCount == 4, $"The sheet's empty third row should be kept as a row, but it has {sales.RowCount}");
        Assert.Equal(string.Empty, sales.Cell(2, 0));
        Assert.True(window.EditorArea.Active!.IsReadOnly, "A workbook's tab should be read-only, since it holds no text of the file");

        ToggleButton other = window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == "Other");
        Assert.DoesNotContain(window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<ToggleButton>(), b => b.Content as string == "Hidden");
        Ui.Click(other);
        Ui.WaitFor(() => Grid(window).Table is { RowCount: 2 } table && table.Cell(1, 1) == "42", "the Other sheet's cells");

        window.Perform(AppActions.SaveAll);
        Ui.Settle();
        window.Close();
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "The workbook should never be written over");
    }

    [AvaloniaFact]
    public void A_workbook_saved_again_by_another_program_shows_its_new_cells_and_no_text_s_storage_in_the_status_bar()
    {
        string path = Path.Combine(folder, "sales.xlsx");
        WriteWorkbook(path);
        MainWindow window = Open(path);
        Assert.False(Status(window, "EncodingButton").IsVisible || Status(window, "LineSeparatorButton").IsVisible,
            "A workbook's tab holds no text, so the status bar should offer no encoding or line separator to change");
        Assert.True(string.IsNullOrEmpty(((TextBlock)Status(window, "IndentText")).Text) && string.IsNullOrEmpty(((TextBlock)Status(window, "CaretText")).Text),
            "A workbook's tab holds no text, so the status bar should show no indent or caret");

        // As Excel saves: a new file put in the old one's place.
        string saved = Path.Combine(folder, "saved.tmp");
        WriteWorkbook(saved, price: 99);
        File.Move(saved, path, overwrite: true);
        string price = 99.0.ToString("\\$#,##0.00", CultureInfo.CurrentCulture);
        Ui.WaitFor(() => Grid(window).Table?.Cell(1, 1) == price, "the table to show the price the workbook was saved with");
        window.Close();
    }

    /// <summary>Sheets chosen faster than they are read: South's read is slow, landing after East's has and once West has
    /// been chosen, yet the table shows West, chosen last, rather than South.</summary>
    [AvaloniaFact]
    public void A_sheets_read_that_lands_after_a_later_sheet_was_chosen_never_replaces_the_sheet_chosen_last()
    {
        string path = Path.Combine(folder, "regions.xlsx");
        WriteSheets(path, "North", "South", "East", "West");
        MainWindow window = Open(path);
        Assert.Equal("1", Grid(window).Table!.Cell(0, 0));
        TaskCompletionSource south = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource west = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TableFile.For(window.EditorArea.Active!)!.HoldNext(south.Task, Task.CompletedTask, west.Task);

        Ui.Click(Sheet(window, "South"));
        Ui.Click(Sheet(window, "East"));
        Assert.True(Grid(window).Table?.Cell(0, 0) == "3", $"East's read did not land while South's was held: the table shows {Grid(window).Table?.Cell(0, 0)}");
        Ui.Click(Sheet(window, "West"));
        south.SetResult();
        Ui.Settle();
        west.SetResult();
        Ui.Settle();
        Assert.True(Grid(window).Table?.Cell(0, 0) == "4",
            $"West was chosen last, but the table shows {Grid(window).Table?.Cell(0, 0)}: a read of a sheet chosen before it landed in its place");
        Assert.True(Sheet(window, "West").IsChecked == true, "West, chosen last, is not the sheet checked");
        window.Close();
    }

    [AvaloniaFact]
    public void File_Open_on_a_workbook_with_nothing_open_shows_it_as_its_table()
    {
        string path = Path.Combine(folder, "sales.xlsx");
        WriteWorkbook(path);
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();

        string? why = window.Open(path);
        Assert.True(why is null, $"File, Open was to open the workbook, but said '{why}'");
        Ui.WaitFor(() => window.EditorArea.ActiveGroup.Table?.Table is not null, "sales.xlsx's table to be read");
        Assert.Equal("Widget", Grid(window).Table!.Cell(1, 0));
        window.Close();
    }

    [AvaloniaFact]
    public void Keys_choose_a_range_of_cells_and_Copy_writes_them_as_tab_separated_lines()
    {
        string path = Path.Combine(folder, "numbers.tsv");
        File.WriteAllText(path, "a\tb\tc\n1\t2\t3\n4\t5\t6\n");
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        Ui.Click(grid, new Point(60, ListRow.Height * 1.5));
        Assert.Equal((0, 0), grid.Caret);

        Ui.Press(window, "shift DOWN");
        Ui.Press(window, "shift RIGHT");
        Ui.Press(window, Ui.Shortcut(EditorActions.Copy));
        Assert.Equal($"1\t2{Environment.NewLine}4\t5{Environment.NewLine}", Ui.Clipboard(window));
        window.Close();
    }

    /// <summary>The cells' menu copies what is chosen as Copy's keys do, each item naming its keys: a right-click among the
    /// chosen cells keeps them, one outside them chooses the cell under it, and Select All there chooses every cell.</summary>
    [AvaloniaFact]
    public void The_cells_menu_copies_the_chosen_cells_and_a_right_click_elsewhere_chooses_the_cell_under_it()
    {
        string path = Path.Combine(folder, "numbers.csv");
        File.WriteAllText(path, "a,b,c\n1,2,3\n4,5,6\n7,8,9\n");
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        Ui.Click(grid, new Point(60, ListRow.Height * 1.5));
        Ui.Press(window, "shift DOWN");
        Ui.Press(window, "shift RIGHT");

        Ui.RightClick(grid, new Point(60, ListRow.Height * 2.5));
        Assert.True(grid.Chosen == (0, 0, 1, 1), $"A right-click among the chosen cells left {grid.Chosen} chosen");
        ContextMenu menu = grid.Menu ?? throw new Xunit.Sdk.XunitException("A right-click on the table's cells opened no menu");
        Keystroke copyKeys = Keystroke.Parse(Ui.Shortcut(EditorActions.Copy));
        Assert.Equal(new KeyGesture(copyKeys.Key, copyKeys.Modifiers), menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Copy").InputGesture);
        Ui.Choose(menu, "Copy");
        Assert.Equal($"1\t2{Environment.NewLine}4\t5{Environment.NewLine}", Ui.Clipboard(window));

        Ui.RightClick(grid, new Point(60, ListRow.Height * 3.5));
        Assert.True(grid.Chosen == (2, 0, 2, 0), $"A right-click outside the chosen cells left {grid.Chosen} chosen, not the cell under it");
        Ui.Choose(grid.Menu, "Copy");
        Assert.Equal($"7{Environment.NewLine}", Ui.Clipboard(window));

        Ui.RightClick(grid, new Point(60, ListRow.Height * 1.5));
        Ui.Choose(grid.Menu, "Select All");
        Assert.Equal((0, 0, 2, 2), grid.Chosen);
        window.Close();
    }

    /// <summary>An empty CSV shows a header of no columns: the pointer moved, pressed and double-clicked on it finds no
    /// column's edge to size.</summary>
    [AvaloniaFact]
    public void The_pointer_over_an_empty_CSVs_header_finds_no_column_edge_and_raises_no_error()
    {
        string path = Path.Combine(folder, "empty.csv");
        File.WriteAllText(path, string.Empty);
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        Assert.True(grid.Table is { ColumnCount: 0, RowCount: 0 }, $"An empty CSV should show a table of no cells, not {grid.Table?.RowCount} rows of {grid.Table?.ColumnCount} columns");

        foreach (double x in new[] { 30.0, 45.0, 60.0, 200.0 })
        {
            window.MouseMove(grid.TranslatePoint(new Point(x, ListRow.Height / 2), window)!.Value, RawInputModifiers.None);
        }
        Ui.Settle();
        Assert.True(grid.Cursor is null, "The pointer over an empty table's header should find no column's edge to size, but shows the sizing cursor");
        Ui.Click(grid, new Point(60, ListRow.Height / 2));
        Ui.DoubleClick(grid, new Point(60, ListRow.Height / 2));
        Ui.Drag(window, grid.TranslatePoint(new Point(60, ListRow.Height / 2), window)!.Value, grid.TranslatePoint(new Point(160, ListRow.Height / 2), window)!.Value);
        Assert.True(grid.Table is { ColumnCount: 0 }, "Pressing an empty table's header should change nothing");
        window.Close();
    }

    [AvaloniaFact]
    public void Find_searches_the_tables_cells_and_enter_walks_them_not_the_text_under_it()
    {
        string path = Path.Combine(folder, "cities.csv");
        File.WriteAllText(path, "Name,City\nAda,London\nBob,Paris\nLonny,Leeds\n");
        MainWindow window = Open(path);
        CellGrid grid = Grid(window);
        grid.Focus();

        Ui.Press(window, Ui.Shortcut(AppActions.Find));
        window.KeyTextInput("lon");
        Ui.Settle();
        Assert.True(grid.Caret == (0, 1), $"Find should choose the first cell holding the query, London, but chose {grid.Caret}");
        Assert.Equal("1/2", window.EditorArea.FindBar.CountText);
        Assert.Empty(window.EditorArea.CodeEditor.Highlights);

        Ui.Press(window, "ENTER");
        Assert.True(grid.Caret == (2, 0), $"Enter should go to the next cell holding the query, Lonny, but chose {grid.Caret}");
        Assert.Equal("2/2", window.EditorArea.FindBar.CountText);
        Ui.Press(window, "shift ENTER");
        Assert.Equal((0, 1), grid.Caret);

        Ui.Press(window, "ESCAPE");
        Assert.False(window.EditorArea.FindBar.IsVisible);
        Assert.True(grid.IsFocused, "Closing Find should give the table its focus back");
        window.Close();
    }

    private MainWindow Open(string path)
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(path);
        Ui.WaitFor(() => window.EditorArea.ActiveGroup.Table?.Table is not null, $"{Path.GetFileName(path)}'s table to be read");
        return window;
    }

    private static CellGrid Grid(MainWindow window) =>
        window.EditorArea.ActiveGroup.Table ?? throw new InvalidOperationException("The file shows no table");

    private static Control Status(MainWindow window, string name) =>
        window.FindControl<Control>(name) ?? throw new InvalidOperationException($"The status bar has no {name}");

    private static Control Named(MainWindow window, string name) =>
        window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

    /// <summary>A workbook's sheet's segment above its table.</summary>
    private static ToggleButton Sheet(MainWindow window, string name) =>
        window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == name);

    /// <summary>A workbook of sheets of these names, each holding its place among them, from 1, in A1.</summary>
    private static void WriteSheets(string path, params string[] names)
    {
        const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml)
        {
            using StreamWriter writer = new(zip.CreateEntry(name).Open());
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
        }
        Part("xl/workbook.xml", $"<workbook {Ns}><sheets>{string.Concat(names.Select((n, i) => $"<sheet name=\"{n}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"))}</sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"ws\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) + "</Relationships>");
        for (int i = 0; i < names.Length; i++)
        {
            Part($"xl/worksheets/sheet{i + 1}.xml", $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\"><v>{i + 1}</v></c></row></sheetData></worksheet>");
        }
    }

    /// <summary>A workbook as Excel writes one: shared strings, a currency, a date and a percent style, an empty row, a
    /// formula's value, a boolean, and a hidden sheet.</summary>
    private static void WriteWorkbook(string path, double price = 1234.5)
    {
        const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml)
        {
            using StreamWriter writer = new(zip.CreateEntry(name).Open());
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
        }
        Part("xl/workbook.xml", $"<workbook {Ns}><sheets><sheet name=\"Sales\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Other\" sheetId=\"2\" r:id=\"rId2\"/>"
            + "<sheet name=\"Hidden\" sheetId=\"3\" state=\"hidden\" r:id=\"rId2\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"ws\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"ws\" Target=\"/xl/worksheets/sheet2.xml\"/></Relationships>");
        Part("xl/sharedStrings.xml", $"<sst {Ns}><si><t>Product</t></si><si><r><t>Wid</t></r><r><t>get</t></r><rPh><t>x</t></rPh></si><si><t>Total</t></si></sst>");
        Part("xl/styles.xml", $"<styleSheet {Ns}><numFmts><numFmt numFmtId=\"164\" formatCode=\"&quot;$&quot;#,##0.00;[Red]\\-&quot;$&quot;#,##0.00\"/>"
            + "<numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/><xf numFmtId=\"165\"/><xf numFmtId=\"10\"/></cellXfs></styleSheet>");
        Part("xl/worksheets/sheet1.xml", $"<worksheet {Ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row>"
            + $"<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\" s=\"1\"><v>{price.ToString(CultureInfo.InvariantCulture)}</v></c><c r=\"C2\" s=\"2\"><v>45000</v></c><c r=\"D2\" s=\"3\"><v>0.1234</v></c></row>"
            + "<row r=\"4\"><c r=\"A4\" t=\"s\"><v>2</v></c><c r=\"B4\" s=\"1\"><f>SUM(B2:B3)</f><v>1234.5</v></c><c r=\"E4\" t=\"b\"><v>1</v></c></row>"
            + "<row r=\"9\" ht=\"20\" customHeight=\"1\"/></sheetData></worksheet>");
        Part("xl/worksheets/sheet2.xml", $"<worksheet {Ns}><sheetData><row r=\"2\"><c r=\"B2\"><v>42</v></c></row></sheetData></worksheet>");
    }
}
