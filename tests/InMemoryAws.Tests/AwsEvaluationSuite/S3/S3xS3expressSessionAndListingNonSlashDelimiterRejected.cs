// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_non_slash_delimiter_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-non-slash-delimiter-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingNonSlashDelimiterRejected
{
    private const string Check = "s3x-s3express-session-and-listing-non-slash-delimiter-rejected";

    /// <summary>
    /// ListObjectsV2 with a Delimiter other than '/' is InvalidRequest (400) on an S3 Express directory
    /// bucket, while a general-purpose twin accepts it and groups on that delimiter and '/' stays accepted
    /// on the directory bucket: the refusal belongs to the bucket type.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-non-slash-delimiter-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var directory = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-dl-");
        var twin = await S3Fidelity.GeneralPurposeBucket(ctx, s3, "sim2real-s3x-dl-gp-", Check);
        const string key = "k/part-1.txt";
        foreach (var bucket in new[] { directory, twin })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "grouped" });
        }

        // Positive control: '/' is accepted on the directory bucket and groups the key.
        var slash = S3Fidelity.SortedPrefixes(await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = directory, Delimiter = "/" }));
        Ensure(slash.SequenceEqual(["k/"]), $"directory bucket Delimiter='/' listed prefixes {Py.Repr(slash)}, expected ['k/']");

        // The one thing under test.
        var got = await S3Fidelity.ErrorWithStatus(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = directory, Delimiter = "-" }));
        var want = ("InvalidRequest", 400, "This bucket only supports '/' as a delimiter");
        Ensure(got == want, $"directory bucket ListObjectsV2 Delimiter='-' returned {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(want)}");

        // The identical request on the general-purpose twin is accepted and groups on '-'.
        var dash = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = twin, Delimiter = "-" });
        Ensure(
            S3Fidelity.SortedPrefixes(dash).SequenceEqual(["k/part-"]),
            $"twin Delimiter='-' listed prefixes {Py.Repr(S3Fidelity.SortedPrefixes(dash))}, expected ['k/part-']");
        Ensure(dash.S3Objects is not { Count: > 0 }, $"twin Delimiter='-' left the key ungrouped: {Py.Repr(S3Fidelity.SortedKeys(dash))}");
    }
}
