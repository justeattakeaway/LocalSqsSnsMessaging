// Translated from behavioral/s3/check_s3unicode_copy_source_with_plus_and_percent_resolves_literally.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-copy-source-with-plus-and-percent-resolves-literally</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeCopySourceWithPlusAndPercentResolvesLiterally
{
    private const string NoSuchKeyMsg = "The specified key does not exist.";

    /// <summary>
    /// CopyObject reads its copy-source key literally: '+', space, '%' and an emoji name the source.
    /// The copy is byte-identical with the same ETag; spelling '+' as a space is NoSuchKey and creates
    /// no destination object.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-copy-source-with-plus-and-percent-resolves-literally")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string emoji = "\U0001f600";
        const string srcPrefix = "cpsrc/";
        const string dstPrefix = "cpdst/";
        const string srcKey = $"{srcPrefix}a+b c%20d-{emoji}.bin";
        const string dstKey = $"{dstPrefix}{emoji} plus+pct%25 café.bin";
        byte[] body = [0x00, 0xff, 0xfe, .. Encoding.UTF8.GetBytes(emoji), .. "copy-source-payload\r\n"u8];

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, InputStream = new MemoryStream(body) });
        var srcEtag = (await s3.GetObjectMetadataAsync(bucket, srcKey)).ETag;
        Ensure(
            srcEtag.Trim('"') == S3Fidelity.Md5Hex(body),
            $"the source object's ETag is not the MD5 of the bytes we sent: {Py.Repr(srcEtag)} vs {Py.Repr(S3Fidelity.Md5Hex(body))}");

        // --- The copy resolves the reserved-character source and lands byte-identical.
        var copied = await s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = dstKey, SourceBucket = bucket, SourceKey = srcKey });
        Ensure(
            copied.ETag == srcEtag,
            $"the copy's ETag does not match the source's: {Py.Repr(copied.ETag)} vs {Py.Repr(srcEtag)} -- the content changed while being copied");
        var got = await S3Fidelity.GetBytes(s3, bucket, dstKey);
        Ensure(got.AsSpan().SequenceEqual(body), $"the copied object's bytes differ from the source's: sent {Py.Repr(body)}, got {Py.Repr(got)}");

        // --- The destination key -- itself full of reserved characters -- lists back byte-exactly.
        var listed = await ListKeys(s3, bucket, dstPrefix);
        Ensure(listed.SequenceEqual([dstKey]), $"ListObjectsV2 did not return exactly the copy destination: expected [{Py.Repr(dstKey)}], got {Py.Repr(listed)}");
        Ensure(
            Encoding.UTF8.GetBytes(listed[0]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(dstKey)),
            $"the listed destination key's bytes differ from the requested key's: listed {Py.Repr(Encoding.UTF8.GetBytes(listed[0]))}, requested {Py.Repr(Encoding.UTF8.GetBytes(dstKey))}");

        // --- Break exactly one thing: spell the source's '+' as a space.
        var badSource = srcKey.Replace("a+b", "a b", StringComparison.Ordinal);
        Ensure(badSource != srcKey, "test setup error: the '+'-as-space source equals the real source");
        const string neverKey = $"{dstPrefix}must-not-exist.bin";
        var exc = await S3Fidelity.ExpectError(
            () => s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = neverKey, SourceBucket = bucket, SourceKey = badSource }),
            $"CopyObject accepted the source key {Py.Repr(badSource)}, which no object has; the '+' in the copy-source header is being form-decoded as a space");
        Ensure(exc.Code == "NoSuchKey", $"expected NoSuchKey for a copy source that spells '+' as a space, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == NoSuchKeyMsg, $"expected message {Py.Repr(NoSuchKeyMsg)}, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.NotFound, $"expected HTTP 404 for NoSuchKey, got {(int)exc.StatusCode}");

        // --- Effect absent: the rejected copy created no destination object.
        exc = await S3Fidelity.ExpectError(() => s3.GetObjectMetadataAsync(bucket, neverKey), $"the rejected copy created {Py.Repr(neverKey)} anyway; the failure was not atomic");
        Ensure(exc.StatusCode == HttpStatusCode.NotFound, $"expected HTTP 404 for the object the rejected copy must not have created, got {(int)exc.StatusCode}");

        // --- Positive control: the good copy is untouched by the rejection.
        listed = await ListKeys(s3, bucket, dstPrefix);
        Ensure(listed.SequenceEqual([dstKey]), $"the destination prefix changed after the rejected copy: {Py.Repr(listed)}");
        var final = await S3Fidelity.GetBytes(s3, bucket, dstKey);
        Ensure(final.AsSpan().SequenceEqual(body), $"the good copy was altered by the rejected copy: expected {Py.Repr(body)}, got {Py.Repr(final)}");
    }

    /// <summary><c>_list_keys(s3, bucket, prefix)</c>: every key under the prefix, across all pages.</summary>
    private static async Task<List<string>> ListKeys(IAmazonS3 s3, string bucket, string prefix) =>
        await S3CoreFidelity.ListAllKeys(s3, bucket, prefix);
}
