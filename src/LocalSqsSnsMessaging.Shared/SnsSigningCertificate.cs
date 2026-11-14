using System.Security.Cryptography;
#if NET
using System.Security.Cryptography.X509Certificates;
#endif

namespace LocalSqsSnsMessaging;

/// <summary>
/// The key SNS messages are signed with, and the self-signed X.509 certificate a message's
/// <c>SigningCertURL</c> serves, so a subscriber can verify a message's <c>Signature</c> exactly
/// as it would one from AWS (only the certificate's issuer differs).
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-verify-signature-of-message.html
/// </summary>
/// <remarks>
/// One key and certificate serve the whole process, so every bus (and every account the server
/// hosts) signs alike and serves the same certificate. Generated on first use. Only built on .NET:
/// netstandard2.0 has no <c>CertificateRequest</c>, so there <see cref="Current"/> is
/// <see langword="null"/> and messages carry placeholder signatures.
/// </remarks>
internal sealed class SnsSigningCertificate
{
    private const string FileNamePrefix = "SimpleNotificationService-";

#if NET
    private static readonly Lazy<SnsSigningCertificate> Instance = new(Create);
#endif

    /// <summary>The signing key's parameters, from which each signer in <see cref="_signers"/> is made.</summary>
    private readonly RSAParameters _parameters;

    /// <summary>
    /// Idle signers. RSA instances aren't documented as safe for concurrent use, so each signature
    /// takes one of its own: concurrent publishers sign in parallel instead of queueing on one key.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<RSA> _signers = [];

    private SnsSigningCertificate(RSA key, string pem, string fileName)
    {
        _parameters = key.ExportParameters(includePrivateParameters: true);
        _signers.Add(key);
        Pem = pem;
        FileName = fileName;
    }

    /// <summary>The process's signing certificate, or <see langword="null"/> where none can be built (netstandard2.0).</summary>
    public static SnsSigningCertificate? Current =>
#if NET
        Instance.Value;
#else
        null;
#endif

    /// <summary>The certificate in PEM form, as <c>SigningCertURL</c> serves it.</summary>
    public string Pem { get; }

    /// <summary>The certificate's file name, <c>SimpleNotificationService-&lt;hash&gt;.pem</c>, the last segment of <c>SigningCertURL</c>.</summary>
    public string FileName { get; }

    /// <summary>The media type the certificate is served with.</summary>
    public const string ContentType = "application/x-pem-file";

    /// <summary>
    /// Whether <paramref name="path"/> (a request's URL path) names the signing certificate: its last
    /// segment is the certificate's file name, whatever path the service URL is mounted under.
    /// </summary>
    public static bool IsCertificatePath(string? path) =>
        path is not null
        && path.StartsWith('/')
        && Current is { } certificate
        && path.EndsWith("/" + certificate.FileName, StringComparison.Ordinal);

    /// <summary>
    /// The base64 RSASSA-PKCS1-v1_5 signature SNS puts in a message's <c>Signature</c>, over its
    /// <paramref name="canonicalString"/>: SHA1withRSA for <c>SignatureVersion</c> 1, SHA256withRSA for 2.
    /// <see langword="null"/> when the platform refuses the digest (some crypto policies disable SHA-1
    /// signatures), so the message is still delivered, with a placeholder signature.
    /// </summary>
    public string? Sign(string canonicalString, string signatureVersion)
    {
        var hash = string.Equals(signatureVersion, "2", StringComparison.Ordinal)
            ? HashAlgorithmName.SHA256
            : HashAlgorithmName.SHA1;
        var data = Encoding.UTF8.GetBytes(canonicalString);
        if (!_signers.TryTake(out var signer))
        {
            signer = RSA.Create();
            signer.ImportParameters(_parameters);
        }

        try
        {
            return Convert.ToBase64String(signer.SignData(data, hash, RSASignaturePadding.Pkcs1));
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            _signers.Add(signer);
        }
    }

#if NET
    private static SnsSigningCertificate Create()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=LocalSqsSnsMessaging SNS message signing", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

#pragma warning disable CA1308 // AWS names its certificate files with lowercase hex.
        var hash = Convert.ToHexString(SHA256.HashData(certificate.RawData), 0, 16).ToLowerInvariant();
#pragma warning restore CA1308
        return new SnsSigningCertificate(key, certificate.ExportCertificatePem() + "\n", $"{FileNamePrefix}{hash}.pem");
    }
#endif
}
