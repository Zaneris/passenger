using Passenger.App.Debugging;

namespace Passenger.UnitTests.Run;

/// <summary>A program's C# apply and its XAML's said as one: the worse result stands, while what its C# came to stays what the
/// debugger follows, so XAML failing after C# applied takes no bookkeeping away from code the program now runs; each part's
/// words are said, C#'s first.</summary>
public sealed class HotReloadOutcomeTests
{
    private static HotReloadOutcome Code(HotReloadResult result) => new(result, "C# said") { Files = ["/app/MainWindow.axaml.cs"], Modules = ["App"], Methods = 1 };

    private static HotReloadOutcome Xaml(HotReloadResult result) => new(result, "XAML said") { Files = ["/app/MainWindow.axaml"] };

    [Fact]
    public void A_Hot_Reload_of_CSharp_and_XAML_comes_to_what_the_program_took() => Cases.All(
        ("xaml that failed after csharp applied leaves the csharp applied under the worse result", () => Cases.Each<HotReloadResult>(
        [
            HotReloadResult.Errors,
            HotReloadResult.RestartNeeded,
        ], Xaml_that_failed_after_csharp_applied_leaves_the_csharp_applied_under_the_worse_result)),
        ("csharp that needed nothing stays so beside xaml that failed", Csharp_that_needed_nothing_stays_so_beside_xaml_that_failed),
        ("csharp refused beside xaml that applied is no code the program took", Csharp_refused_beside_xaml_that_applied_is_no_code_the_program_took),
        ("an apply of one part alone is its own code outcome", An_apply_of_one_part_alone_is_its_own_code_outcome));

    private void Xaml_that_failed_after_csharp_applied_leaves_the_csharp_applied_under_the_worse_result(HotReloadResult failed)
    {
        HotReloadOutcome code = Code(HotReloadResult.Applied);
        HotReloadOutcome both = HotReloadOutcome.Both(code, Xaml(failed))!;
        Assert.Equal(failed, both.Result);
        Assert.Equal("C# said; XAML said", both.Said.English);
        Assert.Same(code, both.Code);
        Assert.Equal(["/app/MainWindow.axaml.cs"], both.Code.Files);
        Assert.Equal(["App"], both.Code.Modules);
    }

    private void Csharp_that_needed_nothing_stays_so_beside_xaml_that_failed()
    {
        HotReloadOutcome both = HotReloadOutcome.Both(new HotReloadOutcome(HotReloadResult.NothingToApply, "Nothing"), Xaml(HotReloadResult.Errors))!;
        Assert.Equal(HotReloadResult.Errors, both.Result);
        Assert.Equal(HotReloadResult.NothingToApply, both.Code.Result);
    }

    private void Csharp_refused_beside_xaml_that_applied_is_no_code_the_program_took()
    {
        HotReloadOutcome both = HotReloadOutcome.Both(Code(HotReloadResult.Errors), Xaml(HotReloadResult.Applied))!;
        Assert.Equal(HotReloadResult.Errors, both.Code.Result);
    }

    private void An_apply_of_one_part_alone_is_its_own_code_outcome()
    {
        HotReloadOutcome xaml = Xaml(HotReloadResult.Applied);
        Assert.Same(xaml, HotReloadOutcome.Both(null, xaml)!.Code);
        HotReloadOutcome code = Code(HotReloadResult.Applied);
        Assert.Same(code, HotReloadOutcome.Both(code, null)!.Code);
        Assert.Null(HotReloadOutcome.Both(null, null));
    }
}
