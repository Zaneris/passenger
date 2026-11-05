using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>Commit Signing…: what commits are signed with, set up in one step. SSH comes first, offered with the key the
/// person pushes with, then GPG and X.509, each with the person's own keys to choose from (<see cref="SigningKeys"/>) or
/// a key typed; set in this repository's git config or the global one; every commit signed or only those Sign is
/// checked for. An SSH key is listed in an allowed signers file, so git shows the person's own signatures as good. OK
/// closes it with <see cref="Choice"/>.</summary>
public sealed class CommitSigningDialog : IdeWindow
{
    private readonly Git? repository;
    private readonly RadioButton ssh;
    private readonly RadioButton gpg;
    private readonly RadioButton x509;
    private readonly TextBox key;
    private readonly WrapPanel found;
    private readonly TextBlock foundNone;
    private readonly RadioButton here;
    private readonly RadioButton everywhere;
    private readonly CheckBox every;
    private readonly TextBlock listed;
    private readonly string globalSigners;
    private int finding;

    public CommitSigningDialog(SigningSetup setup, Git? repository, string globalSigners)
    {
        this.repository = repository;
        this.globalSigners = globalSigners;
        bool configured = setup.IsSetUp;
        ssh = Radio("format", "SSH key", !configured || setup.Format is SigningFormat.Ssh);
        gpg = Radio("format", "GPG key", configured && setup.Format is SigningFormat.OpenPgp);
        x509 = Radio("format", "X.509 certificate", configured && setup.Format is SigningFormat.X509);
        key = new TextBox { Name = "SigningKey", Classes = { StyleClasses.Search }, Text = setup.Key ?? string.Empty };
        found = new WrapPanel { ItemSpacing = 12 };
        foundNone = MutedText.Of(string.Empty);
        foundNone.TextWrapping = TextWrapping.Wrap;
        here = Radio("scope", "This repository", setup.KeyScope is not "global");
        everywhere = Radio("scope", "All your repositories (your global git config)", setup.KeyScope is "global");
        every = new CheckBox { Content = "Sign every commit (commit.gpgsign)", IsChecked = setup.Signs || !configured };
        listed = MutedText.Of(string.Empty);
        listed.TextWrapping = TextWrapping.Wrap;
        foreach (RadioButton format in new[] { ssh, gpg, x509 })
        {
            format.IsCheckedChanged += (_, _) =>
            {
                if (format.IsChecked == true)
                {
                    key.Text = string.Empty;
                    Find();
                }
            };
        }
        here.IsCheckedChanged += (_, _) => Say();
        key.TextChanged += (_, _) => Say();

        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 10 };
        Row(fields, "Sign with:", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { ssh, gpg, x509 } });
        Row(fields, "Key:", new StackPanel { Spacing = 6, Children = { key, found, foundNone } });
        Row(fields, "Set in:", new StackPanel { Spacing = 6, Children = { here, everywhere } });
        Row(fields, string.Empty, every);
        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Accept();
        DialogFooter.Question(this, "Commit Signing", [fields, listed], DialogFooter.Of(ok, DialogFooter.Cancel(() => Close(null))));
        Find();
    }

    /// <summary>The format chosen.</summary>
    public SigningFormat Format => ssh.IsChecked == true ? SigningFormat.Ssh : x509.IsChecked == true ? SigningFormat.X509 : SigningFormat.OpenPgp;

    /// <summary>The key typed or chosen.</summary>
    public string Key
    {
        get => key.Text?.Trim() ?? string.Empty;
        set => key.Text = value;
    }

    /// <summary>Whether it is set in the global git config rather than the repository's.</summary>
    public bool Global
    {
        get => everywhere.IsChecked == true;
        set => (value ? everywhere : here).IsChecked = true;
    }

    public bool SignEvery
    {
        get => every.IsChecked == true;
        set => every.IsChecked = value;
    }

    /// <summary>Chooses a format, as a click on its button does.</summary>
    public void Choose(SigningFormat format) => (format switch
    {
        SigningFormat.Ssh => ssh,
        SigningFormat.X509 => x509,
        _ => gpg,
    }).IsChecked = true;

    /// <summary>The keys offered for the format chosen, once found.</summary>
    public IReadOnlyList<SigningKey> Found { get; private set; } = [];

    /// <summary>What OK set up, once pressed: null until then, and when cancelled.</summary>
    public SigningChoice? Choice { get; private set; }

    /// <summary>OK: the choice made, the allowed signers file the global one where it is set globally (the repository's
    /// own, in its git folder, where it is set there).</summary>
    public void Accept()
    {
        Choice = new SigningChoice(Format, Key.Length > 0 ? Key : null, SignEvery, Global, Global ? globalSigners : null);
        Close(true);
    }

    private void Find() => IdeErrors.Observe(FindAsync());

    /// <summary>The person's keys for the format chosen, each a link that takes it; the first taken where none is typed.</summary>
    private async Task FindAsync()
    {
        int find = ++finding;
        SigningFormat format = Format;
        IReadOnlyList<SigningKey> keys = await SigningKeys.FindAsync(format, repository);
        if (find != finding)
        {
            return;
        }
        Found = keys;
        found.Children.Clear();
        foreach (SigningKey offered in keys)
        {
            Button take = new() { Classes = { StyleClasses.Link }, Content = offered.Shown };
            take.Click += (_, _) => key.Text = offered.Value;
            found.Children.Add(take);
        }
        foundNone.Text = keys.Count > 0 ? string.Empty : format switch
        {
            SigningFormat.Ssh => "No SSH key was found in ~/.ssh or an agent: type a key's file, or make one with ssh-keygen",
            SigningFormat.X509 => "gpgsm has no certificate to sign with, or is not installed: type a certificate's id",
            _ => "gpg has no secret key, or is not installed: type a key's id, or leave it empty for the committer's own",
        };
        foundNone.IsVisible = keys.Count == 0;
        if (Key.Length == 0 && keys.Count > 0)
        {
            key.Text = keys[0].Value;
        }
        Say();
    }

    /// <summary>Says where an SSH key is listed as the person's, so their signatures show as good.</summary>
    private void Say()
    {
        listed.IsVisible = Format is SigningFormat.Ssh;
        listed.Text = $"Your public key is listed for your email in {(Global ? globalSigners : "the repository's own allowed_signers file (.git/allowed_signers)")}, "
            + "unless git's config names one already, so git shows your own signatures as good.";
    }

    private static RadioButton Radio(string group, string words, bool chosen) => new() { GroupName = group, Content = words, IsChecked = chosen };

    private static void Row(Grid grid, string label, Control field)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        TextBlock text = new() { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0) };
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
    }
}
