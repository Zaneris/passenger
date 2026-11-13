namespace Passenger.App.Settings;

/// <summary>What an open Settings dialog changes of the look, shown before Apply over the settings in force (<see
/// cref="SettingsStore.Effective"/>): only the looks it changed since it last applied, so another window's change
/// meanwhile shows beside it, and a font Settings refuses shown as the last one it took. Behaviour waits for Apply.
/// Disposed as the dialog closes, the settings in force show again.</summary>
public sealed class SettingsPreview : IDisposable
{
    private readonly SettingsStore store;
    private AppSettings from;
    private AppSettings to;
    private bool disposed;

    internal SettingsPreview(SettingsStore store)
    {
        this.store = store;
        from = to = store.Value;
    }

    /// <summary>Shows what the dialog's options change of the look from the settings it last applied.</summary>
    public void Update(AppSettings applied, AppSettings working)
    {
        if (disposed)
        {
            return;
        }
        AppSettings shown = working.Clamped();
        if (Declared.EditorFontFamily.Refused(applied, working) is not null)
        {
            shown = shown with { EditorFontFamily = to.EditorFontFamily };
        }
        if (Declared.WindowFontFamily.Refused(applied, working) is not null)
        {
            shown = shown with { WindowFontFamily = to.WindowFontFamily };
        }
        if (Declared.TerminalFontFamily.Refused(applied, working) is not null)
        {
            shown = shown with { TerminalFontFamily = to.TerminalFontFamily };
        }
        from = applied;
        to = shown;
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

    /// <summary>The settings in force with the looks this dialog changed put over them: the Appearance page's, the
    /// Editor page's but its behaviour, and the terminal's font and cursor.</summary>
    internal AppSettings Over(AppSettings saved) => saved with
    {
        Theme = Shown(from.Theme, to.Theme, saved.Theme),
        Look = Shown(from.Look, to.Look, saved.Look),
        WindowFontFamily = Shown(from.WindowFontFamily, to.WindowFontFamily, saved.WindowFontFamily),
        DisplayScale = Shown(from.DisplayScale, to.DisplayScale, saved.DisplayScale),
        MainMenuBar = Shown(from.MainMenuBar, to.MainMenuBar, saved.MainMenuBar),
        MergeMainMenuWithWindowTitle = Shown(from.MergeMainMenuWithWindowTitle, to.MergeMainMenuWithWindowTitle, saved.MergeMainMenuWithWindowTitle),
        ColourScheme = Shown(from.ColourScheme, to.ColourScheme, saved.ColourScheme),
        EditorFontFamily = Shown(from.EditorFontFamily, to.EditorFontFamily, saved.EditorFontFamily),
        EditorFontSize = Shown(from.EditorFontSize, to.EditorFontSize, saved.EditorFontSize),
        EditorLineSpacing = Shown(from.EditorLineSpacing, to.EditorLineSpacing, saved.EditorLineSpacing),
        Ligatures = Shown(from.Ligatures, to.Ligatures, saved.Ligatures),
        ShowLineNumbers = Shown(from.ShowLineNumbers, to.ShowLineNumbers, saved.ShowLineNumbers),
        RainbowBraces = Shown(from.RainbowBraces, to.RainbowBraces, saved.RainbowBraces),
        ParameterHints = Shown(from.ParameterHints, to.ParameterHints, saved.ParameterHints),
        TypeHints = Shown(from.TypeHints, to.TypeHints, saved.TypeHints),
        SoftWrap = Shown(from.SoftWrap, to.SoftWrap, saved.SoftWrap),
        SoftWrapFiles = Shown(from.SoftWrapFiles, to.SoftWrapFiles, saved.SoftWrapFiles),
        SoftWrapIndent = Shown(from.SoftWrapIndent, to.SoftWrapIndent, saved.SoftWrapIndent),
        ShowWhitespaces = Shown(from.ShowWhitespaces, to.ShowWhitespaces, saved.ShowWhitespaces),
        TerminalFontSize = Shown(from.TerminalFontSize, to.TerminalFontSize, saved.TerminalFontSize),
        TerminalFontFamily = Shown(from.TerminalFontFamily, to.TerminalFontFamily, saved.TerminalFontFamily),
        TerminalCursorStyle = Shown(from.TerminalCursorStyle, to.TerminalCursorStyle, saved.TerminalCursorStyle),
        TerminalCursorBlinking = Shown(from.TerminalCursorBlinking, to.TerminalCursorBlinking, saved.TerminalCursorBlinking),
    };

    private static T Shown<T>(T applied, T working, T saved) => EqualityComparer<T>.Default.Equals(applied, working) ? saved : working;
}
