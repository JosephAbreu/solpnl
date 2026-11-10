using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SolPnl.Contracts.Models;

namespace SolPnl.Plugin.Enphase.Tests;

/// <summary>
/// Tests against responses shaped like pyenphase's captured ones (tests/fixtures/7.6.185_with_cts_and_battery_3t),
/// reusing its values where it has them.
/// </summary>
public class EnphaseTests
{
    private const string Info = """
        <?xml version='1.0' encoding='UTF-8'?>
        <envoy_info><device><sn>122123456789</sn><pn>800-00654-r08</pn><software>D7.6.185</software><imeter>true</imeter></device></envoy_info>
        """;

    private const string Production = """
        {"production":[{"type":"inverters","activeCount":5,"readingTime":0,"wNow":0,"whLifetime":45349},
          {"type":"eim","activeCount":1,"measurementType":"production","readingTime":1695769629,"wNow":-0.957,
           "whLifetime":2432969.956,"rmsCurrent":0.387,"rmsVoltage":238.522,"whToday":0.956}],
         "consumption":[{"type":"eim","activeCount":1,"measurementType":"total-consumption","wNow":523.579,"whLifetime":3711429.155},
          {"type":"eim","activeCount":1,"measurementType":"net-consumption","wNow":524.536,"whLifetime":1279037.969}],
         "storage":[{"type":"acb","activeCount":0,"readingTime":0,"wNow":0,"whNow":0,"state":"idle"}]}
        """;

    private const string DayProduction = """
        {"production":[{"type":"inverters","activeCount":12,"wNow":4100,"whLifetime":9000000},
          {"type":"eim","activeCount":1,"measurementType":"production","wNow":4210.5,"whLifetime":9100000,"whToday":18250}],
         "consumption":[{"type":"eim","activeCount":1,"measurementType":"total-consumption","wNow":1500.25},
          {"type":"eim","activeCount":1,"measurementType":"net-consumption","wNow":-2710.25}]}
        """;

    private const string Meters = """
        [{"eid":704643328,"state":"enabled","measurementType":"production","phaseMode":"three","phaseCount":1,"meteringStatus":"normal","statusFlags":[]},
         {"eid":704643584,"state":"enabled","measurementType":"net-consumption","phaseMode":"three","phaseCount":1,"meteringStatus":"normal","statusFlags":[]}]
        """;

    private const string MeterReadings = """
        [{"eid":704643328,"timestamp":1695769629,"actEnergyDlvd":2432969.956,"actEnergyRcvd":2001.5,"activePower":-0.661,"voltage":238.524,"current":0.386,"freq":50.000,
          "channels":[{"eid":1778385169,"activePower":-0.661,"voltage":238.524},{"eid":1778385170,"activePower":0,"voltage":0},{"eid":1778385171,"activePower":0,"voltage":0}]},
         {"eid":704643584,"timestamp":1695769629,"actEnergyDlvd":1279037.969,"actEnergyRcvd":512000.25,"activePower":522.043,"voltage":238.282,"current":3.138,"freq":50.000,
          "channels":[{"eid":1778385425,"activePower":522.043,"voltage":238.282},{"eid":1778385426,"activePower":0,"voltage":0},{"eid":1778385427,"activePower":0,"voltage":0}]}]
        """;

    private const string SplitMeters = """
        [{"eid":1,"state":"enabled","measurementType":"net-consumption","phaseMode":"split","phaseCount":2}]
        """;

    private const string SplitReadings = """
        [{"eid":1,"activePower":900,"voltage":121.2,"current":7.4,"freq":60.01,"actEnergyDlvd":1000,"actEnergyRcvd":2000,
          "channels":[{"eid":11,"activePower":500,"voltage":121.2},{"eid":12,"activePower":400,"voltage":120.8}]}]
        """;

    private const string Secctrl = """{"ENC_agg_soc":15,"agg_soc":15,"ENC_agg_avail_energy":480,"ACB_agg_soc":0}""";
    private const string EnsemblePower = """{"devices:":[{"serial_num":"122249097612","real_power_mw":-1250000,"apparent_power_mva":0,"soc":15}]}""";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

    private static EnphaseDecoder.Readings NewReadings() => new("dev", Now);

    private static Dictionary<string, Reading> ToDictionary(EnphaseDecoder.Readings r) => r.List.ToDictionary(x => x.Metric);

    // ---- Decoding ----

    [Fact]
    public void Production_NightFixture()
    {
        var r = NewReadings();
        using var doc = JsonDocument.Parse(Production);
        EnphaseDecoder.DecodeProduction(doc.RootElement, r);
        var d = ToDictionary(r);
        Assert.Equal(0, d["pv.power"].Value);                 // CT reads -0.957 W at night
        Assert.Equal(524.536, d["grid.power"].Value, 3);      // + import
        Assert.Equal(523.579, d["load.power"].Value, 3);
        Assert.Equal(2432.97, d["vendor.enphase.energy.pv_total"].Value, 3);
        Assert.Equal(5, d["vendor.enphase.microinverters_producing"].Value);
    }

    [Fact]
    public void Production_DayExporting()
    {
        var r = NewReadings();
        using var doc = JsonDocument.Parse(DayProduction);
        EnphaseDecoder.DecodeProduction(doc.RootElement, r);
        var d = ToDictionary(r);
        Assert.Equal(4210.5, d["pv.power"].Value, 3);         // the CT, not the microinverters' 4100
        Assert.Equal(-2710.25, d["grid.power"].Value, 3);
        Assert.Equal(1500.25, d["load.power"].Value, 3);
        Assert.Equal(18.25, d["vendor.enphase.energy.pv_today"].Value, 3);
    }

    [Fact]
    public void Production_TotalEqualToNet_IsRepaired()
    {
        var json = DayProduction.Replace("\"wNow\":1500.25", "\"wNow\":-2710.25");
        var r = NewReadings();
        using var doc = JsonDocument.Parse(json);
        EnphaseDecoder.DecodeProduction(doc.RootElement, r);
        Assert.Equal(1500.25, ToDictionary(r)["load.power"].Value, 3); // net + production
    }

    [Fact]
    public void Production_WithoutCts_UsesMicroinverters()
    {
        const string json = """{"production":[{"type":"inverters","activeCount":8,"wNow":2600,"whLifetime":1200000}]}""";
        var r = NewReadings();
        using var doc = JsonDocument.Parse(json);
        EnphaseDecoder.DecodeProduction(doc.RootElement, r);
        var d = ToDictionary(r);
        Assert.Equal(2600, d["pv.power"].Value);
        Assert.DoesNotContain("grid.power", d.Keys);
        Assert.DoesNotContain("load.power", d.Keys);
    }

    [Fact]
    public void Meters_PreferTheGridMeter()
    {
        var r = NewReadings();
        using var meters = JsonDocument.Parse(Meters);
        using var readings = JsonDocument.Parse(MeterReadings);
        EnphaseDecoder.DecodeMeters(meters.RootElement, readings.RootElement, r);
        var d = ToDictionary(r);
        Assert.Equal(238.282, d["grid.voltage.l1"].Value, 3);
        Assert.Equal(3.138, d["grid.current.l1"].Value, 3);
        Assert.Equal(50.0, d["grid.frequency"].Value, 3);
        Assert.Equal(1279.038, d["vendor.enphase.energy.grid_import_total"].Value, 3);
        Assert.Equal(512.0, d["vendor.enphase.energy.grid_export_total"].Value, 3);
        Assert.DoesNotContain("grid.voltage.l2", d.Keys);    // phaseCount 1: unused channels ignored
    }

    [Fact]
    public void Meters_SplitPhase()
    {
        var r = NewReadings();
        using var meters = JsonDocument.Parse(SplitMeters);
        using var readings = JsonDocument.Parse(SplitReadings);
        EnphaseDecoder.DecodeMeters(meters.RootElement, readings.RootElement, r);
        var d = ToDictionary(r);
        Assert.Equal(121.2, d["grid.voltage.l1"].Value, 3);
        Assert.Equal(120.8, d["grid.voltage.l2"].Value, 3);
        Assert.Equal(400, d["grid.power.l2"].Value);
        Assert.Equal(60.01, d["grid.frequency"].Value, 3);
    }

    [Fact]
    public void Meters_DisabledAreIgnored()
    {
        var r = NewReadings();
        using var meters = JsonDocument.Parse(Meters.Replace("\"enabled\"", "\"disabled\""));
        using var readings = JsonDocument.Parse(MeterReadings);
        EnphaseDecoder.DecodeMeters(meters.RootElement, readings.RootElement, r);
        Assert.Empty(r.List);
    }

    [Fact]
    public void Batteries_SocAndPower()
    {
        var r = NewReadings();
        using var secctrl = JsonDocument.Parse(Secctrl);
        using var power = JsonDocument.Parse(EnsemblePower);
        EnphaseDecoder.DecodeBatteries(secctrl.RootElement, power.RootElement, r);
        var d = ToDictionary(r);
        Assert.Equal(15, d["battery.soc"].Value);
        Assert.Equal(1250, d["battery.power"].Value);         // -1250000 mW (Enphase: - charging) → + charging
    }

    [Fact]
    public void Info_ParsesXml()
    {
        Assert.Equal(new EnphaseInfo("122123456789", "800-00654-r08", "D7.6.185", true), EnphaseDecoder.ParseInfo(Info));
    }

    private static string Jwt(long exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"ES256"}""")}.{B64($$"""{"enphaseUser":"owner","exp":{{exp}}}""")}.sig";
    }

    [Fact]
    public void Token_ExpiryAndDaysLeft()
    {
        var token = Jwt(Now.AddDays(200.5).ToUnixTimeSeconds());
        var expiry = EnphaseDecoder.TokenExpiry(token);
        Assert.Equal(Now.AddDays(200.5), expiry);
        var r = NewReadings();
        EnphaseDecoder.AddTokenDaysLeft(expiry, r);
        Assert.Equal(200, ToDictionary(r)["vendor.enphase.token_days_left"].Value);
        Assert.Null(EnphaseDecoder.TokenExpiry("not.a.jwt"));
    }

    // ---- Settings ----

    [Theory]
    [InlineData(null, null, null, "Gateway address is required")]
    [InlineData("bad host", null, null, "isn't a valid")]
    [InlineData("envoy.local", "abc", null, "access token")]
    [InlineData("envoy.local", null, "12:34", "Certificate fingerprint")]
    public void ParseOptions_RejectsInvalid(string? host, string? token, string? fingerprint, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => EnphasePluginType.ParseOptions(new Dictionary<string, string?>
        {
            ["host"] = host, ["token"] = token, ["certFingerprint"] = fingerprint,
        }));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ParseOptions_NormalizesFingerprint()
    {
        var hex = string.Concat(Enumerable.Repeat("ab", 32));
        var withColons = string.Join(':', Enumerable.Repeat("ab", 32));
        var o = EnphasePluginType.ParseOptions(new Dictionary<string, string?> { ["host"] = "envoy.local", ["certFingerprint"] = withColons });
        Assert.Equal(hex.ToUpperInvariant(), o.CertificateFingerprint);
    }

    // ---- Certificate pinning ----

    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public async Task Pinning_TrustsTheFirstCertificateOnly()
    {
        using var first = SelfSigned("envoy");
        using var other = SelfSigned("impostor");
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = "envoy.local" });

        Assert.True(plugin.Validate(first, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(EnphasePlugin.Fingerprint(first), plugin.TrustedFingerprint);
        Assert.True(plugin.Validate(first, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(plugin.Validate(other, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(EnphasePlugin.Fingerprint(first), plugin.TrustedFingerprint); // not replaced
    }

    [Fact]
    public async Task Pinning_ConfiguredFingerprintRejectsEverythingElse()
    {
        using var gateway = SelfSigned("envoy");
        using var other = SelfSigned("impostor");
        await using var plugin = new EnphasePlugin(new EnphaseOptions
        {
            Host = "envoy.local", CertificateFingerprint = EnphasePlugin.Fingerprint(gateway),
        });
        Assert.False(plugin.Validate(other, SslPolicyErrors.RemoteCertificateChainErrors)); // even as the first one
        Assert.True(plugin.Validate(gateway, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task Pinning_SystemTrustedCertificatesAreAccepted()
    {
        using var cert = SelfSigned("trusted");
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = "envoy.local" });
        Assert.True(plugin.Validate(cert, SslPolicyErrors.None));
        Assert.Null(plugin.TrustedFingerprint); // nothing pinned
    }

    // ---- End to end against a fake gateway ----

    private sealed class FakeGateway(Dictionary<string, string> routes, bool refuseHttps = false, bool tlsFailure = false) : HttpMessageHandler
    {
        public List<(string Url, string? Auth)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme == "https" && refuseHttps)
                throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
            if (uri.Scheme == "https" && tlsFailure)
                throw new HttpRequestException("ssl", new System.Security.Authentication.AuthenticationException("bad cert"));

            Requests.Add((uri.ToString(), request.Headers.Authorization?.ToString()));
            var key = uri.PathAndQuery.TrimStart('/');
            return Task.FromResult(routes.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static Dictionary<string, string> Routes() => new()
    {
        ["info"] = Info,
        ["production.json?details=1"] = DayProduction,
        ["ivp/meters"] = SplitMeters,
        ["ivp/meters/readings"] = SplitReadings,
        ["ivp/ensemble/secctrl"] = Secctrl,
        ["ivp/ensemble/power"] = EnsemblePower,
    };

    [Fact]
    public async Task Plugin_ReadsEverythingWithTheTokenOverHttps()
    {
        var token = Jwt(DateTimeOffset.UtcNow.AddDays(100).ToUnixTimeSeconds());
        var fake = new FakeGateway(Routes());
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = "envoy.local", Token = token }, fake);

        var d = (await plugin.ReadAsync()).ToDictionary(r => r.Metric);
        Assert.Equal(4210.5, d["pv.power"].Value, 3);
        Assert.Equal(120.8, d["grid.voltage.l2"].Value, 3);
        Assert.Equal(15, d["battery.soc"].Value);
        Assert.InRange(d["vendor.enphase.token_days_left"].Value, 99, 100);
        Assert.Equal("enphase-envoy-local", d["pv.power"].DeviceId);

        Assert.All(fake.Requests, r => Assert.StartsWith("https://", r.Url));
        Assert.All(fake.Requests.Where(r => !r.Url.EndsWith("/info")), r => Assert.Equal($"Bearer {token}", r.Auth));

        var info = await plugin.GetDeviceInfoAsync();
        Assert.Equal(("122123456789", "D7.6.185"), (info.SerialNumber, info.FirmwareVersion));
    }

    [Fact]
    public async Task Plugin_OldFirmwareOverHttp_NeverSendsTheToken()
    {
        var fake = new FakeGateway(Routes(), refuseHttps: true);
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = "envoy.local", Token = Jwt(4102444800) }, fake);
        await plugin.ReadAsync();
        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r => Assert.StartsWith("http://", r.Url));
        Assert.All(fake.Requests, r => Assert.Null(r.Auth));
    }

    [Fact]
    public async Task Plugin_CertificateFailure_DoesNotFallBackToHttp()
    {
        var fake = new FakeGateway(Routes(), tlsFailure: true);
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = "envoy.local", Token = Jwt(4102444800) }, fake);
        await Assert.ThrowsAsync<HttpRequestException>(() => plugin.ReadAsync());
        Assert.Empty(fake.Requests); // nothing sent over HTTP
    }

    [Fact]
    public async Task Plugin_ExpiredToken_SaysSo()
    {
        var routes = Routes();
        var fake = new UnauthorizedGateway(routes);
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = "envoy.local", Token = Jwt(1_700_000_000) }, fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => plugin.ReadAsync());
        Assert.Contains("expired on 2023-11-14", ex.Message);
    }

    [Fact]
    public async Task Plugin_NoBatteriesOrMeters_AreDropped()
    {
        var routes = Routes();
        routes.Remove("ivp/ensemble/secctrl");
        routes.Remove("ivp/meters");
        var fake = new FakeGateway(routes);
        await using var plugin = new EnphasePlugin(new EnphaseOptions { Host = "envoy.local" }, fake);
        await plugin.ReadAsync();
        fake.Requests.Clear();
        await plugin.ReadAsync();
        Assert.DoesNotContain(fake.Requests, r => r.Url.Contains("ivp/"));
    }

    /// <summary>Answers /info, then 401 for everything else (as for an expired token).</summary>
    private sealed class UnauthorizedGateway(Dictionary<string, string> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/info"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(routes["info"]) }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
