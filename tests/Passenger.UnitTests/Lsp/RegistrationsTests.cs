using Passenger.Lsp;

namespace Passenger.UnitTests.Lsp;

/// <summary>What a server registered (its file watchers, hundreds as a solution loads), kept by id and listed whole only as
/// the list is read after a registration came or went.</summary>
public class RegistrationsTests
{
    [Fact]
    public void Registrations_cost_what_they_hold_and_list_every_item_still_registered() => Cases.All(
        ("a registration costs what it holds however many came before it", A_registration_costs_what_it_holds_however_many_came_before_it),
        ("every item still registered is listed once registrations came and went", Every_item_still_registered_is_listed_once_registrations_came_and_went));

    /// <summary>A thousand registrations of three items each, read once they are all in as a burst is, allocate per
    /// registration about what a hundred do: one list made per registration would take their square.</summary>
    private static void A_registration_costs_what_it_holds_however_many_came_before_it()
    {
        Registered(10);
        long few = Registered(100);
        long many = Registered(1_000);
        Assert.True(many < few * 20, $"A thousand registrations allocated {many:N0} bytes, against {few:N0} for a hundred: each costs more the more came before "
            + "it, so the hundreds a solution's load registers cost their square");

        static long Registered(int count)
        {
            string[] ids = [.. Enumerable.Range(0, count).Select(i => $"registration {i}")];
            int[][] items = [.. Enumerable.Range(0, count).Select(i => new[] { i, i + 1, i + 2 })];
            Registrations<int> registrations = new();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                registrations.Set(ids[i], items[i]);
            }
            int listed = registrations.All.Count;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(3 * count, listed);
            return allocated;
        }
    }

    private static void Every_item_still_registered_is_listed_once_registrations_came_and_went()
    {
        Registrations<string> registrations = new();
        Assert.Empty(registrations.All);
        registrations.Set("a", ["a1", "a2"]);
        registrations.Set("b", ["b1"]);
        Assert.Equal(["a1", "a2", "b1"], registrations.All.Order(StringComparer.Ordinal));
        registrations.Set("a", ["a3"]);
        Assert.Equal(["a3", "b1"], registrations.All.Order(StringComparer.Ordinal));
        Assert.True(registrations.Remove("b"));
        Assert.False(registrations.Remove("b"));
        Assert.Equal(["a3"], registrations.All);
    }
}
