using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Attach to Process's choice of where the programs it lists run: anywhere, this machine, every container or one
/// container, made in its Sort and Filter beside the search and taken with the search together, and the next Attach to
/// Process in the folder opening on the last choice.</summary>
public sealed class AttachToProcessTests : IDisposable
{
    private readonly string folder = TestFolders.Create("attach-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A program of this machine and two of each of two containers, which never run.</summary>
    private static readonly DotnetProcess[] Programs =
    [
        new(4101, "Shop.Api", "dotnet Shop.Api.dll", "me", true),
        new(7, "Orders", "dotnet Orders.dll", "app", false) { Container = "orders-1" },
        new(9, "Orders.Worker", "dotnet Orders.Worker.dll", "app", false) { Container = "orders-1" },
        new(12, "Cart", "dotnet Cart.dll", "app", false) { Container = "cart-1" },
    ];

    [AvaloniaFact]
    public void The_programs_listed_are_those_running_where_chosen_and_the_search_narrows_them_there()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        AttachToProcessDialog dialog = new(() => Task.FromResult<IReadOnlyList<DotnetProcess>>(Programs), _ => false);
        dialog.Show(window);
        Ui.WaitFor(() => dialog.Listed.IsCompleted, "Attach to Process to list the stand-in programs");
        Assert.Equal(["Shop.Api", "Orders", "Orders.Worker", "Cart"], dialog.Shown.Select(p => p.Name));
        Assert.False(dialog.Places.Button.IsChecked == true, "Attach to Process opened narrowed, with no choice made");

        Choose(dialog, "This Machine");
        Assert.Equal(["Shop.Api"], dialog.Shown.Select(p => p.Name));
        Assert.True(dialog.Places.Button.IsChecked == true, "The choice of where programs run does not show itself made");
        Choose(dialog, "All Containers");
        Assert.Equal(["Orders", "Orders.Worker", "Cart"], dialog.Shown.Select(p => p.Name));
        Assert.Contains(dialog.Rows, r => r.Heading == "Container cart-1");
        Choose(dialog, "Container orders-1");
        Assert.Equal(["Orders", "Orders.Worker"], dialog.Shown.Select(p => p.Name));
        Assert.Equal(new AttachScope(AttachWhere.Container, "orders-1"), dialog.Scope);

        dialog.Search.Text = "worker";
        Ui.Settle();
        Assert.Equal(["Orders.Worker"], dialog.Shown.Select(p => p.Name));
        dialog.Search.Text = "cart";
        Ui.Settle();
        Assert.True(dialog.Shown.Count == 0 && dialog.Placeholder == "No .NET process in container orders-1 matches 'cart'",
            $"A search finding nothing in the container chosen showed [{string.Join(", ", dialog.Shown.Select(p => p.Name))}] and said '{dialog.Placeholder}'");
        Choose(dialog, "All Processes");
        Assert.Equal(["Cart"], dialog.Shown.Select(p => p.Name));
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void The_next_attach_to_process_opens_on_where_the_last_listed_programs_ran()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        AttachToProcessDialog first = window.ShowAttachToProcess();
        Ui.WaitFor(() => first.Listed.IsCompleted, "Attach to Process to list the .NET programs");
        Choose(first, "This Machine");
        first.Close();
        Ui.Settle();
        AttachToProcessDialog next = window.ShowAttachToProcess();
        Ui.WaitFor(() => next.Listed.IsCompleted, "Attach to Process to list the .NET programs again");
        Assert.True(next.Scope.Where == AttachWhere.ThisMachine && next.Places.Button.IsChecked == true,
            $"Attach to Process opened listing programs running {next.Scope.Where}, rather than on this machine as last chosen");
        Assert.DoesNotContain(next.Shown, p => p.Container is not null);
        next.Close();
        window.Close();
    }

    /// <summary>A choice of where programs run, made in the dialog's Sort and Filter as a person makes it.</summary>
    private static void Choose(AttachToProcessDialog dialog, string where)
    {
        Ui.Click(dialog.Places.Button);
        Ui.Choose(dialog.Places.OpenMenu, where);
    }
}
