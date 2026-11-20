// Translated from behavioral/s3/check_sigv4skew_presigned_get_date_shifted_one_second_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sigv4skew-presigned-get-date-shifted-one-second-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4skewPresignedGetDateShiftedOneSecondRejected
{
    /// <summary>
    /// A presigned GET's X-Amz-Date shifted by ONE SECOND, still inside the signed window, fails
    /// SignatureDoesNotMatch and never "Request has expired". A sibling check shifts the same field to a
    /// long-past instant and gets AccessDenied instead.
    /// </summary>
    [Test]
    [DisplayName("s3.sigv4skew-presigned-get-date-shifted-one-second-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = "sigv4skew-one-second-payload"u8.ToArray();
        const string key = "sigv4skew/one-second.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(payload) });
        const int signedFor = 600;
        var url = S3Fidelity.Presign(ctx, "GET", bucket, key, signedFor);

        var (status, _, got) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"the untampered presigned GET must serve the object, got {status}");
        Ensure(got.SequenceEqual(payload), $"untampered presigned GET served {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(payload)}");

        // Break exactly one thing: X-Amz-Date one second later.
        var pairs = S3Fidelity.SignedQuery(url);
        var original = pairs.First(p => p.Key == "X-Amz-Date").Value;
        var shifted = S3Fidelity.AmzDate(S3Fidelity.AmzInstant(original).AddSeconds(1));
        Ensure(shifted != original, $"the shift must change the date; both read {Py.Repr(original)}");
        var tamperedPairs = pairs.Select(p => p.Key == "X-Amz-Date" ? new KeyValuePair<string, string>(p.Key, shifted) : p).ToList();
        Ensure(pairs.Zip(tamperedPairs).Count(p => !p.First.Equals(p.Second)) == 1, "exactly one parameter may differ");
        var (tamperedStatus, _, body) = await S3Fidelity.Send(ctx, "GET", S3Fidelity.Rebuild(url, tamperedPairs));
        Ensure(tamperedStatus == 403, $"shifting a signed X-Amz-Date by one second must be refused 403, got {tamperedStatus}: {S3Fidelity.Head(body, 300)}");
        var err = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(
            err["Code"] == "SignatureDoesNotMatch",
            $"a one-second shift is still inside the {signedFor}s window, so the refusal must come " +
            $"from the signature; got {Py.Repr(err["Code"])} ({Py.Repr(err.GetValueOrDefault("Message"))})");
        Ensure(err["Message"] == S3Fidelity.SignatureMismatchMessage, $"unexpected SignatureDoesNotMatch message {Py.Repr(err["Message"])}");
        Ensure(body.AsSpan().IndexOf(payload) < 0, $"the refusal body must not contain the object's bytes; got {S3Fidelity.Head(body, 300)}");
        Ensure(
            err.GetValueOrDefault("CanonicalRequest", string.Empty).Contains($"X-Amz-Date={shifted}", StringComparison.Ordinal),
            $"S3's canonical request must carry the shifted date X-Amz-Date={shifted}; it read {Py.Repr(err.GetValueOrDefault("CanonicalRequest"))}");
        var stringToSign = err.GetValueOrDefault("StringToSign", string.Empty).Split('\n');
        Ensure(
            stringToSign.Length > 1 && stringToSign[1] == shifted,
            $"the string to sign's timestamp line must be the shifted date {Py.Repr(shifted)}, got {Py.Repr(stringToSign.ElementAtOrDefault(1))}");

        (status, _, got) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"the untampered URL must still serve the object, got {status}");
        Ensure(got.SequenceEqual(payload), $"untampered presigned GET served {S3Fidelity.Head(got)} after the tamper");
    }
}
