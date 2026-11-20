// Translated from behavioral/s3/check_s3_express_append_produces_the_exact_concatenation_of_three_offset_writes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-express-append-produces-the-exact-concatenation-of-three-offset-writes</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ExpressAppendProducesTheExactConcatenationOfThreeOffsetWrites
{
    private const string WriteOffsetMessage = "The write offset value that you specified does not match the current object size.";

    private static readonly byte[] C1 = Times("chunk-one-", 12);
    private static readonly byte[] C2 = Times("chunk-two-is-longer-", 9);
    private static readonly byte[] C3 = Times("chunk-three-", 15);
    private static readonly int Total = C1.Length + C2.Length + C3.Length;

    /// <summary>
    /// Three WriteOffsetBytes appends assemble the exact concatenation with a running Size, and appends
    /// at a wrong offset fail InvalidWriteOffset (400) without changing the object.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-express-append-produces-the-exact-concatenation-of-three-offset-writes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // Express is not offered in every zone (an unsupported one fails InvalidBucketName), so try
        // each available zone id and keep the first that accepts a directory bucket.
        var zoneIds = S3Fidelity.AvailabilityZoneIds(ctx);
        Ensure(zoneIds.Count > 0, "DescribeAvailabilityZones returned no available zones");
        string? bucket = null;
        var refusals = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var zoneId in zoneIds)
        {
            var candidate = $"{FidelityContext.UniqueName("sim2real-behav-dir-")}--{zoneId}--x-s3";
            var refusal = await FidelityContext.CaptureError(() => S3Fidelity.CreateDirectoryBucketRaw(s3, candidate, zoneId));
            if (refusal is null)
            {
                bucket = candidate;
                ctx.OnCleanup(() => S3Fidelity.EmptyAndDeleteDirectoryBucket(s3, candidate));
                break;
            }

            refusals[zoneId] = refusal.Code;
        }

        Ensure(
            bucket is not null,
            $"no availability zone accepted an S3 Express directory bucket; the refusals were {Py.Repr(refusals)} over zones {Py.Repr(zoneIds)}");
        const string key = "appended.bin";

        // --- Write 1: the initial create. PutObject returns Size only on appends, so use HeadObject.
        var first = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(C1) });
        Ensure((int)first.HttpStatusCode == 200, $"the initial write returned {(int)first.HttpStatusCode}");
        Ensure(
            (await s3.GetObjectMetadataAsync(bucket, key)).ContentLength == C1.Length,
            $"after the first write the object is {(await s3.GetObjectMetadataAsync(bucket, key)).ContentLength} bytes, want {C1.Length}");

        // --- Writes 2 and 3: each at an offset equal to the CURRENT length.
        var second = await S3Fidelity.Append(s3, bucket!, key, C2, C1.Length);
        Ensure(
            second.Size == C1.Length + C2.Length,
            $"the second append reported Size {Py.Repr(second.Size)}, want the running total {C1.Length + C2.Length} — Size must be the object's length AFTER the append, not the length of the appended chunk");
        Ensure(
            (await s3.GetObjectMetadataAsync(bucket, key)).ContentLength == C1.Length + C2.Length,
            "HeadObject does not agree with the second append's reported Size");
        var third = await S3Fidelity.Append(s3, bucket!, key, C3, C1.Length + C2.Length);
        Ensure(third.Size == Total, $"the third append reported Size {Py.Repr(third.Size)}, want {Total}");

        // --- The assembled object is the exact concatenation, in order.
        byte[] all = [.. C1, .. C2, .. C3];
        var body = await S3Fidelity.GetBytes(s3, bucket!, key);
        Ensure(
            body.SequenceEqual(all),
            $"the assembled object is {body.Length} bytes starting {Py.Repr(body.Take(48).ToArray())}; want {Total} bytes starting {Py.Repr(all.Take(48).ToArray())} — the offset writes were not assembled in order");
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ContentLength == Total, $"HeadObject reports ContentLength {head.ContentLength}, want {Total}");
        var etagBefore = head.ETag;

        // --- Three wrong offsets, each InvalidWriteOffset, each leaving the object untouched.
        foreach (var (label, offset) in new[] { ("the start of the object", 0), ("an interior boundary", C1.Length), ("one byte past the end", Total + 1) })
        {
            var exc = await FidelityContext.CaptureError(() => S3Fidelity.Append(s3, bucket!, key, "MUST-NOT-LAND"u8.ToArray(), offset));
            Ensure(
                exc is not null,
                $"an append at {label} (WriteOffsetBytes={offset}) succeeded on a {Total}-byte object — WriteOffsetBytes is being ignored, so two concurrent appenders silently overwrite each other");
            Ensure(
                exc!.Code == "InvalidWriteOffset",
                $"expected InvalidWriteOffset for an append at {label} (WriteOffsetBytes={offset}), got {Py.Repr(exc.Code)}");
            Ensure(exc.Message == WriteOffsetMessage, $"unexpected InvalidWriteOffset message for an append at {label}: {Py.Repr(exc.Message)}");
            Ensure((int)exc.StatusCode == 400, $"expected HTTP 400 for an append at {label}, got {(int)exc.StatusCode}");
        }

        // --- Effect absent: none of the three refusals changed a byte.
        var after = await S3Fidelity.GetBytes(s3, bucket!, key);
        Ensure(after.SequenceEqual(all), $"a refused append modified the object: it is now {after.Length} bytes, want {Total}");
        Ensure((await s3.GetObjectMetadataAsync(bucket, key)).ETag == etagBefore, $"a refused append changed the object's ETag from {Py.Repr(etagBefore)}");

        // --- And a further LEGAL append still works, so the rejections did not wedge the object.
        var fourth = await S3Fidelity.Append(s3, bucket!, key, C1, Total);
        Ensure(
            fourth.Size == Total + C1.Length,
            $"the append after three refusals reported Size {Py.Repr(fourth.Size)}, want {Total + C1.Length}");
        var final = await S3Fidelity.GetBytes(s3, bucket!, key);
        Ensure(final.SequenceEqual([.. all, .. C1]), $"the final object is {final.Length} bytes, want {Total + C1.Length}");
    }

    private static byte[] Times(string text, int count) =>
        [.. Enumerable.Repeat(System.Text.Encoding.ASCII.GetBytes(text), count).SelectMany(b => b)];
}
