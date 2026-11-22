// Translated from behavioral/s3/check_gzip_object_etag_and_copy_metadata_directive_content_encoding.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.IO.Compression;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.gzip-object-etag-and-copy-metadata-directive-content-encoding</summary>
[Category("AwsEvaluationSuite")]
public sealed class GzipObjectEtagAndCopyMetadataDirectiveContentEncoding
{
    /// <summary>
    /// PutObject's ETag and length pin to the gzip bytes as sent; CopyObject's
    /// MetadataDirective decides whether Content-Encoding survives unrestated.
    /// </summary>
    [Test]
    [DisplayName("s3.gzip-object-etag-and-copy-metadata-directive-content-encoding")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var body = Gzip("a plain text payload compressed before it is stored"u8.ToArray());
        var expectedMd5 = S3Fidelity.Md5Hex(body);
        const string srcKey = "src.txt.gz";

        var putRequest = new PutObjectRequest { BucketName = bucket, Key = srcKey, InputStream = new MemoryStream(body), ContentType = "text/plain" };
        putRequest.Headers.ContentEncoding = "gzip";
        var put = await s3.PutObjectAsync(putRequest);
        var putEtag = put.ETag.Trim('"');
        Ensure(putEtag == expectedMd5, $"PutObject ETag {Py.Repr(putEtag)} != MD5 of the {body.Length} compressed bytes {Py.Repr(expectedMd5)}");

        var head = await s3.GetObjectMetadataAsync(bucket, srcKey);
        Ensure(head.ETag.Trim('"') == expectedMd5, $"HeadObject ETag {Py.Repr(head.ETag)} != MD5 of the compressed bytes {Py.Repr(expectedMd5)}");
        Ensure(head.Headers.ContentEncoding == "gzip", $"HeadObject ContentEncoding {Py.Repr(head.Headers.ContentEncoding)}, expected 'gzip'");
        Ensure(head.Headers.ContentType == "text/plain", $"HeadObject ContentType {Py.Repr(head.Headers.ContentType)}, expected 'text/plain'");
        Ensure(head.ContentLength == body.Length, $"HeadObject ContentLength {head.ContentLength}, expected {body.Length} (the compressed length)");

        using (var got = await s3.GetObjectAsync(bucket, srcKey))
        {
            Ensure(got.Headers.ContentEncoding == "gzip", $"GetObject ContentEncoding {Py.Repr(got.Headers.ContentEncoding)}, expected 'gzip'");
            Ensure(got.Headers.ContentType == "text/plain", $"GetObject ContentType {Py.Repr(got.Headers.ContentType)}, expected 'text/plain'");
            Ensure(got.ContentLength == body.Length, $"GetObject ContentLength {got.ContentLength}, expected {body.Length}");
            var raw = await S3Fidelity.ReadBody(got);
            Ensure(
                raw.SequenceEqual(body),
                $"GetObject returned {raw.Length} bytes that differ from the {body.Length} compressed bytes written — the target must serve the compressed body verbatim, not decode it");
        }

        const string copyKey = "copy-directive-copy.txt.gz";
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = srcKey,
            DestinationBucket = bucket,
            DestinationKey = copyKey,
            MetadataDirective = S3MetadataDirective.COPY,
        });
        var headCopy = await s3.GetObjectMetadataAsync(bucket, copyKey);
        Ensure(
            headCopy.Headers.ContentEncoding == "gzip",
            $"CopyObject MetadataDirective=COPY: HeadObject ContentEncoding {Py.Repr(headCopy.Headers.ContentEncoding)}, expected 'gzip' to survive the copy");

        const string replaceKey = "copy-directive-replace.txt.gz";
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = srcKey,
            DestinationBucket = bucket,
            DestinationKey = replaceKey,
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = "text/plain",
        });
        var headReplace = await s3.GetObjectMetadataAsync(bucket, replaceKey);
        Ensure(
            string.IsNullOrEmpty(headReplace.Headers.ContentEncoding),
            $"CopyObject MetadataDirective=REPLACE without restating ContentEncoding: HeadObject ContentEncoding is {Py.Repr(headReplace.Headers.ContentEncoding)}, expected it dropped (None)");
        Ensure(
            headReplace.Headers.ContentType == "text/plain",
            $"CopyObject MetadataDirective=REPLACE: HeadObject ContentType {Py.Repr(headReplace.Headers.ContentType)}, expected the restated 'text/plain'");
    }

    /// <summary><c>gzip.compress(data)</c>.</summary>
    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }
}
