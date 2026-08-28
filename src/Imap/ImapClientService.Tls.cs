using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Imap;

namespace p2poolmail
{
    /// <summary>IMAP client factory and TLS certificate policy of <see cref="ImapClientService"/>.</summary>
    public partial class ImapClientService
    {
        private ImapClient CreateClient()
        {
            var client = new ImapClient();

            // MailKit performs online revocation (CRL/OCSP) checking during the TLS
            // handshake. The revocation data is fetched over plain HTTP and the fetch
            // often fails on this network (IPv6 black hole: DNS returns IPv6 addresses
            // that cannot connect, and .NET dials sequentially with no per-address
            // timeout), failing every handshake with "unable to get certificate CRL"
            // even though the certificate chain is perfectly sound (verified live:
            // OnlineRevocation -> OfflineRevocation/RevocationStatusUnknown; NoCheck ->
            // clean handshake in ~1s). Disabling revocation here keeps trust, hostname
            // and validity checks fully enforced; the callback below still rejects
            // chains that do not validate. A MITM is therefore still detected.
            client.CheckCertificateRevocation = false;

            client.ServerCertificateValidationCallback = (_, certificate, chain, sslPolicyErrors) =>
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                    return true;

                // Revocation data (CRL/OCSP) is fetched over plain HTTP during the TLS
                // handshake. On this network such fetches often fail (IPv6 black hole:
                // addresses resolve but cannot connect, and .NET dials sequentially),
                // which fails the handshake with "unable to get certificate CRL" even
                // though the certificate chain is perfectly sound. Re-validate the
                // chain with revocation checks disabled; accept only if it is then
                // fully valid — trust, expiration and (via sslPolicyErrors) hostname
                // checks are still enforced, so a MITM is still rejected.
                if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors
                    && certificate is X509Certificate2 cert)
                {
                    try
                    {
                        using var noRevocation = new X509Chain();
                        noRevocation.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (noRevocation.Build(cert)
                            && noRevocation.ChainStatus.All(s => s.Status == X509ChainStatusFlags.NoError))
                        {
                            _logger?.Invoke($"IMAP TLS for {_host}:{_port}: certificate chain valid, CRL fetch failed - revocation check skipped");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"IMAP TLS chain re-validation failed for {_host}:{_port}: {ex.Message}");
                    }
                }

                if (_ignoreCertificateErrors)
                {
                    _logger?.Invoke($"IMAP SSL certificate validation bypassed for {_host}:{_port} due to: {sslPolicyErrors}");
                    return true;
                }

                return false;
            };

            return client;
        }
    }
}
