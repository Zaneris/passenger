using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Godot;

/// <summary>The project's resources a property may take, each with its thumbnail and path, filtered as a name is typed:
/// a click, or Enter, chooses one; Escape leaves it. Shown beside its button (<see cref="IGodotTuner.ShowPopup"/>).</summary>
public sealed class GodotResourcePicker : Border
{
    private readonly ListBox list;
    private readonly TextBox filter;
    private readonly TextBlock empty;
    private readonly string folder;
    private IReadOnlyList<string> all = [];

    public GodotResourcePicker(string folder, IReadOnlyList<string> classes, string? current)
    {
        this.folder = folder;
        Classes.Add(StyleClasses.Popup);
        Width = PopupFrame.Narrow;
        Padding = new Thickness(8);
        filter = new TextBox { Classes = { StyleClasses.Search }, PlaceholderText = classes.Count > 0 ? $"Filter {string.Join(", ", classes)}" : "Filter resources" };
        list = new ListBox { Classes = { StyleClasses.Tree }, Height = ListRow.Height * 12 };
        ReusedRows.Use(list, Row);
        empty = new TextBlock { Classes = { StyleClasses.EmptyText }, Text = "Looking for the project's resources…" };
        filter.TextChanged += (_, _) => Show();
        filter.KeyDown += (_, e) =>
        {
            if (ListKeys.Move(list, e))
            {
                e.Handled = true;
            }
        };
        list.PointerReleased += (_, _) => Choose();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Classes = { StyleClasses.PopupTitle }, Text = "Choose a Resource" },
                filter,
                new Panel { Children = { list, empty } },
            },
        };
        IdeErrors.Observe(FindAsync(classes, current));
        Dispatcher.UIThread.Post(() => filter.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The resource chosen (its <c>res://</c> path), or null when the list was left.</summary>
    public event EventHandler<string?>? Chosen;

    /// <summary>The resources listed, as the filter leaves them.</summary>
    public IReadOnlyList<string> Listed => list.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>Chooses a resource by its path, as a click on its row does.</summary>
    public void Choose(string path)
    {
        list.SelectedItem = path;
        Choose();
    }

    private async Task FindAsync(IReadOnlyList<string> classes, string? current)
    {
        all = await BackgroundWork.Run(() => GodotResources.Under(folder, classes));
        Show();
        if (current is not null && Listed.Contains(current))
        {
            list.SelectedItem = current;
        }
        else if (Listed.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    private void Show()
    {
        string typed = filter.Text?.Trim() ?? string.Empty;
        list.ItemsSource = typed.Length == 0 ? all : all.Where(p => p.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        empty.Text = all.Count == 0 ? "The project has no resource it may take" : "No resource's path has those letters";
        empty.IsVisible = Listed.Count == 0;
    }

    private void Choose()
    {
        if (list.SelectedItem is string chosen)
        {
            Chosen?.Invoke(this, chosen);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Chosen?.Invoke(this, null);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Choose();
        }
    }

    /// <summary>A resource's row: its thumbnail, its file's name, and its folder muted after it.</summary>
    private Control Row()
    {
        Image thumbnail = new() { Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0) };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock where = new() { Classes = { StyleClasses.Muted }, FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        StackPanel line = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Margin = new Thickness(4, 0), Children = { thumbnail, name, where } };
        return line.Showing<string>(path =>
        {
            name.Text = path.Split('/')[^1];
            string under = path[..(path.Length - name.Text.Length)];
            where.Text = under == Passenger.Workspace.GodotResPaths.Scheme ? string.Empty : under;
            IdeErrors.Observe(ThumbnailAsync(thumbnail, path));
        });
    }

    private async Task ThumbnailAsync(Image image, string path)
    {
        image.Tag = path;
        image.Source = null;
        Avalonia.Media.Imaging.Bitmap? made = Passenger.Workspace.GodotResPaths.FileOf(folder, path) is { } file ? await GodotResources.ThumbnailAsync(file, 36) : null;
        if (Equals(image.Tag, path))
        {
            image.Source = made;
        }
    }
}

/// <summary>What Keep will write, shown before it writes: each value's file and line, the line as it reads now struck
/// through (none for a line added) and as it will read. Keep (or Enter) writes them, Cancel or Escape leaves them.</summary>
public sealed class GodotKeepPreview : Border
{
    public GodotKeepPreview(string title, IReadOnlyList<GodotKept> kept, IReadOnlyList<string> refused)
    {
        Classes.Add(StyleClasses.Popup);
        Width = PopupFrame.Medium;
        Padding = new Thickness(12, 10);
        StackPanel page = new() { Spacing = 8 };
        page.Children.Add(new TextBlock { Classes = { StyleClasses.PopupTitle }, Text = title });
        foreach (GodotKept one in kept)
        {
            StackPanel block = new() { Spacing = 2 };
            block.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Text = $"{Path.GetFileName(one.File)}:{one.Line + 1}{(one.Before is null ? ", a line added" : string.Empty)}" });
            if (one.Before is { } before)
            {
                block.Children.Add(Line("− ", before, struck: true));
            }
            block.Children.Add(Line("+ ", one.After, struck: false));
            page.Children.Add(block);
        }
        foreach (string why in refused)
        {
            page.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = why });
        }
        KeepButton = new Button { Content = "Keep", Classes = { StyleClasses.Primary }, IsEnabled = kept.Count > 0 };
        KeepButton.Click += (_, _) => Done?.Invoke(this, true);
        StackPanel answers = DialogFooter.Popup(KeepButton, () => Done?.Invoke(this, false));
        answers.HorizontalAlignment = HorizontalAlignment.Right;
        page.Children.Add(answers);
        Child = page;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Done?.Invoke(this, false);
            }
            else if (e.Key == Key.Enter && kept.Count > 0)
            {
                e.Handled = true;
                Done?.Invoke(this, true);
            }
        }, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() => KeepButton.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>Keep (true) or Cancel (false) pressed.</summary>
    public event EventHandler<bool>? Done;

    public Button KeepButton { get; }

    /// <summary>The lines shown, as they read, for a test to read.</summary>
    public IReadOnlyList<string> Lines => [.. ((StackPanel)Child!).Children.OfType<StackPanel>().SelectMany(b => b.Children.OfType<TextBlock>()).Select(t => t.Text ?? string.Empty)];

    private static TextBlock Line(string sign, string text, bool struck)
    {
        TextBlock line = new TextBlock { Text = sign + text.Trim(), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }.InCodeFont();
        if (struck)
        {
            line.TextDecorations = TextDecorations.Strikethrough;
            line.Classes.Add(StyleClasses.Muted);
        }
        return line;
    }
}

/// <summary>The values a game changed from the properties pane and did not keep, listed as it ends so none is lost unasked:
/// each with what it was and is, and Keep; Keep All and Discard for the lot.</summary>
public sealed class GodotUnkeptPanel : DockPanel
{
    private readonly ListBox list;
    private readonly TextBlock said;

    public GodotUnkeptPanel(string game, GodotTuning of, Action<GodotTuned, Control> keep, Action<Control> keepAll, Action discard)
    {
        said = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(10, 8, 10, 4) };
        SetDock(said, Dock.Top);
        list = new ListBox { Classes = { StyleClasses.Tree } };
        ReusedRows.Use(list, () => Row(keep));
        Button all = new() { Content = "Keep All…", Classes = { StyleClasses.Primary } };
        all.Click += (_, _) => keepAll(all);
        Button drop = new() { Content = "Discard" };
        ToolTip.SetTip(drop, "Let go of these values, writing none of them");
        drop.Click += (_, _) => discard();
        StackPanel answers = DialogFooter.Listed(drop, all);
        answers.Margin = new Thickness(10, 6);
        answers.HorizontalAlignment = HorizontalAlignment.Right;
        SetDock(answers, Dock.Bottom);
        Children.Add(said);
        Children.Add(answers);
        Children.Add(list);
        Game = game;
        Of = of;
        KeepAllButton = all;
    }

    public string Game { get; }

    /// <summary>The values of the game it lists.</summary>
    public GodotTuning Of { get; }

    public Button KeepAllButton { get; }

    /// <summary>The values listed.</summary>
    public IReadOnlyList<GodotTuned> Values => list.ItemsSource as IReadOnlyList<GodotTuned> ?? [];

    public string Said => said.Text ?? string.Empty;

    /// <summary>Lists its game's values, saying which other games' values come once these are kept or discarded.</summary>
    public void Show(IReadOnlyList<GodotTuned> values, IReadOnlyList<string> waiting)
    {
        string next = waiting.Count == 0 ? string.Empty : $" {Plural.Listed([.. waiting.Select(w => $"{w}'s")])} come next.";
        said.Text = $"{Game} ended with {Plural.Of(values.Count, "value")} changed and not kept. Keep one where it comes from, or all of them:{next}";
        list.ItemsSource = values;
    }

    private static Control Row(Action<GodotTuned, Control> keep)
    {
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock values = new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.InCodeFont();
        Button link = new() { Classes = { StyleClasses.Link }, Content = "Keep…", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(link, Dock.Right);
        DockPanel.SetDock(name, Dock.Left);
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(10, 0), Children = { link, name, values } };
        link.Click += (_, _) =>
        {
            if (line.DataContext is GodotTuned tuned)
            {
                keep(tuned, link);
            }
        };
        return line.Showing<GodotTuned>(tuned =>
        {
            name.Text = $"{tuned.NodeName}.{tuned.Name}";
            values.Text = $"{GodotValueCell.Written(tuned.Original)} → {GodotValueCell.Written(tuned.Value)}";
            ToolTip.SetTip(name, tuned.Place.Path);
        });
    }
}
