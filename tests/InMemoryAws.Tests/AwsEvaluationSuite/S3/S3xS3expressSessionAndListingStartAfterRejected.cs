// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_start_after_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-start-after-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingStartAfterRejected
{
    private const string Check = "s3x-s3express-session-and-listing-start-after-rejected";

    /// <summary>
    /// ListObjectsV2 with StartAfter is NotSupported (501) on an S3 Express directory bucket that lists
    /// normally without it, while a general-purpose twin accepts it and honours the marker: the refusal
    /// belongs to the bucket type.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-start-after-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var directory = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-sa-");
        var twin = await S3Fidelity.GeneralPurposeBucket(ctx, s3, "sim2real-s3x-sa-gp-", Check);
        const string key = "k/obj.txt";
        foreach (var bucket in new[] { directory, twin })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "listed" });
        }

        // Positive control: the directory bucket lists this object without StartAfter.
        var plain = S3Fidelity.SortedKeys(await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = directory, Prefix = "k/" }));
        Ensure(plain.SequenceEqual([key]), $"directory bucket listing without StartAfter returned {Py.Repr(plain)}");

        // The one thing under test.
        var (code, status, message) = await S3Fidelity.ErrorWithStatus(
            () => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = directory, StartAfter = "a/" }));
        Ensure(
            (code, status) == ("NotSupported", 501),
            $"directory bucket ListObjectsV2 StartAfter returned ({Py.Repr(code)}, {status}), expected ('NotSupported', 501)");
        const string want = "This bucket does not support start-after query parameter for ListObjectsV2 API.";
        Ensure(message == want, $"directory bucket ListObjectsV2 StartAfter said {Py.Repr(message)}, expected {Py.Repr(want)}");

        // The identical request on the general-purpose twin is accepted and honours the marker.
        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = twin, StartAfter = "a/" });
        Ensure(resp.StartAfter == "a/", $"twin did not echo StartAfter: {Py.Repr(resp.StartAfter)}");
        Ensure(S3Fidelity.SortedKeys(resp).SequenceEqual([key]), $"twin listing after 'a/' returned {Py.Repr(S3Fidelity.SortedKeys(resp))}, expected [{Py.Repr(key)}]");
        var past = S3Fidelity.SortedKeys(await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = twin, StartAfter = key }));
        Ensure(past.Count == 0, $"twin listing StartAfter the only key still returned {Py.Repr(past)}");
    }
}
