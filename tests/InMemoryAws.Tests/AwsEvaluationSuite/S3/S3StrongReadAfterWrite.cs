// Translated from behavioral/s3/check_s3_strong_read_after_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-strong-read-after-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3StrongReadAfterWrite
{
    /// <summary>A brand-new key is immediately GETtable and visible in ListObjectsV2, and an
    /// immediate overwrite of that key is immediately GETtable too (strong read-after-write,
    /// for both first-write and overwrite).</summary>
    [Test]
    [DisplayName("s3.s3-strong-read-after-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var key = $"raw/{Guid.NewGuid():N}.txt";
        const string body = "strong-read-after-write-sim2real";

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = body });

        // Immediate GET — no retry/poll: S3 is strongly consistent for new objects.
        var got = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(got == body, $"expected immediate read-your-write, got {Py.Repr(got)}");

        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "raw/" });
        var listed = (resp.S3Objects ?? []).Select(o => o.Key).ToHashSet();
        Ensure(listed.Contains(key), $"new key {key} not immediately visible in ListObjectsV2: {Py.Repr(listed.ToList())}");

        const string overwriteBody = "strong-read-after-overwrite-sim2real";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = overwriteBody });

        // Immediate GET after overwriting an *existing* key — no retry/poll: S3 has been
        // strongly consistent for overwrites (not just first-writes) since December 2020.
        var gotAfterOverwrite = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(gotAfterOverwrite == overwriteBody, $"expected immediate read-your-overwrite, got {Py.Repr(gotAfterOverwrite)}");
    }
}
