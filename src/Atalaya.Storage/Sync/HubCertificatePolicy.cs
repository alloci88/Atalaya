using System.Security.Cryptography.X509Certificates;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.Storage.Sync;

/// <summary>What the policy decided about a TLS certificate libgit2 would otherwise reject.</summary>
public enum CertificateVerdict
{
    /// <summary>libgit2 validated it itself; nothing to decide.</summary>
    Accepted,

    /// <summary>
    /// The chain is trusted, in date and matches the host, but the revocation status could not be
    /// checked (the CRL/OCSP responder is unreachable). Accepted — see the class remarks.
    /// </summary>
    AcceptedWithoutRevocationCheck,

    /// <summary>Anything else: untrusted root, expired, wrong hostname, unreadable.</summary>
    Rejected,
}

/// <summary>
/// Decides whether to accept a TLS certificate that libgit2 flagged.
/// <para>
/// libgit2 validates the chain with the Windows APIs and hard-fails when the revocation status
/// cannot be checked ("certificate revocation status could not be verified"), which is routine on
/// corporate networks where the CRL/OCSP responders are blocked or a proxy intercepts TLS. Browsers
/// soft-fail that case instead, because an attacker able to intercept traffic can equally block the
/// responder — the hard failure costs availability without buying much security.
/// </para>
/// <para>
/// So this policy soft-fails <b>only</b> that one condition: the certificate must still chain to a
/// trusted root, be in date, and match the host. An untrusted root — which is what a real
/// man-in-the-middle without a corporate CA in the store looks like — is still rejected.
/// Set <c>requireRevocationCheck</c> to restore libgit2's hard-fail behaviour.
/// </para>
/// </summary>
public sealed class HubCertificatePolicy
{
    private readonly bool _requireRevocationCheck;
    private readonly ILogger _log;

    public HubCertificatePolicy(bool requireRevocationCheck = false, ILogger? log = null)
    {
        _requireRevocationCheck = requireRevocationCheck;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// The host whose revocation status we could not verify, or null. Surfaced in the UI so the
    /// relaxation is visible instead of silent.
    /// </summary>
    public string? RevocationUncheckedHost { get; private set; }

    /// <summary>The handler to hand LibGit2Sharp's fetch/clone/push options.</summary>
    public bool Check(Certificate certificate, bool valid, string host)
    {
        X509Certificate2? x509 = Extract(certificate);
        CertificateVerdict verdict = Evaluate(valid, x509, host, _requireRevocationCheck);

        switch (verdict)
        {
            case CertificateVerdict.AcceptedWithoutRevocationCheck:
                RevocationUncheckedHost = host;
                _log.LogWarning(
                    "TLS: could not check revocation for {Host}; the chain is trusted and in date, so the "
                    + "connection proceeds. Ask IT to allow the CRL/OCSP endpoints to restore the check.",
                    host);
                return true;

            case CertificateVerdict.Rejected:
                _log.LogWarning("TLS: rejected the certificate presented by {Host}", host);
                return false;

            default:
                return true;
        }
    }

    /// <summary>The decision itself, isolated from LibGit2Sharp so it can be tested directly.</summary>
    internal static CertificateVerdict Evaluate(
        bool valid, X509Certificate2? certificate, string host, bool requireRevocationCheck)
    {
        if (valid)
        {
            return CertificateVerdict.Accepted;
        }

        if (requireRevocationCheck || certificate is null)
        {
            return CertificateVerdict.Rejected;
        }

        try
        {
            using var chain = new X509Chain();
            // Everything is still verified EXCEPT reaching the revocation responder.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            if (!chain.Build(certificate))
            {
                return CertificateVerdict.Rejected;
            }

            return certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: true)
                ? CertificateVerdict.AcceptedWithoutRevocationCheck
                : CertificateVerdict.Rejected;
        }
        catch (Exception)
        {
            // A malformed host or certificate is not something to be lenient about.
            return CertificateVerdict.Rejected;
        }
    }

    private static X509Certificate2? Extract(Certificate certificate)
    {
        if (certificate is not CertificateX509 { Certificate: { } raw })
        {
            return null;
        }

        try
        {
            return raw as X509Certificate2 ?? new X509Certificate2(raw.GetRawCertData());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
