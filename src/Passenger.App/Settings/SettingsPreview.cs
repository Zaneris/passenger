namespace Passenger.App.Settings;

/// <summary>A temporary visual overlay; disposing it reveals the latest committed settings.</summary>
public sealed class SettingsPreview : IDisposable
{
    private readonly SettingsStore store;
    private AppSettings from = new();
    private AppSettings to = new();
    private bool disposed;

    internal SettingsPreview(SettingsStore store)
    {
        this.store = store;
        from = to = store.Value;
    }

    public void Update(AppSettings baseline, AppSettings working)
    {
        if (disposed)
        {
            return;
        }
        AppSettings valid = working.Clamped();
        if (Declared.EditorFontFamily.Refused(baseline, working) is not null)
        {
            valid = valid with { EditorFontFamily = to.EditorFontFamily };
        }
        if (Declared.WindowFontFamily.Refused(baseline, working) is not null)
        {
            valid = valid with { WindowFontFamily = to.WindowFontFamily };
        }
        from = baseline;
        to = valid;
        store.RefreshPreview();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        store.EndPreview(this);
    }

    // Explicit allowlist: behavior settings must wait for Apply or OK.
    internal AppSettings Over(AppSettings saved) => saved with
    {
        Theme = from.Theme != to.Theme ? to.Theme : saved.Theme,
        Look = from.Look != to.Look ? to.Look : saved.Look,
        WindowFontFamily = from.WindowFontFamily != to.WindowFontFamily ? to.WindowFontFamily : saved.WindowFontFamily,
        MainMenuBar = from.MainMenuBar != to.MainMenuBar ? to.MainMenuBar : saved.MainMenuBar,
        MergeMainMenuWithWindowTitle = from.MergeMainMenuWithWindowTitle != to.MergeMainMenuWithWindowTitle ? to.MergeMainMenuWithWindowTitle : saved.MergeMainMenuWithWindowTitle,
        ColourScheme = from.ColourScheme != to.ColourScheme ? to.ColourScheme : saved.ColourScheme,
        EditorFontFamily = from.EditorFontFamily != to.EditorFontFamily ? to.EditorFontFamily : saved.EditorFontFamily,
        EditorFontSize = from.EditorFontSize != to.EditorFontSize ? to.EditorFontSize : saved.EditorFontSize,
        EditorLineSpacing = from.EditorLineSpacing != to.EditorLineSpacing ? to.EditorLineSpacing : saved.EditorLineSpacing,
        Ligatures = from.Ligatures != to.Ligatures ? to.Ligatures : saved.Ligatures,
        ShowLineNumbers = from.ShowLineNumbers != to.ShowLineNumbers ? to.ShowLineNumbers : saved.ShowLineNumbers,
        RainbowBraces = from.RainbowBraces != to.RainbowBraces ? to.RainbowBraces : saved.RainbowBraces,
        ParameterHints = from.ParameterHints != to.ParameterHints ? to.ParameterHints : saved.ParameterHints,
        TypeHints = from.TypeHints != to.TypeHints ? to.TypeHints : saved.TypeHints,
        SoftWrapIndent = from.SoftWrapIndent != to.SoftWrapIndent ? to.SoftWrapIndent : saved.SoftWrapIndent,
        ShowWhitespaces = from.ShowWhitespaces != to.ShowWhitespaces ? to.ShowWhitespaces : saved.ShowWhitespaces,
    };
}
