using Avalonia.Controls;
using Avalonia.Media;

namespace XamlBreakpointsApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    /// <summary>"paint": code-behind gives Save two backgrounds and two widths in turn.</summary>
    public void Paint()
    {
        Save.Background = Brushes.Green;
        Save.Background = Brushes.Blue;
        Save.Width = 100;
        Save.Width = 200;
        Console.WriteLine("Painted");
    }

    /// <summary>"warn": both buttons take the class a style of the window's turns red, Delete first.</summary>
    public void Warn()
    {
        Delete.Classes.Add("danger");
        Save.Classes.Add("danger");
        Console.WriteLine("Warned");
    }

    /// <summary>"nudge": Delete sets its own Background (SetCurrentValue), over the one its theme gives it.</summary>
    public void Nudge()
    {
        Delete.SetCurrentValue(Button.BackgroundProperty, Brushes.Purple);
        Console.WriteLine("Nudged");
    }

    /// <summary>"rename": the view model's caption changes, which the caption's binding shows.</summary>
    public void Rename() => ((MainViewModel)DataContext!).Caption = "Renamed";
}
