// Translated from behavioral/s3/check_conc_s3_conditional_writes_concurrent_multipart_completions_exactly_one_wins.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-concurrent-multipart-completions-exactly-one-wins</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesConcurrentMultipartCompletionsExactlyOneWins
{
    private const int Racers = 6;
    private const int PartBytes = 262_144;

    /// <summary>
    /// Racing CompleteMultipartUpload calls for one new key with If-None-Match:* produce exactly one
    /// winner; every loser gets PreconditionFailed with its exact message, and the object holds the
    /// winner's bytes and ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-concurrent-multipart-completions-exactly-one-wins")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var racerClient = ctx.S3Client(maxAttempts: 1);
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        const string key = "lock/leader-election.bin";

        var bodies = Enumerable.Range(0, Racers).Select(i => S3Fidelity.Pattern($"racer-{i}", PartBytes)).ToList();
        var uploads = new List<(string UploadId, string ETag)>();
        for (var i = 0; i < Racers; i++)
        {
            var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
            ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));
            var etag = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(bodies[i]) })).ETag;
            uploads.Add((uploadId, etag));
        }

        var before = await HeadStatus(s3, bucket, key);
        Ensure(before == 404, $"HeadObject before any completion returned {before}; want 404");

        async Task<(int I, bool Ok, string? ETag, string? Code, string? Message, int Status)> Complete(int i)
        {
            var (uploadId, etag) = uploads[i];
            try
            {
                var resp = await racerClient.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = [S3Fidelity.Part(1, etag)],
                    IfNoneMatch = "*",
                });
                return (i, true, resp.ETag, null, null, 200);
            }
            catch (AmazonServiceException ex)
            {
                var error = AwsError.From(ex);
                return (i, false, null, error.Code, error.Message, (int)error.StatusCode);
            }
        }

        (int I, bool Ok, string? ETag, string? Code, string? Message, int Status)[] results;
        using (ctx.Concurrently())
        {
            results = await Task.WhenAll(Enumerable.Range(0, Racers).Select(i => Task.Run(() => Complete(i))));
        }

        var winners = results.Where(r => r.Ok).ToList();
        var losers = results.Where(r => !r.Ok).ToList();
        Ensure(winners.Count == 1, $"expected exactly one completion to win If-None-Match:*, got {winners.Count}: {Py.Repr(results.Select(r => r.ToString()).ToList())}");
        Ensure(losers.Count == Racers - 1, $"expected {Racers - 1} losers: {Py.Repr(results.Select(r => r.ToString()).ToList())}");
        Ensure(
            losers.All(r => r.Code == "PreconditionFailed"),
            $"every losing completion must be PreconditionFailed, got {Py.Repr(losers.Select(r => $"({r.Code}, {r.Status})").ToList())}");
        const string want = "At least one of the pre-conditions you specified did not hold";
        Ensure(losers.All(r => r.Message == want), $"every losing completion must say {Py.Repr(want)}, got {Py.Repr(losers.Select(r => r.Message).ToList())}");

        var winner = winners[0];
        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(
                body.AsSpan().SequenceEqual(bodies[winner.I]),
                $"the stored object (sha256 {S3Fidelity.Sha256Hex(body)}, {body.Length} bytes) is not racer {winner.I}'s parts; matches racer {Py.Repr(Enumerable.Range(0, Racers).Where(j => bodies[j].AsSpan().SequenceEqual(body)).ToList())}");
            Ensure(got.ETag == winner.ETag, $"GET ETag {Py.Repr(got.ETag)} differs from the winning completion's {Py.Repr(winner.ETag)}");
            Ensure(got.ETag.EndsWith("-1\"", StringComparison.Ordinal), $"the object ETag {Py.Repr(got.ETag)} is not a one-part multipart ETag");
        }

        var listed = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => (o.Key, o.Size)).ToList();
        Ensure(listed.SequenceEqual([(key, (long)PartBytes)]), $"the bucket lists {Py.Repr(listed.Select(l => l.ToString()).ToList())}; want exactly one {PartBytes}-byte object at {Py.Repr(key)}");
    }

    /// <summary><c>_head_status</c>: HeadObject's HTTP status, 200 when the key exists.</summary>
    private static async Task<int> HeadStatus(IAmazonS3 s3, string bucket, string key)
    {
        var error = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        return error is null ? 200 : (int)error.StatusCode;
    }
}
