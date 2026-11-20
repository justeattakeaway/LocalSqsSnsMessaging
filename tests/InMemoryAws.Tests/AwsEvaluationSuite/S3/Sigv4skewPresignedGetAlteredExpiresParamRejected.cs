// Translated from behavioral/s3/check_sigv4skew_presigned_get_altered_expires_param_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sigv4skew-presigned-get-altered-expires-param-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4skewPresignedGetAlteredExpiresParamRejected
{
    /// <summary>
    /// Widening only X-Amz-Expires on a presigned GET yields 403 SignatureDoesNotMatch and no bytes; S3's
    /// echoed canonical request carries the tampered value, and the original URL still works.
    /// </summary>
    [Test]
    [DisplayName("s3.sigv4skew-presigned-get-altered-expires-param-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = "sigv4skew-altered-param-payload"u8.ToArray();
        const string key = "sigv4skew/signed-param.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(payload) });
        const int signedFor = 300;
        var url = S3Fidelity.Presign(ctx, "GET", bucket, key, signedFor);

        // Positive control first: the untampered URL serves the object.
        var (status, _, got) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"the untampered presigned GET must serve the object, got {status}");
        Ensure(got.SequenceEqual(payload), $"untampered presigned GET served {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(payload)}");

        // Break exactly one thing: double the expiry window, leave every other pair untouched.
        var pairs = S3Fidelity.SignedQuery(url);
        Ensure(
            pairs.Contains(new("X-Amz-Expires", signedFor.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            $"expected X-Amz-Expires={signedFor} among the signed parameters, got {Py.Repr(pairs.Select(p => $"{p.Key}={p.Value}").ToList())}");
        var widened = (signedFor * 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tamperedPairs = pairs.Select(p => p.Key == "X-Amz-Expires" ? new KeyValuePair<string, string>(p.Key, widened) : p).ToList();
        Ensure(pairs.Zip(tamperedPairs).Count(p => !p.First.Equals(p.Second)) == 1, "exactly one parameter may differ");
        var (tamperedStatus, _, body) = await S3Fidelity.Send(ctx, "GET", S3Fidelity.Rebuild(url, tamperedPairs));
        Ensure(tamperedStatus == 403, $"widening a signed X-Amz-Expires must be refused 403, got {tamperedStatus}: {S3Fidelity.Head(body, 300)}");
        var err = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(err["Code"] == "SignatureDoesNotMatch", $"expected SignatureDoesNotMatch, got {Py.Repr(err["Code"])} ({Py.Repr(err.GetValueOrDefault("Message"))})");
        Ensure(err["Message"] == S3Fidelity.SignatureMismatchMessage, $"unexpected SignatureDoesNotMatch message {Py.Repr(err["Message"])}");

        // Effect absent: no object bytes were served with the refusal.
        Ensure(body.AsSpan().IndexOf(payload) < 0, $"the refusal body must not contain the object's bytes; got {S3Fidelity.Head(body, 300)}");

        // The mechanism, not just the code: the tampered value entered the canonical request S3 signed,
        // and the signature it compared against is the one the URL still carries unchanged.
        Ensure(
            err.GetValueOrDefault("CanonicalRequest", string.Empty).Contains($"X-Amz-Expires={widened}", StringComparison.Ordinal),
            $"S3's canonical request must contain the tampered X-Amz-Expires={widened}, proving the " +
            $"parameter is signed; it read {Py.Repr(err.GetValueOrDefault("CanonicalRequest"))}");
        var original = pairs.First(p => p.Key == "X-Amz-Signature").Value;
        Ensure(
            err.GetValueOrDefault("SignatureProvided") == original,
            "the signature S3 compared against must be the untouched one from the URL " +
            $"({Py.Repr(original)}), got {Py.Repr(err.GetValueOrDefault("SignatureProvided"))}");

        // And the original URL is unharmed -- the refusal was the tamper, not a revoked signature.
        (status, _, got) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"the untampered URL must still serve the object, got {status}");
        Ensure(got.SequenceEqual(payload), $"untampered presigned GET served {S3Fidelity.Head(got)} after the tamper");
    }
}
