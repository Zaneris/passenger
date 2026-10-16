using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Settings;

/// <summary>A Settings option keeps aligned columns when there is room, and its label above its editor on a narrow page.</summary>
public sealed class SettingsOptionRow : Grid
{
    private const double LabelWidth = 220;
    private const double ChoiceWidth = 260;

    private readonly TextBlock label;
    private readonly Control editor;
    private readonly TextBlock? note;
    private readonly Thickness labelMargin;
    private readonly Thickness editorMargin;
    private readonly double? preferredWidth;
    private readonly CheckBox? inlineCheck;
    private bool stacked;

    public SettingsOptionRow(TextBlock label, Control editor, TextBlock? note)
    {
        this.label = label;
        this.editor = editor;
        this.note = note;
        labelMargin = label.Margin;
        editorMargin = editor.Margin;
        inlineCheck = editor is CheckBox { Content: null } check ? check : null;
        ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*");
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
        Children.Add(label);
        Children.Add(editor);
        if (note is not null)
        {
            note.HorizontalAlignment = HorizontalAlignment.Left;
            Children.Add(note);
        }
        if (double.IsFinite(editor.Width))
        {
            preferredWidth = editor is ComboBox ? ChoiceWidth : editor.Width;
            editor.Width = preferredWidth.Value;
            editor.HorizontalAlignment = HorizontalAlignment.Left;
        }
        Place();
        SizeChanged += (_, _) =>
        {
            bool narrow = Bounds.Width < SettingsColumns.WidePageWidth;
            if (stacked != narrow)
            {
                stacked = narrow;
                Place();
            }
            FitEditor();
        };
    }

    private void Place()
    {
        bool together = stacked && inlineCheck is not null;
        ColumnDefinitions[0].Width = together ? new GridLength(0) : stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(LabelWidth);
        ColumnDefinitions[1].Width = stacked && !together ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        label.IsVisible = !together;
        if (inlineCheck is not null)
        {
            inlineCheck.Content = together ? label.Text : null;
        }
        label.Padding = stacked ? default : new Thickness(0, 0, 16, 0);
        label.Margin = stacked ? default : labelMargin;
        editor.Margin = new Thickness(editorMargin.Left, editorMargin.Top + (stacked && !together ? 4 : 0), editorMargin.Right, editorMargin.Bottom);
        SetColumn(editor, stacked && !together ? 0 : 1);
        SetRow(editor, stacked && !together ? 1 : 0);
        if (note is not null)
        {
            note.Margin = new Thickness(0, 4, 0, 0);
            SetColumn(note, stacked && !together ? 0 : 1);
            SetRow(note, stacked && !together ? 2 : 1);
        }
        FitEditor();
    }

    private void FitEditor()
    {
        if (preferredWidth is { } width && Bounds.Width > 0)
        {
            editor.MaxWidth = Math.Min(width, Math.Max(0, Bounds.Width - (stacked ? 0 : LabelWidth)));
        }
    }
}
