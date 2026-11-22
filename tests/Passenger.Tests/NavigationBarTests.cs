using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Navigation;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The navigation bar over an editor, as a person uses it: it names where the caret rests in the file's outline
/// (a C# file's from its own parse, with no language server), each part listing what stands beside it to go to, by the
/// pointer and by the keys; Jump to Navigation Bar and Previous Region give it the keys, and with the setting off it is
/// gone, the action listing the caret's part itself.</summary>
public sealed class NavigationBarTests : IDisposable
{
    private const string Cart = "namespace Contoso.Shop;\n\npublic sealed class Cart\n{\n    private readonly List<string> lines = [];\n\n"
        + "    public Cart(string customer)\n    {\n        Customer = customer;\n    }\n\n    public string Customer { get; }\n\n"
        + "    public void Add(string product, int quantity)\n    {\n        lines.Add(product);\n    }\n\n"
        + "    public bool Remove(string product) => lines.Remove(product);\n}\n\npublic sealed record CartLine(string Product, decimal Price);\n\n"
        + "public enum PaymentMethod\n{\n    Card,\n}\n";

    /// <summary>The line (from zero) between Add and Remove, and Remove's.</summary>
    private const int BetweenMembers = 17;
    private const int RemoveLine = 18;

    private readonly string folder = TestFolders.Create("navigation-bar-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>The navigation bar names the namespace, type and member the caret rests in, each part listing what
    /// stands beside it to go to, and Jump to Navigation Bar gives it the keys.</summary>
    [AvaloniaFact]
    public void The_navigation_bar_names_where_the_caret_rests_and_takes_the_keys_to_go_along_it()
    {
        Flow.StepApart<NavigationBarTests>("the bar names the namespace, type and member the caret rests in, and each part lists what stands beside it to go to", t => t.The_bar_names_the_namespace_type_and_member_the_caret_rests_in_and_each_part_lists_what_stands_beside_it_to_go_to());
        Flow.StepApart<NavigationBarTests>("Jump to Navigation Bar gives it the keys to move along it and open a list, and with the bar off lists the caret's part itself", t => t.Jump_to_Navigation_Bar_gives_it_the_keys_to_move_along_it_and_open_a_list_and_with_the_bar_off_lists_the_caret_s_part_itself());
    }

    private void The_bar_names_the_namespace_type_and_member_the_caret_rests_in_and_each_part_lists_what_stands_beside_it_to_go_to()
    {
        (MainWindow window, CodeEditor editor, NavigationBar bar) = OpenCart();
        try
        {
            Assert.Equal(["Contoso.Shop", "Cart", "Add(string, int)"], bar.Shown);
            Assert.False(bar.ListsMore, "With the caret in Add, the bar still offers a list of what Add holds, which is nothing");
            Assert.True(AutomationProperties.GetName(bar.Buttons[2]) == "Add(string, int), method",
                $"A screen reader hears the member's part as \"{AutomationProperties.GetName(bar.Buttons[2])}\", not by its name and kind");
            Button[] made = [.. bar.Buttons];
            Ui.Press(window, "RIGHT");
            Ui.Press(window, "LEFT");
            Assert.True(bar.Buttons.SequenceEqual(made), "A key moving the caret within Add made the bar's parts again");

            Ui.Click(bar.Buttons[2]);
            SymbolPopup beside = Assert.IsType<SymbolPopup>(window.Popup);
            Ui.WaitFor(() => beside.IsSettled, "Add's fellow members");
            Assert.Equal("Members of Cart", beside.Title);
            Assert.True(beside.Selected?.Name == "Add(string, int)", $"The member's list opened on {beside.Selected?.Name}, not on the member the caret is in");
            Ui.Press(window, "Escape");

            editor.GoTo(new TextPosition(BetweenMembers, 0));
            Ui.Settle();
            Assert.Equal(["Contoso.Shop", "Cart"], bar.Shown);
            Assert.True(bar.ListsMore, "Between Cart's members, the bar offers no list of them");
            Assert.True(bar.Buttons[1] == made[1], "The caret leaving Add for its type made the type's part again");

            Ui.Click(bar.Buttons[1]);
            SymbolPopup types = Assert.IsType<SymbolPopup>(window.Popup);
            Ui.WaitFor(() => types.IsSettled, "the namespace's types");
            Assert.Equal("In Contoso.Shop", types.Title);
            Assert.Equal(["Cart", "CartLine", "PaymentMethod"], types.Rows.Select(r => r.Name));
            Assert.True(types.Selected?.Name == "Cart", $"The type's list opened on {types.Selected?.Name}, not on the type the caret is in");
            Ui.Press(window, "Escape");
            Assert.Null(window.Popup);
            Assert.True(editor.IsKeyboardFocusWithin, "Given up, a list the pointer opened left the editor without the keys");

            Ui.Click(bar.Buttons[^1]);
            SymbolPopup members = Assert.IsType<SymbolPopup>(window.Popup);
            Ui.WaitFor(() => members.IsSettled, "Cart's members");
            Assert.Equal("Members of Cart", members.Title);
            Assert.Equal(["lines", "Cart(string)", "Customer", "Add(string, int)", "Remove(string)"], members.Rows.Select(r => r.Name));
            window.KeyTextInput("Rem");
            Ui.WaitFor(() => members.IsSettled && members.Rows.Count == 1, () => $"Cart's members narrowed to Remove, not {string.Join(", ", members.Rows.Select(r => r.Name))}");
            Ui.Press(window, "Enter");
            Assert.Null(window.Popup);
            Assert.True(editor.CaretPosition.Line == RemoveLine, $"Remove chosen, the caret went to line {editor.CaretPosition.Line + 1}, not Remove's");
            Assert.True(editor.IsKeyboardFocusWithin, "A member chosen from the bar's list left the editor without the keys");
            Ui.WaitFor(() => bar.Shown.Count == 3 && bar.Shown[2] == "Remove(string)", () => $"the bar following the caret to Remove, not {string.Join(" › ", bar.Shown)}");
        }
        finally
        {
            window.Close();
        }
    }

    private void Jump_to_Navigation_Bar_gives_it_the_keys_to_move_along_it_and_open_a_list_and_with_the_bar_off_lists_the_caret_s_part_itself()
    {
        (MainWindow window, CodeEditor editor, NavigationBar bar) = OpenCart();
        try
        {
            Assert.Null(window.Actions.Run(AppActions.JumpToNavigationBar));
            Ui.Settle();
            Assert.True(bar.Buttons[^1].IsKeyboardFocusWithin, "Jump to Navigation Bar left the bar's last part without the keys");
            Ui.Press(window, "LEFT");
            Assert.True(bar.Buttons[1].IsKeyboardFocusWithin, "Left in the bar did not move the keys to the type's part");
            Ui.Press(window, "DOWN");
            SymbolPopup types = Assert.IsType<SymbolPopup>(window.Popup);
            Ui.WaitFor(() => types.IsSettled, "the namespace's types");
            Assert.Equal("In Contoso.Shop", types.Title);
            Ui.Press(window, "Escape");
            Assert.Null(window.Popup);
            Assert.True(bar.Buttons[1].IsKeyboardFocusWithin, "Given up, a list the keys opened did not give the keys back to its part");
            Ui.Press(window, "Escape");
            Assert.True(editor.IsKeyboardFocusWithin, "Escape in the bar did not give the editor the keys back");

            Assert.Null(window.Actions.Run(AppActions.PreviousRegion));
            Ui.Settle();
            Assert.True(bar.Bar.IsKeyboardFocusWithin, "Previous Region from the editor passed over the bar above it");
            Ui.Press(window, "Escape");

            window.ApplySettings(window.Settings with { ShowNavigationBar = false });
            Ui.Settle();
            Assert.False(bar.Bar.IsVisible, "With the setting off, the bar still shows over the editor");
            Assert.Null(window.Actions.Run(AppActions.JumpToNavigationBar));
            Ui.WaitFor(() => window.Popup is SymbolPopup { IsSettled: true }, "the caret's part's list");
            SymbolPopup members = (SymbolPopup)window.Popup!;
            Assert.Equal("Members of Cart", members.Title);
            Assert.True(members.Selected?.Name == "Add(string, int)", $"The list of {string.Join(", ", members.Rows.Select(r => r.Name))} opened on {members.Selected?.Name}, not on the member the caret is in, at line {editor.CaretPosition.Line + 1}");
            Ui.Press(window, "Escape");

            window.ApplySettings(window.Settings with { ShowNavigationBar = true });
            Ui.WaitFor(() => bar.Bar.IsVisible && bar.Shown.Count == 3, () => $"the bar back, naming Add, not {string.Join(" › ", bar.Shown)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A window on Cart.cs with no language server, the caret in Add and the bar naming it.</summary>
    private (MainWindow Window, CodeEditor Editor, NavigationBar Bar) OpenCart()
    {
        string path = Path.Combine(folder, "Cart.cs");
        File.WriteAllText(path, Cart);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.Null(window.OpenFile(path));
        CodeEditor editor = window.EditorArea.CodeEditor;
        NavigationBar bar = window.EditorArea.ActiveGroup.Navigation;
        Ui.PlaceAfter(editor, "lines.Add(");
        editor.Focus();
        Ui.WaitFor(() => bar.Shown.Count == 3, () => $"the path to Add, not {string.Join(" › ", bar.Shown)}");
        return (window, editor, bar);
    }
}
