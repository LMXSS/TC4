namespace FcgNotifications.Functions.Email;

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>Quando false o e-mail nao e enviado, apenas registrado no log.</summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public bool UseStartTls { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = "noreply@fcg.local";
    public string FromName { get; set; } = "FIAP Cloud Games";
}
