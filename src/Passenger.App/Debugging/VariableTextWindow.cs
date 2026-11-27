using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Debugging;

internal sealed class VariableTextWindow : IdeWindow
{
    public VariableTextWindow(string name, string text, Func<string, Task<bool>>? save = null, bool compact = false)
    {
        Title = $"Text · {name}";
        Name = "DebugTextViewer";
        Width = save is null ? DialogWidths.Question : DialogWidths.Form;
        Height = compact || (text.Length <= VariableNode.Longest && !text.Contains('\n')) ? 220 : 320;
        MinWidth = PopupFrame.Narrow;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        CodeEditor editor = new()
        {
            Name = "DebugValueText",
            Document = new TextDocument(text),
            IsReadOnly = true,
            SoftWrap = true,
            ShowLineNumbers = false,
            ShowErrorStripe = false,
            LineCommentPrefix = null,
            FontSize = TypeRamp.Code,
        };
        AutomationProperties.SetName(editor, name);
        CopyButton copy = new("Copy All") { Text = text };
        Button edit = new() { Name = "EditDebugValue", Content = "Edit", IsVisible = save is not null };
        Button apply = new() { Name = "ApplyDebugValue", Content = "Apply", Classes = { StyleClasses.Primary }, IsVisible = false };
        TextBlock status = MutedText.Caption(LengthOf(text));
        Button close = DialogFooter.Cancel(Close, "Close");
        Border footer = DialogFooter.Bar(DialogFooter.Listed(copy, edit, apply, close), status);
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel
        {
            Children =
            {
                footer,
                new Border { Classes = { StyleClasses.Framed }, Margin = new Thickness(Gaps.Normal), Child = editor },
            },
        };
        Opened += (_, _) =>
        {
            editor.Keymap = (Owner is { } owner ? CodeEditor.GetGestures(owner)?.Invoke() : null) ?? DefaultKeymaps.ForPlatform();
            editor.Focus();
        };
        edit.Click += (_, _) =>
        {
            editor.IsReadOnly = false;
            edit.IsVisible = copy.IsVisible = false;
            apply.IsVisible = true;
            editor.Focus();
        };
        apply.Click += (_, _) => IdeErrors.Observe(ApplyAsync());

        async Task ApplyAsync()
        {
            using IDisposable held = BackgroundWork.Scope();
            string changed = editor.Document.Snapshot.ToString();
            apply.IsEnabled = false;
            editor.IsReadOnly = true;
            try
            {
                bool applied = await save!(changed);
                if (!IsVisible)
                {
                    return;
                }
                status.Text = applied ? LengthOf(changed) : "Could not set the value";
                if (applied)
                {
                    copy.Text = changed;
                    copy.IsVisible = edit.IsVisible = true;
                    apply.IsVisible = false;
                }
                else
                {
                    editor.IsReadOnly = false;
                }
                editor.Focus();
            }
            finally
            {
                apply.IsEnabled = true;
            }
        }
    }

    private static string LengthOf(string text) => $"{Numbers.Said(text.Length)} characters";
}
