using Passenger.Languages.WinForms;
using Passenger.TestTables;

namespace Passenger.UnitTests.Languages;

/// <summary>Windows Forms designer code read as its designer writes it, from the file's parse alone: where each component of
/// a class's InitializeComponent is made and each of its properties set, which the Live Visual Tree links a running app's
/// controls to.</summary>
public sealed class WinFormsDesignerCodeTests
{
    [Fact]
    public void Designer_code_is_read_as_its_designer_writes_it()
    {
        Cases.All(
            ("each component's creation and property lines are found whether the code names them with this. or not, and a nested class's by its holder's name",
                Places));
    }

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
