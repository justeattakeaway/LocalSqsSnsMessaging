// Translated from behavioral/s3/check_deep_s3_immutability_copy_metadata_directive_replace_drops_unrestated.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-copy-metadata-directive-replace-drops-unrestated</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityCopyMetadataDirectiveReplaceDropsUnrestated
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("bytes-that-every-copy-must-carry-unchanged");
    private static readonly Dictionary<string, string> SrcMeta = new() { ["owner"] = "team-a", ["stage"] = "raw" };

    /// <summary>
    /// MetadataDirective=COPY carries user metadata and system headers and ignores request metadata;
    /// REPLACE keeps only what the request restates and resets Content-Type; bytes and ETag match.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-copy-metadata-directive-replace-drops-unrestated")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-imm-");

        var put = new PutObjectRequest { BucketName = bucket, Key = "src.txt", InputStream = new MemoryStream(Body), ContentType = "text/plain" };
        put.Headers.CacheControl = "max-age=60";
        put.Headers.ContentDisposition = "attachment; filename=src.txt";
        foreach (var (name, value) in SrcMeta)
        {
            put.Metadata.Add(name, value);
        }

        var srcEtag = (await s3.PutObjectAsync(put)).ETag;

        // --- COPY: everything comes from the source; request metadata is ignored, not merged.
        var copy = new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = "copied.txt", SourceBucket = bucket, SourceKey = "src.txt", MetadataDirective = S3MetadataDirective.COPY };
        copy.Metadata.Add("injected", "must-not-appear");
        await s3.CopyObjectAsync(copy);
        var h = await s3.GetObjectMetadataAsync(bucket, "copied.txt");
        var meta = S3Fidelity.UserMetadata(h.Metadata);
        Ensure(
            Py.Equal(meta, SrcMeta),
            $"COPY produced user metadata {Py.Repr(meta)}; want exactly the source's {Py.Repr(SrcMeta)} (metadata on a COPY request is ignored)");
        Ensure(
            (h.Headers.ContentType, h.Headers.CacheControl, h.Headers.ContentDisposition) == ("text/plain", "max-age=60", "attachment; filename=src.txt"),
            $"COPY carried ContentType/CacheControl/ContentDisposition as ({Py.Repr(h.Headers.ContentType)}, {Py.Repr(h.Headers.CacheControl)}, {Py.Repr(h.Headers.ContentDisposition)})");
        Ensure(h.ETag == srcEtag, $"COPY changed the ETag: {Py.Repr(h.ETag)} vs source {Py.Repr(srcEtag)}");

        // --- REPLACE restating ONE user key and nothing else: all unrestated headers are dropped.
        var replace = new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = "replaced.txt", SourceBucket = bucket, SourceKey = "src.txt", MetadataDirective = S3MetadataDirective.REPLACE };
        replace.Metadata.Add("stage", "curated");
        await s3.CopyObjectAsync(replace);
        h = await s3.GetObjectMetadataAsync(bucket, "replaced.txt");
        meta = S3Fidelity.UserMetadata(h.Metadata);
        Ensure(
            Py.Equal(meta, new Dictionary<string, string> { ["stage"] = "curated" }),
            $"REPLACE produced user metadata {Py.Repr(meta)}; want only the restated {{'stage': 'curated'}} — 'owner' was not restated and must be gone");
        Ensure(
            h.Headers.ContentType == "binary/octet-stream",
            $"REPLACE without a ContentType left ContentType {Py.Repr(h.Headers.ContentType)}; S3 resets it to its default 'binary/octet-stream'");
        Ensure(
            h.Headers.CacheControl is null && h.Headers.ContentDisposition is null,
            $"REPLACE kept unrestated system headers: CacheControl={Py.Repr(h.Headers.CacheControl)}, ContentDisposition={Py.Repr(h.Headers.ContentDisposition)}");
        Ensure(h.ETag == srcEtag, $"REPLACE changed the ETag: {Py.Repr(h.ETag)} vs {Py.Repr(srcEtag)}");

        // --- REPLACE restating system headers: exactly what was restated.
        var retype = new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "retyped.txt",
            SourceBucket = bucket,
            SourceKey = "src.txt",
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = "application/json",
        };
        retype.Headers.CacheControl = "no-store";
        await s3.CopyObjectAsync(retype);
        h = await s3.GetObjectMetadataAsync(bucket, "retyped.txt");
        meta = S3Fidelity.UserMetadata(h.Metadata);
        Ensure(
            h.Headers.ContentType == "application/json" && h.Headers.CacheControl == "no-store" && meta.Count == 0,
            $"REPLACE with restated headers produced ({Py.Repr(h.Headers.ContentType)}, {Py.Repr(h.Headers.CacheControl)}, {Py.Repr(meta)})");
        Ensure(h.Headers.ContentDisposition is null, $"REPLACE kept the unrestated ContentDisposition {Py.Repr(h.Headers.ContentDisposition)}");

        // --- Every copy carries identical bytes, and the source itself is untouched.
        foreach (var key in new[] { "copied.txt", "replaced.txt", "retyped.txt" })
        {
            var body = await S3Fidelity.GetBytes(s3, bucket, key);
            Ensure(body.AsSpan().SequenceEqual(Body), $"{key} holds {Py.Repr(body)}, want the source bytes {Py.Repr(Body)}");
        }

        h = await s3.GetObjectMetadataAsync(bucket, "src.txt");
        meta = S3Fidelity.UserMetadata(h.Metadata);
        Ensure(
            Py.Equal(meta, SrcMeta) && h.Headers.ContentType == "text/plain",
            $"copying mutated the source: metadata {Py.Repr(meta)}, ContentType {Py.Repr(h.Headers.ContentType)}");
    }
}
