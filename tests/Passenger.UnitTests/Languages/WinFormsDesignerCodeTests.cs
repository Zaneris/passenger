using Passenger.Core;
using Passenger.Languages.WinForms;
using Passenger.TestTables;

namespace Passenger.UnitTests.Languages;

/// <summary>Windows Forms designer code read as its designer writes it, from the file's parse alone: where each component of
/// a class's InitializeComponent is made and each of its properties set, which the Live Visual Tree links a running app's
/// controls to, and each value as the code writes it, which the designer makes; and a design's change written into it,
/// touching nothing else.</summary>
public sealed class WinFormsDesignerCodeTests
{
    [Fact]
    public void Designer_code_is_read_as_its_designer_writes_it()
    {
        Cases.All(
            ("each component's creation and property lines are found whether the code names them with this. or not, and a nested class's by its holder's name",
                Places),
            ("each value is read as the code writes it, its names left for the designer, and a form's fields, usings and base with it", Values),
            ("an added control's creation, section under its comment, Controls.Add and field go where the designer writes them, a moved one's line is written again, and the file's line breaks are kept",
                Added),
            ("a deleted control takes its lines, its section's comment and its field with it, a statement first in a section goes under that section's comment, and a class declaring no component's field takes the first past its #endregion",
                Deleted));
    }

    /// <summary>A form of a button and a panel as .NET's designer writes it, with CRLF line breaks.</summary>
    private const string Form = """
        namespace Shop
        {
            partial class MainForm
            {
                #region Windows Form Designer generated code

                private void InitializeComponent()
                {
                    ok = new Button();
                    Side = new Panel();
                    SuspendLayout();
                    // 
                    // ok
                    // 
                    ok.Location = new Point(12, 12);
                    ok.Name = "ok";
                    ok.Click += OnOk;
                    // 
                    // Side
                    // 
                    Side.Location = new Point(12, 48);
                    Side.Name = "Side";
                    // 
                    // MainForm
                    // 
                    ClientSize = new Size(300, 200);
                    Controls.Add(Side);
                    Controls.Add(ok);
                    Name = "MainForm";
                    ResumeLayout(false);
                }

                #endregion

                private Button ok;
                private Panel Side;
            }
        }

        """;

    private static void Added()
    {
        string text = Form.ReplaceLineEndings("\r\n");
        DesignerSource read = DesignerCode.Read(text, "Shop.MainForm") ?? throw new InvalidOperationException("MainForm's designer code was not read");
        string made = Applied(text, DesignerWriter.Edits(text, read,
        [
            new DesignerChange(DesignerChangeKinds.Insert, 1) { Text = "label1 = new Label();" },
            new DesignerChange(DesignerChangeKinds.Replace, 3) { Text = "ok.Location = new Point(20, 12);" },
            new DesignerChange(DesignerChangeKinds.Insert, 7) { Text = "label1.AutoSize = true;", Header = "label1" },
            new DesignerChange(DesignerChangeKinds.Insert, 7) { Text = "label1.Name = \"label1\";" },
            new DesignerChange(DesignerChangeKinds.Insert, 8) { Text = "Controls.Add(label1);" },
            new DesignerChange(DesignerChangeKinds.Declare, 1) { Text = "private Label label1;" },
        ]));
        string expected = Form.Replace("            Side = new Panel();\n", "            Side = new Panel();\n            label1 = new Label();\n")
            .Replace("ok.Location = new Point(12, 12);", "ok.Location = new Point(20, 12);")
            .Replace("            Side.Name = \"Side\";\n", "            Side.Name = \"Side\";\n            // \n            // label1\n            // \n            label1.AutoSize = true;\n            label1.Name = \"label1\";\n")
            .Replace("            ClientSize = new Size(300, 200);\n", "            ClientSize = new Size(300, 200);\n            Controls.Add(label1);\n")
            .Replace("        private Panel Side;\n", "        private Panel Side;\n        private Label label1;\n");
        Assert.Equal(expected.ReplaceLineEndings("\r\n"), made);
    }

    private static void Deleted()
    {
        DesignerSource read = DesignerCode.Read(Form, "Shop.MainForm") ?? throw new InvalidOperationException("MainForm's designer code was not read");
        string made = Applied(Form, DesignerWriter.Edits(Form, read,
        [
            new DesignerChange(DesignerChangeKinds.Remove, 0),
            new DesignerChange(DesignerChangeKinds.Remove, 3),
            new DesignerChange(DesignerChangeKinds.Remove, 4),
            new DesignerChange(DesignerChangeKinds.Remove, 5),
            new DesignerChange(DesignerChangeKinds.Remove, 10),
            new DesignerChange(DesignerChangeKinds.Insert, 6) { Text = "Side.BackColor = Color.Red;", Before = true },
            new DesignerChange(DesignerChangeKinds.Undeclare, 0),
        ]));
        string expected = Form.Replace("            ok = new Button();\n", string.Empty)
            .Replace("            // \n            // ok\n            // \n            ok.Location = new Point(12, 12);\n            ok.Name = \"ok\";\n            ok.Click += OnOk;\n", string.Empty)
            .Replace("            Side.Location", "            Side.BackColor = Color.Red;\n            Side.Location")
            .Replace("            Controls.Add(ok);\n", string.Empty)
            .Replace("        private Button ok;\n", string.Empty);
        Assert.Equal(expected, made);
        string bare = "namespace Shop;\n\npartial class Empty\n{\n    #region Windows Form Designer generated code\n\n    private void InitializeComponent()\n    {\n        SuspendLayout();\n        ResumeLayout(false);\n    }\n\n    #endregion\n}\n";
        DesignerSource empty = DesignerCode.Read(bare, "Shop.Empty") ?? throw new InvalidOperationException("Empty's designer code was not read");
        Assert.Equal(bare.Replace("    #endregion\n", "    #endregion\n\n    private Button button1;\n"),
            Applied(bare, DesignerWriter.Edits(bare, empty, [new DesignerChange(DesignerChangeKinds.Declare, -1) { Text = "private Button button1;" }])));
    }

    /// <summary>A text with edits made to it, the last first.</summary>
    private static string Applied(string text, IReadOnlyList<Passenger.Text.TextEdit> edits)
    {
        Assert.NotEmpty(edits);
        string made = text;
        foreach (Passenger.Text.TextEdit edit in edits.OrderByDescending(e => e.Start))
        {
            made = made[..edit.Start] + edit.Text + made[(edit.Start + edit.Length)..];
        }
        return made;
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
