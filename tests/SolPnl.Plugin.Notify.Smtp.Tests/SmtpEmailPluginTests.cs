using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Security;
using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.Notify.Smtp.Tests;

public class SmtpEmailPluginTests
{
    private static Dictionary<string, string?> Settings(int port, string security = SmtpEmailPlugin.NoEncryption,
        string? username = null, string? password = null) => new()
    {
        ["host"] = "127.0.0.1",
        ["port"] = port.ToString(),
        ["security"] = security,
        ["username"] = username,
        ["password"] = password,
        ["from"] = "SolPnl <solpnl@example.com>",
        ["to"] = "a@example.com, B <b@example.com>",
        ["subjectPrefix"] = "[Test]",
    };

    [Fact]
    public void Parse_DefaultsPortFromEncryption()
    {
        var starttls = SmtpEmailPlugin.Parse(new Dictionary<string, string?>
        {
            ["host"] = "smtp.gmail.com", ["from"] = "me@example.com", ["to"] = "me@example.com",
        });
        Assert.Equal(587, starttls.Port);
        Assert.Equal(SecureSocketOptions.StartTls, starttls.Security);
        Assert.Equal("[SolPnl]", starttls.SubjectPrefix);

        var ssl = SmtpEmailPlugin.Parse(new Dictionary<string, string?>
        {
            ["host"] = "smtp.example.com", ["security"] = SmtpEmailPlugin.SslTls, ["from"] = "me@example.com", ["to"] = "me@example.com",
        });
        Assert.Equal(465, ssl.Port);
        Assert.Equal(SecureSocketOptions.SslOnConnect, ssl.Security);
    }

    [Theory]
    [InlineData("host", null, "SMTP server is required")]
    [InlineData("host", "bad host", "isn't a valid server name")]
    [InlineData("port", "99999", "Port")]
    [InlineData("from", "not an address", "From must be")]
    [InlineData("to", "", "Send to must be")]
    [InlineData("to", "a@example.com, nope", "Send to must be")]
    [InlineData("security", "Telepathy", "Unknown encryption")]
    public void Parse_RejectsInvalid(string key, string? value, string expected)
    {
        var settings = Settings(587);
        settings[key] = value;
        var ex = Assert.Throws<ArgumentException>(() => SmtpEmailPlugin.Parse(settings));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Parse_UsernameNeedsPassword() =>
        Assert.Contains("password", Assert.Throws<ArgumentException>(() => SmtpEmailPlugin.Parse(Settings(587, username: "me"))).Message);

    [Fact]
    public void BuildMessage_SubjectRecipientsAndPriority()
    {
        var options = SmtpEmailPlugin.Parse(Settings(25));
        var message = SmtpEmailPlugin.BuildMessage(options, new Alert("x", AlertSeverity.Critical, "Inverter is offline", "Details here.", DateTimeOffset.UtcNow));
        Assert.Equal("[Test] Inverter is offline", message.Subject);
        Assert.Equal(["a@example.com", "b@example.com"], message.To.Mailboxes.Select(m => m.Address));
        Assert.Equal(MimeKit.MessageImportance.High, message.Importance);
        Assert.Contains("Details here.", message.TextBody);
    }

    [Fact]
    public async Task Send_DeliversThroughSmtp_WithLogin()
    {
        await using var server = await FakeSmtpServer.StartAsync();
        var plugin = new SmtpEmailPlugin();
        await plugin.SendAsync(new Alert("x", AlertSeverity.Warning, "Solar below forecast", "Check the panels.", DateTimeOffset.UtcNow),
            Settings(server.Port, username: "me@example.com", password: "app-password"));

        var session = await server.NextSessionAsync();
        Assert.Contains("AUTH PLAIN", session.Commands.Single(c => c.StartsWith("AUTH")));
        Assert.Contains("MAIL FROM:<solpnl@example.com>", session.Commands);
        Assert.Contains("RCPT TO:<a@example.com>", session.Commands);
        Assert.Contains("RCPT TO:<b@example.com>", session.Commands);
        Assert.Contains("Subject: [Test] Solar below forecast", session.Data);
        Assert.Contains("Check the panels.", session.Data);
    }

    [Fact]
    public async Task Test_ReportsSuccessAndConnectionFailure()
    {
        await using var server = await FakeSmtpServer.StartAsync();
        var plugin = new SmtpEmailPlugin();

        var ok = await plugin.TestAsync(Settings(server.Port));
        Assert.True(ok.Ok, ok.Message);
        Assert.Contains("a@example.com", ok.Message);
        Assert.Contains("Subject: [Test] Test email", (await server.NextSessionAsync()).Data);

        var closedPort = FakeSmtpServer.FreePort();
        var fail = await plugin.TestAsync(Settings(closedPort));
        Assert.False(fail.Ok);
        Assert.Contains("connect", fail.Message, StringComparison.OrdinalIgnoreCase);

        var invalid = await plugin.TestAsync(new Dictionary<string, string?> { ["host"] = "" });
        Assert.False(invalid.Ok);
        Assert.Contains("SMTP server is required", invalid.Message);
    }
}

/// <summary>Just enough of an SMTP server (no TLS) to receive messages from MailKit in tests.</summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Channels.Channel<Session> _sessions = System.Threading.Channels.Channel.CreateUnbounded<Session>();

    public sealed record Session(List<string> Commands, string Data);

    private FakeSmtpServer(TcpListener listener) => _listener = listener;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<FakeSmtpServer> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new FakeSmtpServer(listener);
        _ = server.AcceptLoopAsync();
        return Task.FromResult(server);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task<Session> NextSessionAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _sessions.Reader.ReadAsync(timeout.Token);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleAsync(client);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        var commands = new List<string>();
        var data = new StringBuilder();

        await writer.WriteLineAsync("220 fake ESMTP ready");
        while (await reader.ReadLineAsync() is { } line)
        {
            commands.Add(line);
            var verb = line.Split(' ')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-fake");
                    await writer.WriteLineAsync("250 AUTH PLAIN LOGIN");
                    break;
                case "AUTH":
                    await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    break;
                case "MAIL":
                case "RCPT":
                case "RSET":
                case "NOOP":
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        data.AppendLine(dataLine);
                    await writer.WriteLineAsync("250 OK queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 Bye");
                    await _sessions.Writer.WriteAsync(new Session(commands, data.ToString()));
                    return;
                default:
                    await writer.WriteLineAsync("502 Command not implemented");
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        _cts.Dispose();
    }
}
