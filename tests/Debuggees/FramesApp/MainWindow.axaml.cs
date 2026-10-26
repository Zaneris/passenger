using Avalonia.Controls;

namespace FramesApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (Program.Lists)
        {
            StackPanel hundred = new() { Name = "Hundred" };
            for (int i = 0; i < 100; i++)
            {
                hundred.Children.Add(new Border { Name = $"Border{i}", Height = 2 });
            }
            StackPanel thousand = new() { Name = "Thousand" };
            for (int i = 0; i < 1000; i++)
            {
                thousand.Children.Add(new TextBlock { Name = $"Text{i}", Text = $"Text {i}" });
            }
            Panel.Children.Add(hundred);
            Panel.Children.Add(thousand);
        }
    }
}
