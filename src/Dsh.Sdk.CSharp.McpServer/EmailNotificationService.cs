using DeepSeek.Harness.Sdk;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Security.Authentication;

namespace Dsh.Sdk.CSharp.McpServer;

public sealed class EmailNotificationService(
    IConfiguration configuration,
    McpBridgeSettings settings) : IEmailNotificationService
{
    public bool IsEnabled => settings.EmailNotificationsEnabled;

    private readonly string? _host = GetOptionalSetting(configuration, "Email:SmtpHost", "SMTP_HOST");
    private readonly string? _username = GetOptionalSetting(configuration, "Email:SmtpUsername", "SMTP_USERNAME");
    private readonly string? _password = GetOptionalSetting(configuration, "Email:SmtpPassword", "SMTP_PASSWORD");
    private readonly string? _from = GetOptionalSetting(configuration, "Email:FromEmail", "SMTP_FROM");
    private readonly string? _fromName = GetOptionalSetting(configuration, "Email:FromName", "SMTP_FROM_NAME");
    private readonly int _port = int.TryParse(
        configuration["SMTP_PORT"] ?? configuration["Email:SmtpPort"],
        out var port)
        ? port
        : 587;
    private readonly IReadOnlySet<string> _allowedRecipients =
        (configuration["DSH_EMAIL_ALLOWED_RECIPIENTS"] ?? configuration["Email:AllowedRecipients"] ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly SecureSocketOptions _socketOptions =
        ParseSocketOptions(configuration["SMTP_SECURITY"] ?? configuration["Email:SmtpSecurity"]);

    public void ValidateRecipient(string recipient)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (!_allowedRecipients.Contains(recipient))
        {
            throw new InvalidOperationException(
                "The requested email recipient is not in the configured email recipient allowlist.");
        }

        if (string.IsNullOrWhiteSpace(_host) || string.IsNullOrWhiteSpace(_from))
        {
            throw new InvalidOperationException(
                "Email delivery is not configured. Set Email:SmtpHost, Email:FromEmail, and Email:AllowedRecipients.");
        }
    }

    public async Task<EmailNotificationResult> SendResultAsync(
        string recipient,
        HarnessRunResult result,
        CancellationToken cancellationToken)
    {
        var subject = $"DSH task completed ({result.FinishReason ?? "unknown"})";
        var body = $"""
            Session: {result.SessionId}
            Finish reason: {result.FinishReason ?? "(not reported)"}

            {result.FinalResponse}
            """;
        return await SendMessageAsync(recipient, subject, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmailNotificationResult> SendMessageAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return new EmailNotificationResult("disabled", recipient, null);
        }

        ValidateRecipient(recipient);

        if ((_username is null) != (_password is null))
        {
            throw new InvalidOperationException(
                "Email:SmtpUsername and Email:SmtpPassword must either both be set or both be omitted.");
        }

        var host = _host
            ?? throw new InvalidOperationException("Email:SmtpHost is not configured.");
        var from = _from
            ?? throw new InvalidOperationException("Email:FromEmail is not configured.");

        try
        {
            var message = new MimeMessage();
            var fromAddress = MailboxAddress.Parse(from);
            message.From.Add(string.IsNullOrWhiteSpace(_fromName)
                ? fromAddress
                : new MailboxAddress(_fromName, fromAddress.Address));
            message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(host, _port, _socketOptions, cancellationToken).ConfigureAwait(false);
            if (_username is not null && _password is not null)
            {
                await smtp.AuthenticateAsync(_username, _password, cancellationToken).ConfigureAwait(false);
            }

            await smtp.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return new EmailNotificationResult("sent", recipient, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is MailKit.ProtocolException
                or MailKit.ServiceNotConnectedException
                or MailKit.ServiceNotAuthenticatedException
                or MailKit.Security.AuthenticationException
                or System.Security.Authentication.AuthenticationException
                or IOException
                or FormatException)
        {
            return new EmailNotificationResult(
                "failed",
                recipient,
                $"Email delivery failed ({exception.GetType().Name}).");
        }
    }

    private static SecureSocketOptions ParseSocketOptions(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? SecureSocketOptions.StartTls
            : Enum.TryParse<SecureSocketOptions>(value, ignoreCase: true, out var parsed)
                && parsed is SecureSocketOptions.StartTls or SecureSocketOptions.SslOnConnect
                    ? parsed
                    : throw new InvalidOperationException(
                        "Email:SmtpSecurity must be StartTls or SslOnConnect.");

    private static string? GetOptionalSetting(
        IConfiguration configuration,
        string sectionKey,
        string legacyEnvironmentKey)
    {
        var value = configuration[legacyEnvironmentKey] ?? configuration[sectionKey];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
