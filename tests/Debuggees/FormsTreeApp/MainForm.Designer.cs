namespace FormsTreeApp;

partial class MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        Strip = new MenuStrip();
        HelpItem = new ToolStripMenuItem();
        Count = new Button();
        Counter = new Label();
        NameBox = new TextBox();
        Hello = new Label();
        Bold = new Panel();
        Inside = new Label();
        List = new ListBox();
        Strip.SuspendLayout();
        Bold.SuspendLayout();
        SuspendLayout();
        // 
        // Strip
        // 
        Strip.Items.AddRange(new ToolStripItem[] { HelpItem });
        Strip.Location = new Point(0, 0);
        Strip.Name = "Strip";
        Strip.Size = new Size(480, 24);
        Strip.TabIndex = 0;
        // 
        // HelpItem
        // 
        HelpItem.Name = "HelpItem";
        HelpItem.Size = new Size(44, 20);
        HelpItem.Text = "Help";
        HelpItem.Click += OnHelp;
        // 
        // Count
        // 
        Count.Location = new Point(12, 36);
        Count.Name = "Count";
        Count.Size = new Size(100, 30);
        Count.TabIndex = 1;
        Count.Text = "Count";
        Count.UseVisualStyleBackColor = true;
        Count.Click += OnCount;
        // 
        // Counter
        // 
        Counter.AutoSize = true;
        Counter.Location = new Point(124, 43);
        Counter.Name = "Counter";
        Counter.Size = new Size(94, 15);
        Counter.TabIndex = 2;
        Counter.Text = "Not clicked yet";
        // 
        // NameBox
        // 
        NameBox.Location = new Point(12, 80);
        NameBox.Name = "NameBox";
        NameBox.Size = new Size(200, 23);
        NameBox.TabIndex = 3;
        NameBox.KeyDown += OnNameKey;
        // 
        // Hello
        // 
        Hello.AutoSize = true;
        Hello.Location = new Point(224, 84);
        Hello.Name = "Hello";
        Hello.Size = new Size(0, 15);
        Hello.TabIndex = 4;
        // 
        // Bold
        // 
        Bold.Controls.Add(Inside);
        Bold.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        Bold.Location = new Point(12, 116);
        Bold.Name = "Bold";
        Bold.Size = new Size(200, 40);
        Bold.TabIndex = 5;
        // 
        // Inside
        // 
        Inside.AutoSize = true;
        Inside.Location = new Point(3, 10);
        Inside.Name = "Inside";
        Inside.Size = new Size(120, 15);
        Inside.TabIndex = 0;
        Inside.Text = "Bold as its panel";
        // 
        // List
        // 
        List.FormattingEnabled = true;
        List.ItemHeight = 15;
        List.Location = new Point(12, 168);
        List.Name = "List";
        List.Size = new Size(200, 94);
        List.TabIndex = 6;
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(480, 280);
        Controls.Add(List);
        Controls.Add(Bold);
        Controls.Add(Hello);
        Controls.Add(NameBox);
        Controls.Add(Counter);
        Controls.Add(Count);
        Controls.Add(Strip);
        MainMenuStrip = Strip;
        Name = "MainForm";
        Text = "Forms Tree App";
        Strip.ResumeLayout(false);
        Strip.PerformLayout();
        Bold.ResumeLayout(false);
        Bold.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private MenuStrip Strip;
    private ToolStripMenuItem HelpItem;
    private Button Count;
    private Label Counter;
    private TextBox NameBox;
    private Label Hello;
    private Panel Bold;
    private Label Inside;
    private ListBox List;
}
