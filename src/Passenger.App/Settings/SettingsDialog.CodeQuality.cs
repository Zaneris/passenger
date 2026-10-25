using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Settings;

/// <summary>The ESLint and Prettier pages: each tool Disabled, Automatic (the project's own package, found from each
/// file's folder up) or Manual (a package named here), the files it works on as a glob, and what it does on save and on
/// Reformat Code.</summary>
public sealed partial class SettingsDialog
{
    /// <summary>The names of <see cref="ToolConfiguration"/>'s choices, in its order, for a tool.</summary>
    public static string[] ConfigurationChoices(string tool) =>
        [$"Disabled {tool} configuration", $"Automatic {tool} configuration", $"Manual {tool} configuration"];

    private void AddCodeQualityTools()
    {
        TextBox eslintPackage = Text(Working.EslintPackage ?? string.Empty, "…/node_modules/eslint", Working.Eslint == ToolConfiguration.Manual,
            text => Change(s => s with { EslintPackage = text.Length == 0 ? null : text }));
        Add(EslintPage, "Configuration", Configuration("ESLint", Working.Eslint, v =>
        {
            Change(s => s with { Eslint = v });
            eslintPackage.IsEnabled = v == ToolConfiguration.Manual;
        }), $"{EslintWords} automatic manual disabled configuration",
            "Automatic: the eslint package installed in the project, found from each file's folder up, with the project's own "
            + "configuration (eslint.config.js, or .eslintrc). Its problems show as you type, and Show Context Actions fixes them. "
            + "ESLint runs on the Node.js the Node.js page names.");
        Add(EslintPage, "ESLint package", eslintPackage, $"{EslintWords} package node_modules path folder manual");
        Add(EslintPage, "Run for files", Text(Working.EslintFiles, AppSettings.DefaultEslintFiles, true,
            text => Change(s => s with { EslintFiles = text.Length == 0 ? AppSettings.DefaultEslintFiles : text })),
            $"{EslintWords} run for files glob pattern extensions", "A glob of the files' paths from the folder open, for example **/*.{js,ts}.");
        Add(Declared.EslintFixOnSave);

        TextBox prettierPackage = Text(Working.PrettierPackage ?? string.Empty, "…/node_modules/prettier", Working.Prettier == ToolConfiguration.Manual,
            text => Change(s => s with { PrettierPackage = text.Length == 0 ? null : text }));
        Add(PrettierPage, "Configuration", Configuration("Prettier", Working.Prettier, v =>
        {
            Change(s => s with { Prettier = v });
            prettierPackage.IsEnabled = v == ToolConfiguration.Manual;
        }), $"{PrettierWords} automatic manual disabled configuration",
            "Automatic: the prettier package installed in the project, found from each file's folder up, with the project's own "
            + ".prettierrc, .prettierignore and .editorconfig. Reformat with Prettier formats any file it reads.");
        Add(PrettierPage, "Prettier package", prettierPackage, $"{PrettierWords} package node_modules path folder manual");
        Add(PrettierPage, "Run for files", Text(Working.PrettierFiles, AppSettings.DefaultPrettierFiles, true,
            text => Change(s => s with { PrettierFiles = text.Length == 0 ? AppSettings.DefaultPrettierFiles : text })),
            $"{PrettierWords} run for files glob pattern extensions", "A glob of the files' paths from the folder open, for example **/*.{js,ts}.");
        Add(Declared.PrettierOnReformat);
        Add(Declared.PrettierOnSave);
    }

    /// <summary>The three radio buttons for how a tool is configured.</summary>
    private static StackPanel Configuration(string tool, ToolConfiguration now, Action<ToolConfiguration> changed)
    {
        StackPanel choices = new() { Spacing = 4 };
        string[] names = ConfigurationChoices(tool);
        foreach (ToolConfiguration choice in Enum.GetValues<ToolConfiguration>())
        {
            RadioButton radio = new() { Content = names[(int)choice], GroupName = $"{tool}Configuration", IsChecked = now == choice };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    changed(choice);
                }
            };
            choices.Children.Add(radio);
        }
        return choices;
    }

    private static TextBox Text(string text, string placeholder, bool enabled, Action<string> changed)
    {
        TextBox box = new() { Classes = { "search" }, Text = text, PlaceholderText = placeholder, Width = 360, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) => changed((box.Text ?? string.Empty).Trim());
        return box;
    }
}
