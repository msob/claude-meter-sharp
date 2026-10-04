using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ClaudeMeter;

namespace ClaudeMeter.Tests;

/// <summary>
/// End-to-end against a local TLS server presenting a self-signed certificate,
/// like a TLS-inspecting firewall would.
/// </summary>
[Collection("Usage.Endpoint")]  // the tests share the static endpoint override
public class TlsTests
{
    static X509Certificate2 FirewallCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Inspecting Firewall", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel can't serve an ephemeral key; round-trip through PFX.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    static async Task<UsageSnapshot> ProbeAgainstFirewall(bool ignoreTlsErrors)
    {
        using var cert = FirewallCert();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(cert);
                var buf = new byte[4096];
                var request = "";
                while (!request.Contains("\r\n\r\n"))
                    request += Encoding.ASCII.GetString(buf, 0, await ssl.ReadAsync(buf));
                var body = """{"five_hour":{"utilization":0.5}}""";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { }  // client refused us
        });

        var old = Usage.Endpoint;
        try
        {
            Usage.Endpoint = $"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/api/oauth/usage";
            return await Usage.ProbeAsync(new Credential("oauth", "token", "test"), ignoreTlsErrors);
        }
        finally
        {
            Usage.Endpoint = old;
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StrictRejectsAndExplainsTheCertificate()
    {
        var snap = await ProbeAgainstFirewall(ignoreTlsErrors: false);
        Assert.False(snap.Ok);
        Assert.StartsWith("TLS/SSL error: ", snap.Error);
        Assert.Contains("CN=Inspecting Firewall", snap.Error);
        Assert.Contains("RemoteCertificateChainErrors", snap.Error);
        Assert.Equal("TLS/SSL error", UsageWidget.ShortError(snap.Error!));
        Assert.Equal(snap.Error, TooltipPanel.Notice(snap));
    }

    [Fact]
    public async Task LenientAcceptsButStillReportsTheProblem()
    {
        var snap = await ProbeAgainstFirewall(ignoreTlsErrors: true);
        Assert.True(snap.Ok, snap.Error);
        Assert.Equal(0.5, snap.ByKey("five_hour")!.Utilization);
        Assert.Contains("CN=Inspecting Firewall", snap.TlsWarning);
        Assert.Equal(snap.TlsWarning, TooltipPanel.Notice(snap));
    }
}
