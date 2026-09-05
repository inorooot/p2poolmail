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

            // MailKit checks certificate revocation (CRL/OCSP) during the TLS
            // handshake. The CRL fetch often fails on restricted networks and then
            // breaks every handshake, even with a sound chain. Trust, hostname and
            // validity checks stay enforced.
            client.CheckCertificateRevocation = false;

            client.ServerCertificateValidationCallback = (_, certificate, chain, sslPolicyErrors) =>
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                    return true;

                // Re-validate the chain without revocation checks. Accept it only if
                // it is then fully valid. Trust, expiration and hostname checks are
                // still enforced, so a MITM is still rejected.
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
                            _logger?.Invoke($"IMAP TLS for {_host}:{_port}: chain valid, CRL fetch failed - revocation check skipped");
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
