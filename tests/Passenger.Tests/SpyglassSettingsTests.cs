using Avalonia.Headless.XUnit;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>Code Spyglass's settings: off until turned on, its kinds on, and each kind's row says it takes effect only with
/// Spyglass on.</summary>
public sealed class SpyglassSettingsTests
{
    [AvaloniaFact]
    public void Spyglass_is_off_by_default_with_every_kind_on_for_when_it_is_turned_on()
    {
        AppSettings fresh = new();
        Assert.False(fresh.CodeSpyglass, "Code Spyglass is on in a new state, so every C# file asks the server for the usages of its declarations");
        Assert.True(fresh.CodeSpyglassUsages && fresh.CodeSpyglassInheritors && fresh.CodeSpyglassAuthors,
            "Turning Code Spyglass on shows fewer than every kind of hint");
    }

    [AvaloniaFact]
    public void Each_kinds_row_holds_only_while_spyglass_is_on_and_the_switches_change_their_own_setting_alone()
    {
        AppSettings off = new();
        AppSettings on = off with { CodeSpyglass = true };
        foreach (ToggleSetting kind in (ToggleSetting[])[Declared.CodeSpyglassUsages, Declared.CodeSpyglassInheritors, Declared.CodeSpyglassAuthors])
        {
            SettingWhen? when = kind.Row?.When;
            Assert.True(when is not null && !when.Holds(off) && when.Holds(on), $"The row \"{kind.Row?.Label}\" does not grey while Code Spyglass is off, or while it is on");
        }
        Assert.True(Declared.CodeSpyglass.Row?.When is null, "The row of Code Spyglass's own switch is greyed by another setting");
        AppSettings toggled = Declared.CodeSpyglassUsages.Other(on);
        Assert.True(toggled is { CodeSpyglass: true, CodeSpyglassUsages: false, CodeSpyglassInheritors: true, CodeSpyglassAuthors: true },
            "Switching the usages off changed another Code Spyglass setting too");
    }

    [AvaloniaFact]
    public void Spyglass_settings_are_declared_once_each_with_a_row_on_the_editor_page()
    {
        ToggleSetting[] all = [Declared.CodeSpyglass, Declared.CodeSpyglassUsages, Declared.CodeSpyglassInheritors, Declared.CodeSpyglassAuthors];
        Assert.True(all.All(s => Declared.Every.Contains(s) && s.Row is { Page: var page } && page == SettingsDialog.EditorPage),
            "A Code Spyglass setting is not declared, or has no row on the Editor page, so Settings cannot show it");
        Assert.True(all.Select(s => s.Row!.Label).Distinct().Count() == all.Length && all.All(s => s.Row!.Label.Contains("Spyglass", StringComparison.Ordinal)),
            "Code Spyglass's rows share a label or one does not name it, so they are told apart in Settings only by their place");
    }
}
