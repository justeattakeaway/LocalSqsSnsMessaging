// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_unterminated_prefix_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-unterminated-prefix-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingUnterminatedPrefixRejected
{
    private const string Check = "s3x-s3express-session-and-listing-unterminated-prefix-rejected";

    /// <summary>
    /// A Prefix not ending in '/' is InvalidRequest (400) on an S3 Express directory bucket, with or
    /// without Delimiter='/', while 'k/' lists the object there and a general-purpose twin accepts and
    /// matches 'k': the missing terminator on a directory bucket is the cause.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-unterminated-prefix-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var directory = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-px-");
        var twin = await S3Fidelity.GeneralPurposeBucket(ctx, s3, "sim2real-s3x-px-gp-", Check);
        const string key = "k/obj.txt";
        foreach (var bucket in new[] { directory, twin })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "prefixed" });
        }

        // Positive control: the '/'-terminated prefix lists the object on the directory bucket.
        var terminated = S3Fidelity.SortedKeys(await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = directory, Prefix = "k/" }));
        Ensure(terminated.SequenceEqual([key]), $"directory bucket Prefix='k/' returned {Py.Repr(terminated)}, expected [{Py.Repr(key)}]");

        // The one thing under test, alone and alongside the one legal delimiter.
        var want = ("InvalidRequest", 400,
            "This bucket does not support a prefix that does not end in a delimiter. Specify a prefix " +
            "path ending with a delimiter and try again.");
        var got = await S3Fidelity.ErrorWithStatus(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = directory, Prefix = "k" }));
        Ensure(got == want, $"directory bucket ListObjectsV2 Prefix='k' returned {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(want)}");
        got = await S3Fidelity.ErrorWithStatus(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = directory, Prefix = "k", Delimiter = "/" }));
        Ensure(got == want, $"directory bucket ListObjectsV2 Prefix='k' Delimiter='/' returned {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(want)}");

        // The identical request on the general-purpose twin is accepted and matches.
        var loose = S3Fidelity.SortedKeys(await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = twin, Prefix = "k" }));
        Ensure(loose.SequenceEqual([key]), $"twin Prefix='k' returned {Py.Repr(loose)}, expected [{Py.Repr(key)}]");
    }
}
