// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_readwrite_session_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-readwrite-session-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingReadwriteSessionRoundTrip
{
    /// <summary>
    /// A ReadWrite CreateSession token, as the sole signing identity, writes an object, reads it back
    /// byte-exact, lists it as EXPRESS_ONEZONE and deletes it; the client never mints a session.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-readwrite-session-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-rw-");
        var session = await S3Fidelity.CreateSession(s3, bucket, "ReadWrite");
        var creds = session.Credentials;
        Ensure(!string.IsNullOrEmpty(creds.AccessKeyId) && !string.IsNullOrEmpty(creds.SessionToken), "CreateSession returned no token");
        var (rw, stats) = S3Fidelity.SessionClient(ctx, session);

        const string key = "rw/payload.bin";
        var body = Enumerable.Range(0, 16).SelectMany(_ => Enumerable.Range(0, 256).Select(b => (byte)b)).ToArray(); // every byte value, so a lossy/text path cannot pass
        var put = await rw.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
        Ensure(!string.IsNullOrEmpty(put.ETag), "PutObject through the ReadWrite session returned no ETag");
        using (var got = await rw.GetObjectAsync(bucket, key))
        {
            var data = await S3Fidelity.ReadBody(got);
            Ensure(data.SequenceEqual(body), $"read-back differs: {data.Length} bytes vs {body.Length} written");
            Ensure(got.ETag == put.ETag, $"GetObject ETag {Py.Repr(got.ETag)} != PutObject ETag {Py.Repr(put.ETag)}");
        }

        var listed = await rw.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "rw/" });
        var entries = (listed.S3Objects ?? []).ToDictionary(o => o.Key, StringComparer.Ordinal);
        Ensure(entries.Keys.SequenceEqual([key]), $"session listing under rw/ returned {Py.Repr(entries.Keys.Order(StringComparer.Ordinal).ToList())}, expected [{Py.Repr(key)}]");
        Ensure(entries[key].Size == body.Length, $"listed Size {entries[key].Size} != {body.Length}");
        Ensure(
            entries[key].StorageClass?.Value == "EXPRESS_ONEZONE",
            $"directory-bucket object listed with StorageClass {Py.Repr(entries[key].StorageClass?.Value)}");

        await rw.DeleteObjectAsync(bucket, key);
        var refusal = await S3Fidelity.ErrorWithStatus(() => rw.GetObjectAsync(bucket, key));
        Ensure(
            refusal == ("NoSuchKey", 404, "The specified key does not exist."),
            $"GetObject after DeleteObject returned {S3Fidelity.Repr(refusal)}");
        Ensure(
            !stats.Ops.Contains("CreateSession"),
            $"the session-pinned client minted its own session (ops {Py.Repr(stats.Ops)}) -- the token under test was not the one that signed");
        Ensure(
            stats.Pinned == stats.Ops.Count && stats.Ops.Count == 5,
            $"expected all 5 data-plane calls signed with the minted session, got pinned={stats.Pinned} ops={Py.Repr(stats.Ops)}");
    }
}
