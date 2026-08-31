using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FcgNotifications.Functions.Email;

public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(
        string toEmail,
        string toName,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("[EMAIL desabilitado] Para={Email} Assunto={Subject}", toEmail, subject);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(new MailboxAddress(toName, toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        var secureOptions = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

        await client.ConnectAsync(_options.Host, _options.Port, secureOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(_options.UserName))
            await client.AuthenticateAsync(_options.UserName, _options.Password, cancellationToken);

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogInformation("E-mail enviado para {Email} — {Subject}", toEmail, subject);
    }
}
