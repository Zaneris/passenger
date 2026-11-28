using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The refactorings that ask their options in a dialog, each through its own keys and its dialog's controls on a
/// real project, computed by Passenger's extension in the real language server: Change Signature, Extract Interface,
/// Move to Another File, Move to Namespace, Pull Members Up and Push Members Down; and the code clones the extension finds,
/// compared side by side and made to share one method; Undo takes each back whole.</summary>
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

    private const string Coins = "namespace Shop;\n\npublic class Coin\n{\n    public int Value;\n\n    public static Coin Mint(int value, string metal = \"gold\") => new() { Value = value };\n}\n";

    private const string Purse = "using System;\n\nnamespace Shop;\n\npublic static class Purse\n{\n    public static int Fill()\n    {\n        Func<int, string, Coin> mint = Coin.Mint;\n"
        + "        return Coin.Mint(1).Value + mint(2, \"silver\").Value;\n    }\n}\n";

    /// <summary>Code clones: Total and Sum the same but for names and a literal, Header and Footer the same, Stamp and Mark
    /// the same but for the date's method, which no one method can take.</summary>
    private const string Ledger = "using System;\nusing System.Collections.Generic;\n\nnamespace Shop;\n\npublic static class Ledger\n{\n"
        + "    public static int Total(int[] prices)\n    {\n        int total = 0;\n        foreach (int price in prices)\n        {\n            if (price > 100)\n"
        + "            {\n                total += price * 9 / 10;\n            }\n            else\n            {\n                total += price;\n            }\n        }\n"
        + "        Console.WriteLine($\"Total of {prices.Length}: {total}\");\n        return total;\n    }\n\n"
        + "    public static void Header(List<string> lines)\n    {\n" + Notice + "    }\n\n"
        + "    public static string Stamp(DateTime when)\n    {\n        string day = when.ToShortDateString();\n        string time = when.ToShortTimeString();\n"
        + "        string zone = TimeZoneInfo.Local.StandardName;\n        int week = when.DayOfYear / 7 + 1;\n        return $\"{day} {time} ({zone}, week {week})\";\n    }\n}\n";

    private const string Tally = "using System;\nusing System.Collections.Generic;\n\nnamespace Shop;\n\npublic static class Tally\n{\n"
        + "    public static int Sum(int[] amounts)\n    {\n        int sum = 0;\n        foreach (int amount in amounts)\n        {\n            if (amount > 250)\n"
        + "            {\n                sum += amount * 9 / 10;\n            }\n            else\n            {\n                sum += amount;\n            }\n        }\n"
        + "        Console.WriteLine($\"Total of {amounts.Length}: {sum}\");\n        return sum;\n    }\n\n"
        + "    public static void Footer(List<string> lines)\n    {\n" + Notice + "    }\n\n"
        + "    public static string Mark(DateTime at)\n    {\n        string day = at.ToLongDateString();\n        string time = at.ToShortTimeString();\n"
        + "        string zone = TimeZoneInfo.Local.StandardName;\n        int week = at.DayOfYear / 7 + 1;\n        return $\"{day} {time} ({zone}, week {week})\";\n    }\n}\n";

    private const string Notice = "        lines.Add(\"Shop report\");\n        lines.Add(\"===========\");\n        lines.Add(DateTime.Today.ToShortDateString());\n"
        + "        lines.Add(\"Thank you for your custom\");\n        lines.Add(\"Please pay within thirty days\");\n        lines.Add(\"Goods sold are not returned\");\n"
        + "        lines.Add(\"Prices include tax\");\n";

    /// <summary>Clones alike but for names, where Once's one variable stands for Twice's two and is set between their uses.</summary>
    private const string Aliases = "namespace Shop;\n\npublic static class Aliases\n{\n    public static void Twice(int x, int y)\n    {\n"
        + "        System.Console.WriteLine(x);\n        x = 3;\n        System.Console.WriteLine(y);\n    }\n\n    public static void Once(int a)\n    {\n"
        + "        System.Console.WriteLine(a);\n        a = 3;\n        System.Console.WriteLine(a);\n    }\n}\n";

    private const string Pets = "namespace Shop;\n\npublic interface IPet\n{\n    string Name { get; }\n}\n\npublic class Fish : IPet\n{\n    public string Name => \"Nemo\";\n\n"
        + "    public int Fins => 2;\n}\n";

    private string folder = string.Empty;

    /// <summary>Every dialog's refactoring made in one window on one project, each through its own keys or menu item and
    /// its dialog's controls, and each taken back whole by Undo before the next: the project and its language server are
    /// made once, and no refactoring touches a closed file another wrote a moment before, which a refactoring waits for. Each
    /// file a refactoring changes is checked whole.</summary>
    [AvaloniaFact]
    public void Each_refactoring_dialog_changes_every_file_it_must_and_undo_takes_each_back_whole()
    {
        folder = PathNames.Canonical(TestFolders.Create("refactorings-"));
        File.WriteAllText(PathOf("Shop.csproj"), TestProjects.Library("<Nullable>enable</Nullable>"));
        (string Name, string Text)[] files = [("Calc.cs", Calc), ("Till.cs", Till), ("Order.cs", Order), ("OrderBook.cs", OrderBook),
            ("Report.cs", Report), ("Shapes.cs", Shapes), ("Animal.cs", Animal), ("Dog.cs", Dog), ("Cat.cs", Cat), ("Coins.cs", Coins), ("Purse.cs", Purse),
            ("Ledger.cs", Ledger), ("Tally.cs", Tally), ("Pets.cs", Pets), ("Aliases.cs", Aliases)];
        foreach ((string name, string text) in files)
        {
            File.WriteAllText(PathOf(name), text);
        }
        ChildResult restored = Restore.Run(folder);
        Assert.True(restored.Succeeded, $"The project did not restore:\n{restored.Output}{restored.Error}");
        MainWindow window = Open("Calc.cs");
        List<(string Title, string Text)> asked = Answer(window);

        ChangeSignature(window, asked);
        ChangeSignatureNamingAMethodGroup(window, asked);
        ExtractInterface(window, asked);
        MoveToNamespace(window, asked);
        MoveToAnotherFile(window, asked);
        PushMembersDown(window, asked);
        PullMembersUp(window, asked);
        PullIntoAnInterfaceFromContextActions(window, asked);
        CodeClones(window, asked);
        window.Close();
    }

    /// <summary>Find Code Clones lists each group of the solution's clones with how alike they are and where each lies; two
    /// renamed clones compare side by side; Extract Method from Clones makes them one method, the literal they differ in its
    /// parameter, every clone called in its place as one command Undo takes back whole; a group whose clones call different
    /// methods is refused, saying so; Find Clones of Selection finds the statements chosen elsewhere; and a group where one
    /// clone's variable stands for two of another's and is set between their uses is refused, saying so.</summary>
    private void CodeClones(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Ledger.cs"));
        Assert.True(window.Perform(AppActions.FindClones), Ui.Said(window));
        Ui.Settle();
        Ui.WaitFor(() => window.ClonesWindow.Found is not null, () => $"the clones to be found, where the status bar says {window.Status}");
        Assert.StartsWith("3 groups of code clones in the solution (", window.Status, StringComparison.Ordinal);
        IReadOnlyList<CloneSet> sets = window.ClonesWindow.Found!.Sets;
        Assert.Equal("Renamed, 79% alike: Ledger.cs:10–23 Ledger.Total, Tally.cs:10–23 Tally.Sum\nRenamed, 93% alike: Ledger.cs:39–43 Ledger.Stamp, Tally.cs:39–43 Tally.Mark\n"
            + "Exact: Ledger.cs:28–34 Ledger.Header, Tally.cs:28–34 Tally.Footer",
            string.Join("\n", sets.Select(s => $"{s.Kind}: {string.Join(", ", s.Clones.Select(c => $"{c.Shown(folder)} {c.Member}"))}")));
        Assert.Equal(["Renamed, 79% alike", "Ledger.cs:10–23", "Tally.cs:10–23", "Renamed, 93% alike", "Exact"],
            window.ClonesWindow.Rows.Select(r => r.Clone?.Shown(folder) ?? r.Set.Kind));

        window.ClonesWindow.Choose(sets[0], sets[0].Clones[1]);
        Assert.True(window.Perform(AppActions.CompareClones), Ui.Said(window));
        Ui.Settle();
        Passenger.App.Vcs.DiffView compared = OpenDiffs.ViewOf(OpenDiffs.Landed(window, "the clones side by side"));
        Assert.StartsWith("        int total = 0;\n", compared.Left.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("        int sum = 0;\n", compared.Right.Document.Snapshot.ToString(), StringComparison.Ordinal);

        window.ClonesWindow.Choose(sets[1]);
        Ui.Settle();
        Assert.Equal("The clones name different things: ToShortDateString at Ledger.cs:39, ToLongDateString at Tally.cs:39; one method can't take both",
            window.Actions.WhyNot(AppActions.ExtractClones, window.Actions.Context())?.Shown);

        window.ClonesWindow.Choose(sets[0]);
        Ui.Settle();
        OpenDocument ledger = window.EditorArea.Documents.First(d => d.Name == "Ledger.cs");
        int version = ledger.Text.Version;
        Assert.True(window.Perform(AppActions.ExtractClones), Ui.Said(window));
        Ui.Settle();
        Ui.WaitFor(() => ledger.Text.Version != version, () => $"the clones to be extracted, where the status bar says {window.Status}");
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Same(ledger.Text, editor.Document);
        Assert.Equal("NewMethod", window.Assist.Renaming);
        Ui.Press(window, "ESCAPE");
        string body = "        int total = 0;\n        foreach (int price in prices)\n        {\n            if (price > 100)\n            {\n                total += price * 9 / 10;\n"
            + "            }\n            else\n            {\n                total += price;\n            }\n        }\n        Console.WriteLine($\"Total of {prices.Length}: {total}\");\n"
            + "        return total;\n";
        Assert.Equal(Ledger.Replace(body, "        return NewMethod(prices, 100);\n    }\n\n    internal static int NewMethod(int[] prices, int value)\n    {\n"
            + body.Replace("price > 100", "price > value", StringComparison.Ordinal), StringComparison.Ordinal), editor.Document.Snapshot.ToString());
        int sumFrom = Tally.IndexOf("        int sum = 0;", StringComparison.Ordinal);
        int sumTo = Tally.IndexOf("        return sum;\n", StringComparison.Ordinal) + "        return sum;\n".Length;
        Assert.Equal(Tally[..sumFrom] + "        return Ledger.NewMethod(amounts, 250);\n" + Tally[sumTo..], File.ReadAllText(PathOf("Tally.cs")));
        Assert.StartsWith("Extracted a method from 2 clones in 2 files", window.Status, StringComparison.Ordinal);
        Assert.Equal(["Renamed, 93% alike", "Exact"], window.ClonesWindow.Rows.Where(r => r.Clone is null).Select(r => r.Set.Kind));

        editor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Extract Method from Clones? It changes Ledger.cs and Tally.cs.")], asked);
        Assert.Equal(Ledger, editor.Document.Snapshot.ToString());
        Assert.Equal(Tally, File.ReadAllText(PathOf("Tally.cs")));

        window.OpenFile(PathOf("Tally.cs"));
        Ui.Settle();
        string tally = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        int from = tally.IndexOf("        lines.Add(\"Shop", StringComparison.Ordinal);
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(new Selection(from, from + Notice.Length));
        Assert.True(window.Perform(AppActions.FindClonesOfSelection), Ui.Said(window));
        Ui.Settle();
        Ui.WaitFor(() => window.ClonesWindow.Found?.Searched.EndsWith("Tally.cs", StringComparison.Ordinal) == true,
            () => $"the selection's clones to be found, where the status bar says {window.Status}");
        Assert.Equal(["Exact: Tally.cs:28–34, Ledger.cs:28–34"],
            window.ClonesWindow.Found!.Sets.Select(s => $"{s.Kind}: {string.Join(", ", s.Clones.Select(c => c.Shown(folder)))}"));

        window.OpenFile(PathOf("Aliases.cs"));
        Ui.Settle();
        int twice = Aliases.IndexOf("        System.Console.WriteLine(x);", StringComparison.Ordinal);
        int twiceEnd = Aliases.IndexOf("WriteLine(y);", StringComparison.Ordinal) + "WriteLine(y);".Length;
        window.EditorArea.CodeEditor.Selections = SelectionSet.Single(new Selection(twice, twiceEnd));
        Assert.True(window.Perform(AppActions.FindClonesOfSelection), Ui.Said(window));
        Ui.Settle();
        Ui.WaitFor(() => window.ClonesWindow.Found?.Searched.EndsWith("Aliases.cs", StringComparison.Ordinal) == true,
            () => $"the selection's clones to be found, where the status bar says {window.Status}");
        Assert.Equal(["Aliases.cs:7–9, Aliases.cs:14–16"], window.ClonesWindow.Found!.Sets.Select(s => string.Join(", ", s.Clones.Select(c => c.Shown(folder)))));
        window.ClonesWindow.Choose(window.ClonesWindow.Found.Sets[0]);
        Ui.Settle();
        Assert.Equal("a at Aliases.cs:14 stands for x and y at Aliases.cs:7 and is set there, which one method taking them apart can't do",
            window.Actions.WhyNot(AppActions.ExtractClones, window.Actions.Context()));
    }

    /// <summary>Change Signature adds, removes and reorders parameters, and every call passes its arguments in the new order,
    /// a change of texts alone that makes no Explorer row again and has the project checked no more.</summary>
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
        FilesFollowed.Check(window, "Change Signature", [PathOf("Calc.cs"), PathOf("Till.cs")], () => Next(dialog), textsAlone: true);

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

    /// <summary>Change Signature changes every call but a method group, which it cannot change: once the dialog is done,
    /// the status bar names the group and why, the delegate it makes no longer fitting the new parameters.</summary>
    private void ChangeSignatureNamingAMethodGroup(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Coins.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Coin Mi");
        Ui.Run(window, AppActions.ChangeSignature);
        ChangeSignatureDialog dialog = Shown<ChangeSignatureDialog>(window, "Change Signature");
        Ui.Click(dialog.Parameters[0].NameBox);
        Ui.Click(dialog.AddButton);
        dialog.KeyTextInput("int");
        Ui.Settle();
        Ui.Click(dialog.Parameters[1].NameBox);
        dialog.KeyTextInput("year");
        Ui.Click(dialog.Parameters[1].DefaultBox);
        dialog.KeyTextInput("2026");
        Ui.Settle();
        Assert.Equal("public static Coin Mint(int value, int year, string metal = \"gold\")", dialog.Preview);
        Next(dialog);

        Assert.Equal(Coins.Replace("Mint(int value, string metal", "Mint(int value, int year, string metal", StringComparison.Ordinal), window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(Purse.Replace("Coin.Mint(1)", "Coin.Mint(1, 2026)", StringComparison.Ordinal), File.ReadAllText(PathOf("Purse.cs")));
        Assert.Equal("Changed the signature of Coin.Mint in 2 files, but left 1 use as it was: Purse.cs:9, Coin.Mint as a Func<int, string, Coin>, "
            + "which the new signature no longer fits", window.Status);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Change Signature? It changes Coins.cs and Purse.cs.")], asked);
        Assert.Equal(Coins, window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal(Purse, File.ReadAllText(PathOf("Purse.cs")));
    }

    /// <summary>Extract Interface makes the interface of the members chosen in a file of its own, whose row alone comes into
    /// the Explorer, and the class implements it.</summary>
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
        string extracted = PathOf("IOrder.cs");
        FilesFollowed.Check(window, "Extract Interface", [extracted], () => Next(dialog));

        Assert.True(File.Exists(extracted), $"Extract Interface made no IOrder.cs: {window.Status}");
        Assert.Equal("namespace Shop;\n\npublic interface IOrder\n{\n    int Total { get; }\n\n    void Add(int amount);\n}\n", File.ReadAllText(extracted).ReplaceLineEndings("\n"));
        Assert.Equal(Order.Replace("public class Order", "public class Order : IOrder", StringComparison.Ordinal), window.EditorArea.Active?.Text.Snapshot.ToString());
        Assert.Equal("Extracted the interface IOrder from Order to IOrder.cs", window.Status);
        Assert.Contains(window.ExplorerView.Tree.Rows, r => r.Path == extracted);
        Ui.WaitFor(() => window.CSharpService!.For(window.EditorArea.Active!)!.Diagnostics.All(d => d.Severity != Passenger.Lsp.DiagnosticSeverity.Error), "the language server to take IOrder.cs in");

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", "Undo Extract Interface? It changes Order.cs, and deletes IOrder.cs.")], asked);
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
        Assert.Equal([("Undo", "Undo Move to Another File? It changes Shapes.cs, and deletes Circle.cs.")], asked);
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

    /// <summary>Pull Members Up is where a refactoring of a member is looked for, Refactor This and the editor's Refactor
    /// submenu, which offers C#'s own refactorings in a C# file, and Show Context Actions on the member's name pulls it up
    /// into the interface its class implements that lacks it, with no dialog, which Undo takes back whole.</summary>
    private void PullIntoAnInterfaceFromContextActions(MainWindow window, List<(string Title, string Text)> asked)
    {
        asked.Clear();
        window.OpenFile(PathOf("Pets.cs"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int Fi");
        Ui.Run(window, AppActions.RefactorThis);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, () => $"Refactor This's list: {window.Status}");
        IReadOnlyList<string> offered = window.Assist.Offered;
        Assert.True(offered.Contains("Pull Members Up…") && !offered.Contains("Extract Method…"),
            $"Refactor This on a member offers Passenger's refactorings that apply, never those the server has none of here: {string.Join("; ", offered)}");
        Ui.Press(window, "ESCAPE");

        int fins = editor.Document.Snapshot.ToString().IndexOf("Fins", StringComparison.Ordinal) + 1;
        Ui.RightClick(editor, editor.PointOf(fins) + new Vector(1, editor.LineHeight / 2));
        MenuItem refactor = window.OpenContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Refactor")
            ?? throw new Xunit.Sdk.XunitException("The editor's menu has no Refactor submenu");
        IReadOnlyList<string> refactorings = [.. refactor.Items.OfType<MenuItem>().Where(i => i.IsEnabled).Select(i => i.Header as string ?? string.Empty)];
        string[] lacking = [.. ((string[])["Change Signature…", "Extract Interface…", "Move…", "Safe Delete…", "Pull Members Up…", "Push Members Down…"]).Where(r => !refactorings.Contains(r))];
        Assert.True(lacking.Length == 0, $"The editor's Refactor submenu on a C# member lacks, or greys, the main menu's {string.Join(", ", lacking)}: {string.Join("; ", refactorings)}");
        window.OpenContextMenu!.Close();
        Ui.Settle();

        Ui.PlaceAfter(editor, "int Fi");
        Ui.Run(window, AppActions.QuickFix);
        const string pull = "Pull 'Fins' up into 'IPet'";
        Ui.WaitFor(() => window.Assist.Offered.Contains(pull), () => $"'{pull}' among the actions offered: {string.Join("; ", window.Assist.Offered)}; the status bar said: {window.Status}");
        for (int down = window.Assist.Offered.ToList().IndexOf(pull); down > 0; down--)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Status == "Pulled 1 member of Fish up to IPet", () => $"the member to be pulled up: {window.Status}");
        Assert.Equal(Pets.Replace("    string Name { get; }\n", "    string Name { get; }\n\n    int Fins { get; }\n", StringComparison.Ordinal), window.EditorArea.Active?.Text.Snapshot.ToString());

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Pets, window.EditorArea.Active?.Text.Snapshot.ToString());
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
    internal static T Shown<T>(MainWindow window, string what) where T : RefactoringDialog
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<T>().Any() || window.Status?.Contains(what, StringComparison.Ordinal) == true, $"{what}'s dialog to open");
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<T>().FirstOrDefault() ?? throw new Xunit.Sdk.XunitException($"{what} opened no dialog: {window.Status}"));
    }

    /// <summary>Next clicked, and the dialog waited out: closed once the refactoring is made, failing with what it says
    /// when it stays open.</summary>
    internal static void Next(RefactoringDialog dialog)
    {
        Assert.True(dialog.NextButton.IsEnabled, $"Next is disabled: {dialog.Problem}");
        Ui.Click(dialog.NextButton);
        Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem.Length > 0, "the refactoring to be made");
        Assert.False(dialog.IsVisible, $"The dialog stayed open: {dialog.Problem}");
    }

    /// <summary>Agrees to every question the window asks, keeping each one's title and text in order.</summary>
    internal static List<(string Title, string Text)> Answer(MainWindow window)
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
