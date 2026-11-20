// Translated from behavioral/s3/check_s3checksum_part_scoped_read_returns_the_part_digest_not_the_composite.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-part-scoped-read-returns-the-part-digest-not-the-composite</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumPartScopedReadReturnsThePartDigestNotTheComposite
{
    /// <summary>The multipart minimum for every part but the last.</summary>
    private const int PartSize = 5 * 1024 * 1024;
    private const int TailSize = 4096;
    private const int NParts = 3;

    /// <summary>
    /// A part-scoped GET/HEAD returns that part's own SHA256; a whole-object read, the composite.
    /// Parallel downloaders verify each fetched part against its own digest, so S3 must keep per-part
    /// digests; every expected value is computed locally with hashlib.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-part-scoped-read-returns-the-part-digest-not-the-composite")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "part-digests.bin";

        var parts = Enumerable.Range(0, NParts - 1).Select(i => S3Fidelity.Repeat((char)(80 + i), PartSize)).Append(S3Fidelity.Repeat('t', TailSize)).ToList();
        var whole = parts.SelectMany(p => p).ToArray();
        var partDigests = parts.Select(S3Fidelity.Sha256B64).ToList();
        Ensure(
            partDigests.Distinct().Count() == NParts,
            $"the {NParts} parts must have distinct digests for a per-part read to be meaningful; got {Py.Repr(partDigests)}");
        var wantComposite = S3Fidelity.Sha256B64([.. parts.SelectMany(SHA256.HashData)]) + $"-{NParts}";

        var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ChecksumAlgorithm = ChecksumAlgorithm.SHA256 });
        var uploadId = created.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var completed = new List<PartETag>();
        for (var number = 1; number <= parts.Count; number++)
        {
            var chunk = parts[number - 1];
            var uploaded = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = number,
                InputStream = new MemoryStream(chunk),
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumSHA256 = partDigests[number - 1],
            });
            Ensure(
                uploaded.ChecksumSHA256 == partDigests[number - 1],
                $"UploadPart {number} reported {Py.Repr(uploaded.ChecksumSHA256)}, the client computed {Py.Repr(partDigests[number - 1])}");
            completed.Add(new PartETag { PartNumber = number, ETag = uploaded.ETag, ChecksumSHA256 = uploaded.ChecksumSHA256 });
        }

        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = completed });
        Ensure(
            done.ChecksumSHA256 == wantComposite,
            $"the completion reported {Py.Repr(done.ChecksumSHA256)}, the client computed the composite {Py.Repr(wantComposite)}");

        // --- The whole-object read returns the composite, for both GET and HEAD.
        using (var wholeGet = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }))
        {
            Ensure(wholeGet.ChecksumSHA256 == wantComposite, $"a whole-object GET reported {Py.Repr(wholeGet.ChecksumSHA256)}, want {Py.Repr(wantComposite)}");
            Ensure(
                (await S3Fidelity.ReadBody(wholeGet)).AsSpan().SequenceEqual(whole),
                $"the whole-object GET did not return the {whole.Length} bytes that were uploaded");
        }

        var wholeHead = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(wholeHead.ChecksumSHA256 == wantComposite, $"a whole-object HEAD reported {Py.Repr(wholeHead.ChecksumSHA256)}, want {Py.Repr(wantComposite)}");

        // --- Each part-scoped read returns THAT part's digest and THAT part's bytes.
        for (var number = 1; number <= parts.Count; number++)
        {
            var chunk = parts[number - 1];
            var want = partDigests[number - 1];
            using (var partGet = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, PartNumber = number, ChecksumMode = ChecksumMode.ENABLED }))
            {
                Ensure(
                    partGet.HttpStatusCode == HttpStatusCode.PartialContent,
                    $"a part-scoped GET is a partial response; part {number} returned {(int)partGet.HttpStatusCode}");
                Ensure(
                    partGet.PartsCount == NParts,
                    $"GET PartNumber={number} reported PartsCount {Py.Repr(partGet.PartsCount)}, want {NParts} — the part boundaries were not retained through completion");
                Ensure(
                    partGet.ChecksumSHA256 == want,
                    $"GET PartNumber={number} reported ChecksumSHA256={Py.Repr(partGet.ChecksumSHA256)}; the client computed {Py.Repr(want)} over that part's {chunk.Length} bytes. Returning the composite {Py.Repr(wantComposite)} here leaves a verifying downloader unable to check anything it actually received");
                Ensure(partGet.ChecksumSHA256 != wantComposite, $"GET PartNumber={number} returned the whole object's composite value");
                var partBody = await S3Fidelity.ReadBody(partGet);
                Ensure(partBody.AsSpan().SequenceEqual(chunk), $"GET PartNumber={number} returned {partBody.Length} bytes, want {chunk.Length}");
                Ensure(
                    S3Fidelity.Sha256B64(partBody) == partGet.ChecksumSHA256,
                    $"part {number}: the bytes S3 returned hash to {Py.Repr(S3Fidelity.Sha256B64(partBody))} but S3 reported {Py.Repr(partGet.ChecksumSHA256)} for them");
            }

            var partHead = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, PartNumber = number, ChecksumMode = ChecksumMode.ENABLED });
            Ensure(partHead.ChecksumSHA256 == want, $"HEAD PartNumber={number} reported {Py.Repr(partHead.ChecksumSHA256)}, want {Py.Repr(want)}");
            Ensure(partHead.ContentLength == chunk.Length, $"HEAD PartNumber={number} reported ContentLength {partHead.ContentLength}, want {chunk.Length}");
        }
    }
}
