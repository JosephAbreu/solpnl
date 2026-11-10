using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SolPnl.Plugin.Enphase.Tests;

/// <summary>Certificate pinning over a real TLS connection to a local HTTPS server with a self-signed certificate.</summary>
public class TlsTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Round-trip through PKCS#12 so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Serves /info and answers everything else with 401, over TLS with the given certificate.</summary>
    private sealed class HttpsGateway : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public int Port { get; }
        public X509Certificate2 Certificate { get; set; }

        public HttpsGateway(X509Certificate2 certificate)
        {
            Certificate = certificate;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var tls = new SslStream(client.GetStream());
                            await tls.AuthenticateAsServerAsync(Certificate);
                            var buffer = new byte[4096];
                            var read = await tls.ReadAsync(buffer);
                            var path = Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1];
                            var (status, body) = path == "/info"
                                ? ("200 OK", "<envoy_info><device><sn>1</sn><software>D8.2.0</software><imeter>false</imeter></device></envoy_info>")
                                : ("401 Unauthorized", "");
                            var response = $"HTTP/1.1 {status}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                            await tls.WriteAsync(Encoding.UTF8.GetBytes(response));
                        }
                        catch { /* the client rejected our certificate */ }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
        }
    }

    [Fact]
    public async Task FirstCertificateIsPinned_AChangedOneIsRejected()
    {
        using var genuine = SelfSigned("envoy");
        using var impostor = SelfSigned("impostor");
        await using var gateway = new HttpsGateway(genuine);
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = $"127.0.0.1:{gateway.Port}" });

        // TLS succeeds (pinned); the 401 afterwards proves the request got through.
        var first = await Assert.ThrowsAsync<HttpRequestException>(() => plugin.ReadAsync());
        Assert.Contains("needs an access token", first.Message);
        Assert.Equal(EnphasePlugin.Fingerprint(genuine), plugin.TrustedFingerprint);

        // Something else now answers at the address: refused, with both fingerprints in the message.
        gateway.Certificate = impostor;
        var second = await Assert.ThrowsAsync<HttpRequestException>(() => plugin.ReadAsync());
        Assert.Contains("isn't the one SolPnl trusts", second.Message);
    }

    [Fact]
    public async Task ConfiguredFingerprint_RejectsAnotherCertificateEvenFirst()
    {
        using var genuine = SelfSigned("envoy");
        using var impostor = SelfSigned("impostor");
        await using var gateway = new HttpsGateway(impostor);
        await using var plugin = new EnphasePlugin(new EnphaseOptions
        {
            Host = $"127.0.0.1:{gateway.Port}", CertificateFingerprint = EnphasePlugin.Fingerprint(genuine),
        });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => plugin.ReadAsync());
        Assert.Contains("isn't the one SolPnl trusts", ex.Message);
    }
}
