// Translated from behavioral/s3/check_conc_s3_conditional_writes_unconditional_concurrent_puts_leave_exactly_one_whole_body.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-unconditional-concurrent-puts-leave-exactly-one-whole-body</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesUnconditionalConcurrentPutsLeaveExactlyOneWholeBody
{
    private const int Writers = 8;
    private const int BodyBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Concurrent unconditional PUTs of distinct random bodies to one key leave exactly one whole
    /// body, with GET, HEAD and ListObjectsV2 metadata matching that writer (any writer may win).
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-unconditional-concurrent-puts-leave-exactly-one-whole-body")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        // Default retries stay on: re-sending an unconditional PUT of the same body is harmless.
        var writerClient = ctx.S3;
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        const string key = "contended/last-writer-wins.bin";

        var bodies = Enumerable.Range(0, Writers).Select(_ => S3Fidelity.RandomBytes(BodyBytes)).ToList();
        Ensure(bodies.Select(S3Fidelity.Sha256Hex).Distinct(StringComparer.Ordinal).Count() == Writers, "bodies not distinct");
        var digestToWriter = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Writers; i++)
        {
            digestToWriter[S3Fidelity.Sha256Hex(bodies[i])] = i;
        }

        async Task<string> Write(int i) =>
            (await writerClient.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(bodies[i]) })).ETag;

        string[] putEtags;
        using (ctx.Concurrently())
        {
            putEtags = await Task.WhenAll(Enumerable.Range(0, Writers).Select(i => Task.Run(() => Write(i))));
        }

        for (var i = 0; i < Writers; i++)
        {
            Ensure(putEtags[i] == S3Fidelity.Md5ETag(bodies[i]), $"writer {i}'s PUT returned ETag {Py.Repr(putEtags[i])}, not the MD5 of its own body {Py.Repr(S3Fidelity.Md5ETag(bodies[i]))}");
        }

        using var got = await s3.GetObjectAsync(bucket, key);
        var body = await S3Fidelity.ReadBody(got);
        var digest = S3Fidelity.Sha256Hex(body);
        Ensure(body.Length == BodyBytes, $"the surviving object is {body.Length} bytes; want {BodyBytes} (truncated or padded)");
        Ensure(digestToWriter.ContainsKey(digest), $"the surviving object (sha256 {digest}) is none of the {Writers} bodies written: a mixture of concurrent writes");
        var winner = digestToWriter[digest];
        Ensure(
            got.ETag == putEtags[winner] && putEtags[winner] == S3Fidelity.Md5ETag(body),
            $"GET ETag {Py.Repr(got.ETag)} vs writer {winner}'s PUT ETag {Py.Repr(putEtags[winner])} vs body MD5 {Py.Repr(S3Fidelity.Md5ETag(body))}: the metadata belongs to a different write than the bytes");

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            head.ETag == got.ETag && head.ContentLength == BodyBytes,
            $"HeadObject reports ({Py.Repr(head.ETag)}, {head.ContentLength}); GET reported ({Py.Repr(got.ETag)}, {BodyBytes})");
        var listed = await S3Fidelity.ListedObjects(s3, bucket);
        Ensure(listed.SequenceEqual([(key, got.ETag, (long)BodyBytes)]), $"ListObjectsV2 reports {Py.Repr(listed.Select(l => l.ToString()).ToList())}; want exactly one entry matching the survivor");

        var again = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(again.AsSpan().SequenceEqual(body), "a second GET returned different bytes after all writers had finished");
    }
}
