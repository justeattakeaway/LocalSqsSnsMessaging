// Translated from behavioral/s3/check_sigv4skew_presigned_put_after_expiry_writes_no_object_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sigv4skew-presigned-put-after-expiry-writes-no-object-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4skewPresignedPutAfterExpiryWritesNoObjectRejected
{
    /// <summary>
    /// A presigned PUT used after its expiry is refused and leaves no object behind, while the same
    /// mechanism used inside its window really writes the bytes. Never attempted before the expiry, so an
    /// accepted early write cannot make the later absence unprovable.
    /// </summary>
    [Test]
    [DisplayName("s3.sigv4skew-presigned-put-after-expiry-writes-no-object-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = "sigv4skew-presigned-put-payload"u8.ToArray();
        const string lateKey = "sigv4skew/late.txt";
        const string controlKey = "sigv4skew/in-time.txt";

        // Positive control: a presigned PUT inside its window writes.
        var controlUrl = S3Fidelity.Presign(ctx, "PUT", bucket, controlKey, 600);
        var (status, _, body) = await S3Fidelity.Send(ctx, "PUT", controlUrl, data: payload);
        Ensure(status == 200, $"a presigned PUT inside its window must succeed, got {status}: {S3Fidelity.Head(body, 300)}");
        var stored = await S3Fidelity.GetBytes(s3, bucket, controlKey);
        Ensure(stored.SequenceEqual(payload), $"the presigned PUT stored {S3Fidelity.Head(stored)}, expected {S3Fidelity.Head(payload)}");

        const int expiresIn = 15;
        var lateUrl = S3Fidelity.Presign(ctx, "PUT", bucket, lateKey, expiresIn);
        var query = S3Fidelity.SignedQuery(lateUrl).ToDictionary(p => p.Key, p => p.Value);
        Ensure(query.GetValueOrDefault("X-Amz-Expires") == "15", $"X-Amz-Expires was {Py.Repr(query.GetValueOrDefault("X-Amz-Expires"))}, expected '15'");
        var expiresAt = S3Fidelity.AmzInstant(query["X-Amz-Date"]).AddSeconds(expiresIn);

        // Wait until S3's own clock is past the expiry, never using the URL before then.
        var clock = FidelityTime.StartNew();
        var serverTime = await S3Fidelity.ServerNow(ctx, ctx.Region, bucket);
        while (serverTime <= expiresAt && clock.Elapsed < TimeSpan.FromSeconds(180))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
            serverTime = await S3Fidelity.ServerNow(ctx, ctx.Region, bucket);
        }

        Ensure(serverTime > expiresAt, $"S3's clock never passed the expiry {expiresAt:O} within the poll window; last read {serverTime:O}");
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", lateUrl, data: payload);
        Ensure(status == 403, $"a presigned PUT used after {expiresAt:O} must be refused 403, got {status}: {S3Fidelity.Head(body, 300)}");
        var err = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(err["Code"] == "AccessDenied", $"expected AccessDenied for an expired presigned PUT, got {Py.Repr(err["Code"])}");
        Ensure(err["Message"] == "Request has expired", $"the refusal must say the request expired, got {Py.Repr(err["Message"])}");
        Ensure(
            err.GetValueOrDefault("Expires") == S3Fidelity.IsoSeconds(expiresAt),
            "the refusal must name X-Amz-Date + X-Amz-Expires to the second: expected " +
            $"{Py.Repr(S3Fidelity.IsoSeconds(expiresAt))}, got {Py.Repr(err.GetValueOrDefault("Expires"))}");

        // Effect absent.
        var (code, _) = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, lateKey));
        Ensure(code == "NoSuchKey", $"the expired presigned PUT must not have written {Py.Repr(lateKey)}; get_object answered {Py.Repr(code)}");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "sigv4skew/" })).S3Objects ?? [])
        {
            listed.Add(o.Key);
        }

        Ensure(
            listed.SetEquals([controlKey]),
            "only the in-time control object may exist under this check's own prefix; the bucket holds " +
            Py.Repr(listed.Order(StringComparer.Ordinal).ToList()));
    }
}
