using System.Globalization;

namespace FormsTreeApp;

/// <summary>What a person does in the app comes as lines on its standard input, which the Debug window's console sends:
/// "click X,Y" in the main form's client area, as Windows sends a pointer's messages to the window under it, so Windows
/// Forms takes them as it takes a real mouse's while the person's own pointer stays where it is.</summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        MainForm form = new();
        Thread input = new(() => Input(form)) { IsBackground = true, Name = "Input" };
        input.Start();
        Application.Run(form);
    }

    private static void Input(MainForm form)
    {
        while (Console.ReadLine() is { } line)
        {
            if (line.Trim().Split(' ', 2) is ["click", string at] && at.Split(',') is [string x, string y])
            {
                Point point = new((int)Math.Round(double.Parse(x, CultureInfo.InvariantCulture)), (int)Math.Round(double.Parse(y, CultureInfo.InvariantCulture)));
                form.BeginInvoke(() => form.PostClick(point));
            }
        }
    }
}
