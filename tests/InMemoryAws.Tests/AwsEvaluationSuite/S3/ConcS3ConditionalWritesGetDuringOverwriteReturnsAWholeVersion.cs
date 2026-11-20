// Translated from behavioral/s3/check_conc_s3_conditional_writes_get_during_overwrite_returns_a_whole_version.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-get-during-overwrite-returns-a-whole-version</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesGetDuringOverwriteReturnsAWholeVersion
{
    private const int BodyBytes = 4 * 1024 * 1024;
    private const int Overwrites = 6;
    private const int Readers = 3;

    /// <summary>
    /// GETs that overlap a chain of If-Match overwrites each return one whole known version with its
    /// own ETag and full length, never a torn or mislabelled body, and the GET after the writer finishes
    /// returns the last version.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-get-during-overwrite-returns-a-whole-version")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var poolClient = ctx.S3;
        // The writer's PUTs are conditional, so a transport retry of one that landed would read 412.
        var writerClient = ctx.S3Client(maxAttempts: 1);
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        const string key = "hot/config-blob.bin";

        var versions = Enumerable.Range(0, Overwrites + 1).Select(_ => S3Fidelity.RandomBytes(BodyBytes)).ToList();
        var byDigest = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < versions.Count; i++)
        {
            byDigest[S3Fidelity.Sha256Hex(versions[i])] = i;
        }

        Ensure(byDigest.Count == versions.Count, "version bodies are not distinct");
        var etag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(versions[0]) })).ETag;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Writer()
        {
            try
            {
                for (var i = 1; i <= Overwrites; i++)
                {
                    etag = (await writerClient.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        InputStream = new MemoryStream(versions[i]),
                        IfMatch = etag,
                    })).ETag;
                }
            }
            finally
            {
                done.TrySetResult();
            }
        }

        async Task<List<int>> Reader(int r)
        {
            var seen = new List<int>();
            while (true)
            {
                var lastRound = done.Task.IsCompleted;
                using var got = await poolClient.GetObjectAsync(bucket, key);
                var body = await S3Fidelity.ReadBody(got);
                var digest = S3Fidelity.Sha256Hex(body);
                Ensure(
                    body.Length == BodyBytes && got.ContentLength == BodyBytes,
                    $"reader {r} got {body.Length} bytes with Content-Length {got.ContentLength}; every version is {BodyBytes} bytes: a partial read");
                Ensure(
                    byDigest.ContainsKey(digest),
                    $"reader {r} got a body (sha256 {digest}) matching none of the {versions.Count} versions ever written: a torn or mixed read");
                Ensure(
                    got.ETag == S3Fidelity.Md5ETag(body),
                    $"reader {r} got version {byDigest[digest]}'s bytes under ETag {Py.Repr(got.ETag)}; that version's ETag is {Py.Repr(S3Fidelity.Md5ETag(body))}");
                seen.Add(byDigest[digest]);
                if (lastRound)
                {
                    return seen;
                }
            }
        }

        List<int>[] seenAll;
        using (ctx.Concurrently())
        {
            var w = Task.Run(Writer);
            var readers = Enumerable.Range(0, Readers).Select(r => Task.Run(() => Reader(r))).ToList();
            await w;
            seenAll = await Task.WhenAll(readers);
        }

        for (var r = 0; r < seenAll.Length; r++)
        {
            var s = seenAll[r];
            Ensure(s.Count > 0, $"reader {r} completed no reads");
            Ensure(s[^1] == Overwrites, $"reader {r}'s read issued after the writer finished saw version {s[^1]}, not the last written {Overwrites}: a stale read after a completed overwrite");
        }

        using var final = await s3.GetObjectAsync(bucket, key);
        Ensure((await S3Fidelity.ReadBody(final)).AsSpan().SequenceEqual(versions[Overwrites]), "the final GET is not the last version");
        Ensure(
            final.ETag == etag && etag == S3Fidelity.Md5ETag(versions[Overwrites]),
            $"final ETag {Py.Repr(final.ETag)}, writer's last ETag {Py.Repr(etag)}: want equal and the MD5");
    }
}
