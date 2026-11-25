using Passenger.Core;
using Passenger.Languages.WinForms;
using Passenger.TestTables;

namespace Passenger.UnitTests.Languages;

/// <summary>Windows Forms designer code read as its designer writes it, from the file's parse alone: where each component of
/// a class's InitializeComponent is made and each of its properties set, which the Live Visual Tree links a running app's
/// controls to, and each value as the code writes it, which the designer makes.</summary>
public sealed class WinFormsDesignerCodeTests
{
    [Fact]
    public void Designer_code_is_read_as_its_designer_writes_it()
    {
        Cases.All(
            ("each component's creation and property lines are found whether the code names them with this. or not, and a nested class's by its holder's name",
                Places),
            ("each value is read as the code writes it, its names left for the designer, and a form's fields, usings and base with it", Values));
    }

    private static void Values()
    {
        const string Designer = """
            using Shop.Controls;

            namespace Shop;

            partial class MainForm
            {
                private void InitializeComponent()
                {
                    this.grid = new System.Windows.Forms.DataGridView();
                    ((System.ComponentModel.ISupportInitialize)(this.grid)).BeginInit();
                    grid.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    grid.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
                    grid.BackColor = Color.FromArgb(((int)(((byte)(255)))), 128, 0);
                    grid.Location = new(-3, 12);
                    grid.Tag = typeof(Gauge);
                    grid.Text = null;
                    Controls.AddRange(new Control[] { grid });
                    grid.Click += (s, e) => { };
                }

                private System.Windows.Forms.DataGridView grid;
            }
            """;
        DesignerSource read = DesignerCode.Read(Designer, "Shop.MainForm") ?? throw new InvalidOperationException("MainForm's designer code was not read");
        Assert.Equal("Shop", read.Namespace);
        Assert.Equal(["Shop.Controls"], read.Usings);
        Assert.Equal([new DesignerField("grid", "System.Windows.Forms.DataGridView")], read.Fields);
        Assert.Equal((DesignerAct.Call, "grid", "BeginInit"), (read.Statements[1].Act, read.Statements[1].Target, read.Statements[1].Member));
        Assert.Equal(["new System.Windows.Forms.DataGridView()", "or(AnchorStyles.Top, AnchorStyles.Left)", "new Font(Segoe UI string, 9 float, FontStyle.Bold)",
            "call Color.FromArgb(cast int(cast byte(255 int)), 128 int, 0 int)", "new ?(negate(3 int), 12 int)", "typeof Gauge", "null null", "unread (s, e) => { }"],
            read.Statements.Where(s => s.Value is not null).Select(s => Said(s.Value!)));
        Assert.Equal(["array Control[](grid)"], read.Statements[^2].Arguments!.Select(Said));
        (string Base, IReadOnlyList<string> Usings) based = DesignerCode.BaseOf("using System;\nnamespace Shop;\npublic partial class MainForm : Form { }", "Shop.MainForm")
            ?? throw new InvalidOperationException("MainForm's base was not read");
        Assert.Equal("Form", based.Base);
        Assert.Equal(["System"], based.Usings);
    }

    /// <summary>A value as the test writes it: its kind's words, then what it holds.</summary>
    private static string Said(DesignerValue value) => value.Kind switch
    {
        DesignerValueKinds.Literal => $"{value.Text ?? "null"} {value.Type}",
        DesignerValueKinds.Name => value.Name!,
        DesignerValueKinds.New => $"new {value.Type ?? "?"}({string.Join(", ", value.Arguments!.Select(Said))})",
        DesignerValueKinds.Call => $"call {value.Name}({string.Join(", ", value.Arguments!.Select(Said))})",
        DesignerValueKinds.Array => $"array {value.Type}[]({string.Join(", ", value.Arguments!.Select(Said))})",
        DesignerValueKinds.Cast => $"cast {value.Type}({Said(value.Arguments![0])})",
        DesignerValueKinds.Or => $"or({Said(value.Arguments![0])}, {Said(value.Arguments![1])})",
        DesignerValueKinds.Negate => $"negate({Said(value.Arguments![0])})",
        DesignerValueKinds.TypeOf => $"typeof {value.Type}",
        _ => $"{value.Kind} {value.Text}",
    };

    private static void Places()
    {
        const string Short = """
            namespace Shop;

            partial class MainForm
            {
                private void InitializeComponent()
                {
                    Go = new Button();
                    SuspendLayout();
                    Go.FlatAppearance.BorderSize = 0;
                    Go.Text = "Go";
                    Go.Click += OnGo;
                    Controls.Add(Go);
                    Text = "Shop";
                }

                private Button Go;
            }
            """;
        DesignerSource read = DesignerCode.Read(Short, "Shop.MainForm") ?? throw new InvalidOperationException("MainForm's designer code was not read");
        Assert.Equal(
            (Short.IndexOf("Go = new", StringComparison.Ordinal), Short.IndexOf("Go.Text", StringComparison.Ordinal), Short.IndexOf("Go.FlatAppearance", StringComparison.Ordinal),
                Short.IndexOf("Text = \"Shop\"", StringComparison.Ordinal), Short.IndexOf("InitializeComponent", StringComparison.Ordinal)),
            (read.MadeAt("Go"), read.SetAt("Go", "Text"), read.SetAt("Go", "FlatAppearance"), read.SetAt(string.Empty, "Text"), read.MadeAt(string.Empty)));
        Assert.Null(read.SetAt("Go", "Click"));
        Assert.Equal([DesignerAct.Create, DesignerAct.Call, DesignerAct.Assign, DesignerAct.Assign, DesignerAct.Hook, DesignerAct.Call, DesignerAct.Assign], read.Statements.Select(s => s.Act));

        const string Qualified = """
            namespace Shop.Views
            {
                partial class Outer
                {
                    partial class Inner
                    {
                        private void InitializeComponent()
                        {
                            this.go = new System.Windows.Forms.Button();
                            this.go.Text = "Go";
                            this.Text = "Inner";
                        }

                        private System.Windows.Forms.Button go;
                    }
                }
            }
            """;
        DesignerSource inner = DesignerCode.Read(Qualified, "Shop.Views.Outer+Inner") ?? throw new InvalidOperationException("Inner's designer code was not read");
        Assert.Equal((Qualified.IndexOf("go = new", StringComparison.Ordinal), Qualified.IndexOf("go.Text", StringComparison.Ordinal), Qualified.IndexOf("Text = \"Inner\"", StringComparison.Ordinal)),
            (inner.MadeAt("go"), inner.SetAt("go", "Text"), inner.SetAt(string.Empty, "Text")));
        Assert.Null(DesignerCode.Read(Qualified, "Shop.Views.Inner"));
    }
}
