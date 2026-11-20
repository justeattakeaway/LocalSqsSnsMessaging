// Translated from behavioral/s3/check_s3checksum_malformed_header_rejected_as_invalid_request_not_baddigest.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-malformed-header-rejected-as-invalid-request-not-baddigest</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumMalformedHeaderRejectedAsInvalidRequestNotBaddigest
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("two-distinct-refusals-guard-one-header-", 160)));

    /// <summary>
    /// A wrong-sized ChecksumSHA256 header is InvalidRequest; a wrong-valued one is BadDigest,
    /// since S3 validates the header's shape before comparing. Neither stores anything, and the
    /// true digest is accepted.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-malformed-header-rejected-as-invalid-request-not-baddigest")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var correct = S3Fidelity.Sha256B64(Payload);
        var wrongValue = S3Fidelity.Sha256B64([.. Payload, (byte)'x']);
        // Valid base64, but three bytes where a SHA-256 is thirty-two.
        var @short = Convert.ToBase64String("abc"u8);
        Ensure(Convert.FromBase64String(@short).Length == 3, "the malformed digest must decode to 3 bytes");
        Ensure(Convert.FromBase64String(correct).Length == 32, "a SHA-256 digest is 32 bytes");

        Task<PutObjectResponse> Put(string key, string checksum) =>
            s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Payload), ChecksumSHA256 = checksum });

        // --- Refusal 1: the header is the wrong SHAPE. This must not be a digest comparison.
        var excShort = await FidelityContext.CaptureError(() => Put("malformed.bin", @short));
        Ensure(
            excShort is not null,
            $"PutObject accepted ChecksumSHA256={Py.Repr(@short)}, which decodes to 3 bytes; a SHA-256 header of the wrong length must be refused before any comparison happens");
        Ensure(excShort!.Code == "InvalidRequest", $"expected InvalidRequest for a 3-byte SHA-256 header, got {Py.Repr(excShort.Code)}");
        Ensure(excShort.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for InvalidRequest, got {(int)excShort.StatusCode}");
        Ensure(excShort.Message == "Value for x-amz-checksum-sha256 header is invalid.", $"the malformed-header refusal's message is {Py.Repr(excShort.Message)}");

        // --- Refusal 2: the header is well-formed but does not describe the bytes.
        var excValue = await FidelityContext.CaptureError(() => Put("mismatched.bin", wrongValue));
        Ensure(excValue is not null, $"PutObject accepted ChecksumSHA256={Py.Repr(wrongValue)} for a body whose true digest is {Py.Repr(correct)}");
        Ensure(excValue!.Code == "BadDigest", $"expected BadDigest for a well-formed but mismatched SHA256, got {Py.Repr(excValue.Code)}");
        Ensure(excValue.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for BadDigest, got {(int)excValue.StatusCode}");
        Ensure(excValue.Message == "The SHA256 you specified did not match the calculated checksum.", $"the mismatched-digest refusal's message is {Py.Repr(excValue.Message)}");

        // --- The claim: the two classes are distinguished, not collapsed onto one code.
        Ensure(
            excShort.Code != excValue.Code,
            $"a malformed header and a mismatched value both returned {Py.Repr(excShort.Code)}; S3 separates shape validation (InvalidRequest) from digest comparison (BadDigest), and collapsing them hides which half of the contract an implementation actually has");

        // --- Effect absent: neither refused PUT stored anything.
        foreach (var key in new[] { "malformed.bin", "mismatched.bin" })
        {
            var missing = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
            Ensure(missing is not null, $"the refused PutObject stored {Py.Repr(key)} anyway");
            Ensure(missing!.StatusCode == HttpStatusCode.NotFound, $"HeadObject for {Py.Repr(key)} returned {(int)missing.StatusCode}, expected 404");
        }

        var listed = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).ToList();
        Ensure(listed.Count == 0, $"the bucket holds {Py.Repr(listed)} after two refused PUTs; it must be empty");

        // --- POSITIVE CONTROL: the same bytes with the true digest.
        var stored = await Put("accepted.bin", correct);
        Ensure(
            stored.ChecksumSHA256 == correct,
            $"the accepted PutObject echoed {Py.Repr(stored.ChecksumSHA256)}, the client computed {Py.Repr(correct)} with hashlib over the same {Payload.Length} bytes");
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "accepted.bin", ChecksumMode = ChecksumMode.ENABLED });
        var body = await S3Fidelity.ReadBody(got);
        Ensure(body.AsSpan().SequenceEqual(Payload), $"the stored object is {body.Length} bytes, want {Payload.Length}");
        Ensure(
            S3Fidelity.Sha256B64(body) == got.ChecksumSHA256 && got.ChecksumSHA256 == correct,
            $"the bytes S3 returned hash to {Py.Repr(S3Fidelity.Sha256B64(body))}, S3 reported {Py.Repr(got.ChecksumSHA256)}, and the client computed {Py.Repr(correct)}");
    }
}
