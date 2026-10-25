using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The refactorings that ask their options in a dialog, each through its own keys and its dialog's controls on a
/// real project, computed by Passenger's extension in the real language server: Change Signature, Extract Interface,
/// Move to Another File, Move to Namespace, Pull Members Up and Push Members Down; Undo takes each back whole.</summary>
public sealed class RefactoringDialogsTests
{
    private const string Calc = "namespace Shop;\n\npublic static class Calc\n{\n    public static int Scale(int value, int factor, int unused) => value * factor;\n\n"
        + "    public static int Twice(int value) => Scale(value, 2, 0);\n}\n";

    private const string Till = "namespace Shop;\n\npublic static class Till\n{\n    public static int Total() => Calc.Scale(2, 3, 9) + Calc.Scale(4, factor: 5, unused: 1);\n}\n";

    private const string Order = "namespace Shop;\n\npublic class Order\n{\n    public int Total { get; private set; }\n\n    public void Add(int amount) => Total += Calc.Twice(amount);\n\n"
        + "    public string Describe() => $\"{Total}\";\n}\n";

    private const string OrderBook = "namespace Shop;\n\npublic static class OrderBook\n{\n    public static Order Open() => new Order();\n}\n";

    private const string Report = "using Shop;\n\nnamespace Shop.Reports;\n\npublic static class Report\n{\n    public static string Of(Order order) => order.Describe() + Till.Total();\n}\n";

    private const string Shapes = "using System;\n\nnamespace Shop;\n\npublic class Circle\n{\n    public double Area(double radius) => Math.PI * radius * radius;\n}\n\n"
        + "public class Square\n{\n    public int Side;\n}\n";

    private const string Animal = "namespace Shop;\n\npublic class Animal\n{\n    public string Name = \"\";\n\n    public virtual string Sound() => \"...\";\n}\n";

    private const string Dog = "using System.Text;\n\nnamespace Shop;\n\npublic class Dog : Animal\n{\n    public int Legs => 4;\n\n    public string Describe()\n    {\n"
        + "        StringBuilder text = new();\n        text.Append(Name).Append(' ').Append(Legs);\n        return text.ToString();\n    }\n}\n";

    private const string Cat = "namespace Shop;\n\npublic class Cat : Animal\n{\n    public bool Purrs => true;\n}\n";

    private string folder = string.Empty;

    /// <summary>Every dialog's refactoring made in one window on one project, each through its own keys or menu item and
    /// its dialog's controls, and each taken back whole by Undo before the next: the project and its language server are
    /// made once, and no refactoring touches a closed file another wrote a moment before, which a refactoring refuses. Each
    /// file a refactoring changes is checked whole.</summary>
    [AvaloniaFact]
    public void Each_refactoring_dialog_changes_every_file_it_must_and_undo_takes_each_back_whole()
    {
        folder = PathNames.Canonical(TestFolders.Create("refactorings-"));
        File.WriteAllText(PathOf("Shop.csproj"), TestProjects.Library("<Nullable>enable</Nullable>"));
        (string Name, string Text)[] files = [("Calc.cs", Calc), ("Till.cs", Till), ("Order.cs", Order), ("OrderBook.cs", OrderBook),
            ("Report.cs", Report), ("Shapes.cs", Shapes), ("Animal.cs", Animal), ("Dog.cs", Dog), ("Cat.cs", Cat)];
        foreach ((string name, string text) in files)
        {
            File.WriteAllText(PathOf(name), text);
        }
        ChildResult restored = Restore.Run(folder);
        Assert.True(restored.Succeeded, $"The project did not restore:\n{restored.Output}{restored.Error}");
        MainWindow window = Open("Calc.cs");
        List<(string Title, string Text)> asked = Answer(window);

        ChangeSignature(window, asked);
        ExtractInterface(window, asked);
        MoveToNamespace(window, asked);
        MoveToAnotherFile(window, asked);
        PushMembersDown(window, asked);
        PullMembersUp(window, asked);
        window.Close();
    }

    /// <summary>Change Signature adds, removes and reorders parameters, and every call passes its arguments in the new order.</summary>
    private void ChangeSignature(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Calc.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "int Sca");
        Ui.Run(window, AppActions.ChangeSignature);
        ChangeSignatureDialog dialog = Shown<ChangeSignatureDialog>(window, "Change Signature");
        Assert.Equal("public static int Scale(int value, int factor, int unused)", dialog.Preview);
        Assert.Equal(["value", "factor", "unused"], dialog.Parameters.Select(p => p.NameBox.Text));

        Ui.Click(dialog.Parameters[2].NameBox);
        Ui.Click(dialog.RemoveButton);
        Ui.Click(dialog.Parameters[1].TypeBox);
        Ui.Click(dialog.UpButton);
        Assert.Equal(["factor", "value"], dialog.Parameters.Select(p => p.NameBox.Text));
        Ui.Click(dialog.Parameters[1].NameBox);
        Ui.Click(dialog.AddButton);
        dialog.KeyTextInput("int");
        Ui.Settle();
        Ui.Click(dialog.Parameters[2].NameBox);
        dialog.KeyTextInput("offset");
        Ui.Click(dialog.Parameters[2].DefaultBox);
        dialog.KeyTextInput("0");
        Ui.Settle();
        Assert.Equal("public static int Scale(int factor, int value, int offset)", dialog.Preview);
        Assert.Equal(string.Empty, dialog.Problem);
        Next(dialog);

        string calc = Calc.Replace("Scale(int value, int factor, int unused)", "Scale(int factor, int value, int offset)", StringComparison.Ordinal)
            .Replace("Scale(value, 2, 0)", "Scale(2, value, 0)", StringComparison.Ordinal);
        string till = Till.Replace("Calc.Scale(2, 3, 9) + Calc.Scale(4, factor: 5, unused: 1)", "Calc.Scale(3, 2, 0) + Calc.Scale(factor: 5, 4, 0)", StringComparison.Ordinal);
        Assert.Equal(calc, window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(till, File.ReadAllText(PathOf("Till.cs")));
        Assert.StartsWith("Changed the signature of Calc.Scale in 2 files", window.Status, StringComparison.Ordinal);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Change Signature? It changes Calc.cs and Till.cs.")], asked);
        Assert.Equal(Calc, window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(Till, File.ReadAllText(PathOf("Till.cs")));
    }

    /// <summary>Extract Interface makes the interface of the members chosen in a file of its own, and the class implements it.</summary>
    private void ExtractInterface(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Order.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Ord");
        // Rider gives it no keys: it is Refactor, Extract Interface... in the menu.
        Assert.True(window.RunFromMenu(AppActions.ExtractInterface), window.Status);
        ExtractInterfaceDialog dialog = Shown<ExtractInterfaceDialog>(window, "Extract Interface");
        Assert.Equal("IOrder", dialog.NameBox.Text);
        Assert.Equal("IOrder.cs", dialog.FileBox.Text);
        Assert.Equal(["Total: int", "Add(int): void", "Describe(): string"], dialog.Members.Select(m => m.Text));
        Ui.Click(dialog.Members[2].Box);
        Assert.False(dialog.Members[2].Box.IsChecked, "A click did not leave Describe out of the interface");
        Next(dialog);

        string extracted = PathOf("IOrder.cs");
        Assert.True(File.Exists(extracted), $"Extract Interface made no IOrder.cs: {window.Status}");
        Assert.Equal("namespace Shop;\n\npublic interface IOrder\n{\n    int Total { get; }\n\n    void Add(int amount);\n}\n", File.ReadAllText(extracted).ReplaceLineEndings("\n"));
        Assert.Equal(Order.Replace("public class Order", "public class Order : IOrder", StringComparison.Ordinal), window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal("Extracted the interface IOrder from Order to IOrder.cs", window.Status);
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Path == extracted);
        Ui.WaitFor(() => window.CSharpService!.For(window.EditorArea.Active!)!.Diagnostics.All(d => d.Severity != Passenger.Lsp.DiagnosticSeverity.Error), "the language server to take IOrder.cs in");

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Extract Interface? It changes Order.cs and IOrder.cs.")], asked);
        Assert.False(File.Exists(extracted), "Undo left IOrder.cs");
        Assert.Equal(Order, window.EditorArea.Active?.Text.Snapshot.ToString());
    }

    /// <summary>Move to Another File takes a type out of a file of several with the usings it needs.</summary>
    private void MoveToAnotherFile(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Shapes.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Circ");
        Ui.Run(window, AppActions.Move);
        Assert.Equal(["Move to Another File…", "Move to Namespace…"], window.Assist.Offered);
        Ui.Press(window, "ENTER");
        MoveTypeDialog dialog = Shown<MoveTypeDialog>(window, "Move to Another File");
        Assert.Equal("Circle.cs", dialog.TargetBox.Text);
        Next(dialog);

        string moved = PathOf("Circle.cs");
        Assert.True(File.Exists(moved), $"Move to Another File made no Circle.cs: {window.Status}");
        Assert.Equal("using System;\n\nnamespace Shop;\n\npublic class Circle\n{\n    public double Area(double radius) => Math.PI * radius * radius;\n}\n",
            File.ReadAllText(moved).ReplaceLineEndings("\n"));
        Assert.Equal("using System;\n\nnamespace Shop;\n\npublic class Square\n{\n    public int Side;\n}\n", window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal("Moved class Circle to Circle.cs", window.Status);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Move to Another File? It changes Shapes.cs and Circle.cs.")], asked);
        Assert.False(File.Exists(moved), "Undo left Circle.cs");
        Assert.Equal(Shapes, window.EditorArea.Active?.Text.Snapshot.ToString());
    }

    /// <summary>Move to Namespace changes a type's namespace and gives the files that use it a using of the new one.</summary>
    private void MoveToNamespace(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Order.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Ord");
        Ui.Run(window, AppActions.Move);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        MoveTypeDialog dialog = Shown<MoveTypeDialog>(window, "Move to Namespace");
        Assert.Equal("Shop", dialog.TargetBox.Text);
        dialog.TargetBox.Focus();
        dialog.TargetBox.SelectAll();
        dialog.KeyTextInput("Sales");
        Next(dialog);

        // The type's file uses Calc, which stayed in Shop; OrderBook, in Shop, and Report, in Shop.Reports, use Order.
        Assert.Equal("using Shop;\n\n" + Order.Replace("namespace Shop;", "namespace Sales;", StringComparison.Ordinal), window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal("using Sales;\n\n" + OrderBook, File.ReadAllText(PathOf("OrderBook.cs")));
        Assert.Equal(Report.Replace("using Shop;\n", "using Sales;\nusing Shop;\n", StringComparison.Ordinal), File.ReadAllText(PathOf("Report.cs")));
        Assert.StartsWith("Moved class Order to namespace Sales in 3 files", window.Status, StringComparison.Ordinal);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Move to Namespace? It changes Order.cs, OrderBook.cs and Report.cs.")], asked);
        Assert.Equal(Order, window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(OrderBook, File.ReadAllText(PathOf("OrderBook.cs")));
        Assert.Equal(Report, File.ReadAllText(PathOf("Report.cs")));
    }

    /// <summary>Pull Members Up moves the members chosen into the base class with the usings they need. Animal.cs is open
    /// here, from Push Members Down before it, so its change is the editor's.</summary>
    private void PullMembersUp(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Dog.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Do");
        // Rider gives it no keys: it is Refactor, Pull Members Up... in the menu.
        Assert.True(window.RunFromMenu(AppActions.PullMembersUp), window.Status);
        MembersMoveDialog dialog = Shown<MembersMoveDialog>(window, "Pull Members Up");
        Assert.Equal("Animal", dialog.BaseBox.SelectedItem);
        Assert.Equal(["Legs: int", "Describe(): string"], dialog.Members.Select(m => m.Text));
        Assert.False(dialog.NextButton.IsEnabled, "Next is offered with no member chosen");
        Ui.Click(dialog.Members[0].Moved);
        Ui.Click(dialog.Members[1].Moved);
        Next(dialog);

        Assert.Equal("using System.Text;\n\nnamespace Shop;\n\npublic class Dog : Animal\n{\n}\n", window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal("using System.Text;\n\nnamespace Shop;\n\npublic class Animal\n{\n    public string Name = \"\";\n\n    public virtual string Sound() => \"...\";\n\n"
            + "    public int Legs => 4;\n\n    public string Describe()\n    {\n        StringBuilder text = new();\n        text.Append(Name).Append(' ').Append(Legs);\n"
            + "        return text.ToString();\n    }\n}\n", OpenText(window, "Animal.cs").ReplaceLineEndings("\n"));
        Assert.Equal("Pulled 2 members of Dog up to Animal", window.Status);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Pull Members Up? It changes Animal.cs and Dog.cs.")], asked);
        Assert.Equal(Dog, window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(Animal, OpenText(window, "Animal.cs"));
    }

    /// <summary>Push Members Down copies a member into each inheritor, and made abstract leaves it abstract in the base class.</summary>
    private void PushMembersDown(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Animal.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Anim");
        Assert.True(window.RunFromMenu(AppActions.PushMembersDown), window.Status);
        MembersMoveDialog dialog = Shown<MembersMoveDialog>(window, "Push Members Down");
        Assert.Equal(["Cat", "Dog"], dialog.Inheritors.Select(i => i.Text).Order(StringComparer.Ordinal));
        Assert.All(dialog.Inheritors, i => Assert.True(i.Box.IsChecked, $"{i.Text} is not chosen at first"));
        Assert.Equal(["Name: string", "Sound(): string"], dialog.Members.Select(m => m.Text));
        Ui.Click(dialog.Members[1].Moved);
        Ui.Click(dialog.Members[1].Abstract);
        Next(dialog);

        Assert.Equal("namespace Shop;\n\npublic abstract class Animal\n{\n    public string Name = \"\";\n\n    public abstract string Sound();\n}\n", window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(Dog.Replace("        return text.ToString();\n    }\n}\n", "        return text.ToString();\n    }\n\n    public override string Sound() => \"...\";\n}\n", StringComparison.Ordinal),
            File.ReadAllText(PathOf("Dog.cs")).ReplaceLineEndings("\n"));
        Assert.Equal(Cat.Replace("    public bool Purrs => true;\n", "    public bool Purrs => true;\n\n    public override string Sound() => \"...\";\n", StringComparison.Ordinal),
            File.ReadAllText(PathOf("Cat.cs")).ReplaceLineEndings("\n"));
        Assert.Equal("Pushed 1 member of Animal down to 2 classes", window.Status);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Single(asked);
        Assert.Equal(Animal, window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(Dog, File.ReadAllText(PathOf("Dog.cs")));
        Assert.Equal(Cat, File.ReadAllText(PathOf("Cat.cs")));
    }

    /// <summary>The text of a file open in the window, failing when it is not open.</summary>
    private static string OpenText(MainWindow window, string name) =>
        (window.EditorArea.Documents.FirstOrDefault(d => d.Name == name) ?? throw new Xunit.Sdk.XunitException($"{name} is not open")).Text.Snapshot.ToString();

    private MainWindow Open(string file)
    {
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        window.OpenFile(PathOf(file));
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        return window;
    }

    /// <summary>The dialog a refactoring opened, once it shows.</summary>
    private static T Shown<T>(MainWindow window, string what) where T : RefactoringDialog
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<T>().Any() || window.Status?.Contains(what, StringComparison.Ordinal) == true, $"{what}'s dialog to open");
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<T>().FirstOrDefault() ?? throw new Xunit.Sdk.XunitException($"{what} opened no dialog: {window.Status}"));
    }

    /// <summary>Next clicked, and the dialog waited out: closed once the refactoring is made, failing with what it says
    /// when it stays open.</summary>
    private static void Next(RefactoringDialog dialog)
    {
        Assert.True(dialog.NextButton.IsEnabled, $"Next is disabled: {dialog.Problem}");
        Ui.Click(dialog.NextButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem.Length > 0, "the refactoring to be made");
        Assert.False(dialog.IsVisible, $"The dialog stayed open: {dialog.Problem}");
    }

    /// <summary>Agrees to every question the window asks, keeping each one's title and text in order.</summary>
    private static List<(string Title, string Text)> Answer(MainWindow window)
    {
        List<(string, string)> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add((title, text));
            return Task.FromResult(true);
        };
        return asked;
    }

    private string PathOf(string name) => Path.Combine(folder, name);
}
