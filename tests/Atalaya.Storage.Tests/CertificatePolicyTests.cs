using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Atalaya.Storage.Sync;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// The TLS policy (F2.7). libgit2 hard-fails when it cannot reach the CRL/OCSP responder
/// ("certificate revocation status could not be verified"), which blocks corporate networks.
/// We soft-fail that ONE condition — and nothing else: an untrusted or mismatched certificate is
/// still rejected, which is what stops this from being "accept anything".
/// </summary>
public sealed class CertificatePolicyTests
{
    [Fact]
    public void A_certificate_libgit2_accepted_is_accepted()
        => HubCertificatePolicy.Evaluate(valid: true, certificate: null, "github.com", requireRevocationCheck: false)
            .Should().Be(CertificateVerdict.Accepted);

    [Fact]
    public void With_the_strict_option_nothing_is_relaxed()
        => HubCertificatePolicy.Evaluate(valid: false, SelfSigned("github.com"), "github.com", requireRevocationCheck: true)
            .Should().Be(CertificateVerdict.Rejected);

    [Fact]
    public void An_untrusted_certificate_is_rejected_even_for_the_right_host()
    {
        // A self-signed certificate chains to nothing in the Windows store: exactly what a
        // man-in-the-middle without a corporate CA looks like. Must NOT be let through.
        using X509Certificate2 impostor = SelfSigned("github.com");

        HubCertificatePolicy.Evaluate(valid: false, impostor, "github.com", requireRevocationCheck: false)
            .Should().Be(CertificateVerdict.Rejected);
    }

    [Fact]
    public void A_certificate_for_another_host_is_rejected()
    {
        using X509Certificate2 wrongHost = SelfSigned("evil.example");

        HubCertificatePolicy.Evaluate(valid: false, wrongHost, "github.com", requireRevocationCheck: false)
            .Should().Be(CertificateVerdict.Rejected);
    }

    [Fact]
    public void An_unreadable_certificate_is_rejected()
        => HubCertificatePolicy.Evaluate(valid: false, certificate: null, "github.com", requireRevocationCheck: false)
            .Should().Be(CertificateVerdict.Rejected);

    [Fact]
    public void A_malformed_host_is_rejected()
    {
        using X509Certificate2 cert = SelfSigned("github.com");

        HubCertificatePolicy.Evaluate(valid: false, cert, string.Empty, requireRevocationCheck: false)
            .Should().Be(CertificateVerdict.Rejected);
    }

    [Fact]
    public void Nothing_is_flagged_until_a_relaxation_actually_happens()
        => new HubCertificatePolicy().RevocationUncheckedHost.Should().BeNull();

    private static X509Certificate2 SelfSigned(string host)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
