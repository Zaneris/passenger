using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Passenger.Core;

namespace Passenger.App;

/// <summary>About: the version, the commit it was built from and the runtime it runs on beside Passenger's icon; the
/// open-source software it carries (its notices) and its own licence a click away; Copy and Close for a bug report. A
/// link closes it and hands its file to <c>open</c>.</summary>
public sealed class AboutDialog : Window
{
    public const string NoticesLink = "open-source software";
    public const string LicenseLink = "MIT License";

    public AboutDialog(Action<string> open)
    {
        Title = "About Passenger";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string commit = Product.Commit is { } hash ? $"Commit {hash[..Math.Min(12, hash.Length)]}" : "Built outside a Git repository";
        Text = $"Passenger {Product.Version}\n{(Product.Commit is { } full ? $"Commit {full}" : commit)}\nRuntime version: {Product.Runtime}\n{RuntimeInformation.OSDescription}";

        StackPanel facts = new()
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = $"Passenger {Product.Version}", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                new TextBlock { Text = commit },
                new TextBlock { Text = $"Runtime version: {Product.Runtime}" },
                Sentence("Powered by ", Link(NoticesLink, Product.Notices), new Thickness(0, 14, 0, 0)),
                Sentence("Licensed under the ", Link(LicenseLink, Product.License), default),
            },
        };
        if (Copyright() is { } copyright)
        {
            facts.Children.Add(new TextBlock { Classes = { "muted" }, Text = copyright, Margin = new Thickness(0, 14, 0, 0) });
        }

        Button copy = new() { Content = "Copy and Close" };
        Button close = new() { Content = "Close", IsDefault = true, IsCancel = true, Classes = { "primary" } };
        copy.Click += (_, _) => IdeErrors.Observe(CopyAndCloseAsync());
        close.Click += (_, _) => Close();

        Content = DialogFooter.Page(new StackPanel
        {
            Margin = new Thickness(24, 22, 20, 20),
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Children =
            {
                new Image
                {
                    Source = new Bitmap(AssetLoader.Open(new Uri("avares://Passenger/Assets/passenger.png"))),
                    Width = 64,
                    Height = 64,
                    VerticalAlignment = VerticalAlignment.Top,
                },
                facts,
            },
        }, DialogFooter.Bar(DialogFooter.Of(close, null, copy)));

        Button Link(string words, string path)
        {
            Button link = new() { Classes = { "link" }, Content = words, IsEnabled = File.Exists(path) };
            link.Click += (_, _) =>
            {
                Close();
                open(path);
            };
            return link;
        }
    }

    private async Task CopyAndCloseAsync()
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(Text);
        }
        Close();
    }

    /// <summary>What Copy and Close copies for a bug report: the version, the full commit, the runtime and the
    /// system.</summary>
    public string Text { get; }

    /// <summary>Running text with a link in it.</summary>
    private static StackPanel Sentence(string words, Button link, Thickness margin) => new()
    {
        Orientation = Orientation.Horizontal,
        Margin = margin,
        Children = { new TextBlock { Text = words, VerticalAlignment = VerticalAlignment.Center }, link },
    };

    /// <summary>Passenger's copyright as its licence states it, with the sign for "(c)".</summary>
    private static string? Copyright()
    {
        try
        {
            return File.ReadLines(Product.License).FirstOrDefault(l => l.StartsWith("Copyright", StringComparison.Ordinal))?.Replace("(c)", "©", StringComparison.Ordinal).Trim();
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
