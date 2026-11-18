using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AgentApp;

public partial class MainWindow : Window
{
    private int clicks;

    private readonly List<string> held = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new Person();
        for (int i = 1; i <= 30; i++)
        {
            Items.Children.Add(new TextBlock { Text = $"Item {i}" });
        }
    }

    private void OnCount(object? sender, RoutedEventArgs e)
    {
        clicks++;
        Counter.Text = clicks == 1 ? "Clicked once" : $"Clicked {clicks} times";
    }

    /// <summary>Enter greets the name; a key held with Ctrl, Alt or Meta that the box takes no part in is said by where it is
    /// on the keyboard, each after the last.</summary>
    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Greeting.Text = $"Hello, {NameBox.Text}";
            e.Handled = true;
        }
        else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            held.Add($"{e.KeyModifiers}+{e.PhysicalKey}");
            Greeting.Text = $"Pressed {string.Join(' ', held)}";
            e.Handled = true;
        }
    }
}

/// <summary>What the window binds to; its caption's binding misspells Name.</summary>
public sealed class Person
{
    public string Name { get; set; } = "Ada";
}
