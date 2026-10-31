using Avalonia.Controls;

namespace Passenger.App.Run;

/// <summary>The question a run asks when what it starts serves HTTPS (a container run's services, an Aspire AppHost) and
/// this machine's ASP.NET Core development certificate is missing or not trusted: Create and Trust (the default), which has
/// <c>dotnet dev-certs https --trust</c> make and trust one (the system may ask too), or Run over HTTP, this run serving
/// HTTP where it would have served HTTPS. Escape and closing answer Run over HTTP.</summary>
public sealed class CertificateQuestionDialog : IdeWindow
{
    public const string CreateAndTrust = "Create and Trust";
    public const string RunOverHttp = "Run over HTTP";

    public CertificateQuestionDialog(string configuration, string services, bool missing)
    {
        CreateButton = new Button { Content = CreateAndTrust, IsDefault = true, Classes = { StyleClasses.Primary } };
        HttpButton = new Button { Content = RunOverHttp, IsCancel = true };
        CreateButton.Click += (_, _) => Close(true);
        HttpButton.Click += (_, _) => Close(false);
        DialogFooter.Question(this, missing ? "No HTTPS development certificate" : "The HTTPS development certificate is not trusted",
        [
            DialogFooter.Paragraph($"{services} of {configuration} {(services.Contains(',', StringComparison.Ordinal) ? "serve" : "serves")} HTTPS, and this computer's "
                + $"ASP.NET Core development certificate {(missing ? "is not there" : "is not trusted")}, so a browser would refuse it."),
            DialogFooter.Paragraph("Create and Trust has dotnet dev-certs make the certificate and trust it, which the system may ask you to allow. "
                + "Run over HTTP serves HTTP on the same ports for this run, where HTTPS would have been served."),
        ], DialogFooter.Answers(CreateButton, HttpButton));
    }

    public Button CreateButton { get; }

    public Button HttpButton { get; }
}
