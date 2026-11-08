using System.Globalization;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SolPnl.Contracts.Models;
using SolPnl.Contracts.Plugins;

namespace SolPnl.Plugin.Notify.Smtp;

/// <summary>
/// Sends SolPnl notifications by email through any SMTP server (your mail provider's, or your own).
/// Supports STARTTLS (usually port 587), SSL/TLS from the start (usually 465) and, for local relays only, no encryption.
/// </summary>
public sealed class SmtpEmailPlugin : INotificationPlugin, IConfigurablePlugin
{
    public const string PluginId = "email-smtp";
    public const string StartTls = "STARTTLS";
    public const string SslTls = "SSL/TLS";
    public const string NoEncryption = "None";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Id => PluginId;
    public string DisplayName => "Email (SMTP)";
    public string Description => "Sends notifications by email through your mail provider's SMTP server (e.g. Gmail, Outlook, iCloud) or your own.";
    public string? TestLabel => "Send test email";

    public IReadOnlyList<SettingField> PluginSettings { get; } =
    [
        new("host", "SMTP server", Placeholder: "smtp.gmail.com", Help: "Your provider's outgoing mail server."),
        new("port", "Port", SettingKind.Number, Required: false, Default: "587", Help: "587 for STARTTLS, 465 for SSL/TLS."),
        new("security", "Encryption", SettingKind.Choice, Required: false, Default: StartTls, Options: [StartTls, SslTls, NoEncryption],
            Help: "Use None only for a mail relay on your own network."),
        new("username", "Username", Required: false, Placeholder: "you@example.com", Help: "Usually your full email address. Leave empty if the server needs no login."),
        new("password", "Password", SettingKind.Secret, Required: false,
            Help: "For Gmail, Outlook.com and iCloud, create an app password in your account's security settings; your normal password won't work."),
        new("from", "From", Placeholder: "SolPnl <you@example.com>", Help: "Sender address. Most providers require it to be your own address."),
        new("to", "Send to", Placeholder: "you@example.com, partner@example.com", Help: "One or more addresses, separated by commas."),
        new("subjectPrefix", "Subject prefix", Required: false, Default: "[SolPnl]"),
    ];

    public Task SendAsync(Alert alert, IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken = default)
    {
        var options = Parse(settings);
        return SendMessageAsync(options, BuildMessage(options, alert), cancellationToken);
    }

    public async Task<PluginActionResult> TestAsync(IReadOnlyDictionary<string, string?> settings, CancellationToken cancellationToken = default)
    {
        SmtpOptions options;
        try
        {
            options = Parse(settings);
        }
        catch (ArgumentException ex)
        {
            return new PluginActionResult(false, ex.Message);
        }

        var alert = new Alert("test", AlertSeverity.Info, "Test email",
            "This is a test email from SolPnl. Notifications by email are working.", DateTimeOffset.UtcNow);
        try
        {
            await SendMessageAsync(options, BuildMessage(options, alert), cancellationToken);
            return new PluginActionResult(true, $"Test email sent to {string.Join(", ", options.To.Mailboxes.Select(m => m.Address))}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PluginActionResult(false, Explain(ex));
        }
    }

    public sealed record SmtpOptions(
        string Host, int Port, SecureSocketOptions Security, string? Username, string? Password,
        MailboxAddress From, InternetAddressList To, string SubjectPrefix);

    /// <summary>Validates settings. Throws ArgumentException with a user-facing message.</summary>
    public static SmtpOptions Parse(IReadOnlyDictionary<string, string?> settings)
    {
        string? Get(string key) => settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var host = Get("host") ?? throw new ArgumentException("SMTP server is required.");
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ArgumentException($"'{host}' isn't a valid server name.");

        var security = (Get("security") ?? StartTls) switch
        {
            StartTls => SecureSocketOptions.StartTls,
            SslTls => SecureSocketOptions.SslOnConnect,
            NoEncryption => SecureSocketOptions.None,
            var other => throw new ArgumentException($"Unknown encryption '{other}'."),
        };

        var port = security == SecureSocketOptions.SslOnConnect ? 465 : 587;
        if (Get("port") is { } p && (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new ArgumentException("Port must be a whole number from 1 to 65535.");

        if (!MailboxAddress.TryParse(Get("from") ?? "", out var from) || string.IsNullOrEmpty(from.Address) || !from.Address.Contains('@'))
            throw new ArgumentException("From must be an email address, e.g. SolPnl <you@example.com>.");

        if (!InternetAddressList.TryParse(Get("to") ?? "", out var to) || to.Mailboxes.Count() == 0
            || to.Mailboxes.Any(m => !m.Address.Contains('@')))
            throw new ArgumentException("Send to must be one or more email addresses, separated by commas.");

        var username = Get("username");
        var password = settings.TryGetValue("password", out var pw) && !string.IsNullOrEmpty(pw) ? pw : null;
        if (username is not null && password is null) throw new ArgumentException("Enter the password for the username.");

        return new SmtpOptions(host, port, security, username, password, from, to, Get("subjectPrefix") ?? "[SolPnl]");
    }

    public static MimeMessage BuildMessage(SmtpOptions options, Alert alert)
    {
        var message = new MimeMessage();
        message.From.Add(options.From);
        message.To.AddRange(options.To);
        message.Subject = $"{options.SubjectPrefix} {alert.Title}".Trim();
        message.Date = alert.RaisedAt;
        if (alert.Severity == AlertSeverity.Critical) message.Importance = MessageImportance.High;

        message.Body = new TextPart("plain")
        {
            Text = $"{alert.Message}\n\n— SolPnl, {alert.RaisedAt.ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"))}\n",
        };
        return message;
    }

    private static async Task SendMessageAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        await client.ConnectAsync(options.Host, options.Port, options.Security, ct);
        if (options.Username is not null) await client.AuthenticateAsync(options.Username, options.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    /// <summary>Turns common SMTP failures into advice.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        AuthenticationException => "The server rejected the username or password. For Gmail, Outlook.com and iCloud use an app password.",
        SslHandshakeException => "Couldn't set up encryption. Check the encryption setting matches the port (STARTTLS with 587, SSL/TLS with 465).",
        System.Net.Sockets.SocketException se => $"Couldn't connect to the server: {se.Message}",
        SmtpCommandException ce => $"The server refused the message: {ce.Message}",
        SmtpProtocolException pe => $"Unexpected answer from the server: {pe.Message}",
        OperationCanceledException or TimeoutException => "The server didn't answer in time. Check the server name and port.",
        _ => ex.GetBaseException().Message,
    };
}
