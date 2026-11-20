// Translated from behavioral/s3/check_deep_s3_immutability_copy_unknown_tagging_directive_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-copy-unknown-tagging-directive-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityCopyUnknownTaggingDirectiveRejected
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("source-for-a-copy-with-a-bogus-directive");

    /// <summary>
    /// CopyObject with a TaggingDirective outside {COPY, REPLACE} is rejected with InvalidArgument and
    /// writes no destination; the identical copy with REPLACE succeeds and replaces the tags.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-copy-unknown-tagging-directive-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-imm-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "src.bin", InputStream = new MemoryStream(Body), TagSet = [new Tag { Key = "class", Value = "gold" }] });

        // botocore does not enforce enums client side, so the bogus value reaches the service verbatim.
        // The .NET SDK sends TaggingDirective=REPLACE whenever it sends tags, so this request goes raw.
        var (status, code, message) = await S3Fidelity.SendRaw(
            ctx,
            HttpMethod.Put,
            $"{bucket}/dest.bin",
            ("x-amz-copy-source", $"{bucket}/src.bin"),
            ("x-amz-tagging-directive", "MERGE"),
            ("x-amz-tagging", "class=silver"));
        if (code is null)
        {
            throw Fail($"a CopyObject with TaggingDirective=MERGE unexpectedly SUCCEEDED (HTTP {status}) where real AWS refuses it");
        }

        Ensure(
            (code, status, message) == ("InvalidArgument", 400, "Unknown tagging directive."),
            $"expected InvalidArgument/400 with AWS's exact message for an unknown TaggingDirective, got {Py.Repr(code)}/{status}/{Py.Repr(message)}");

        // Effect absent: nothing was written at the destination.
        var absent = await S3Fidelity.ExpectError(
            () => s3.GetObjectMetadataAsync(bucket, "dest.bin"),
            "HeadObject of the destination the refused copy must not have written unexpectedly SUCCEEDED where real AWS refuses it");
        Ensure(
            (int)absent.StatusCode == 404,
            $"after the refused copy, HeadObject on the destination returned {Py.Repr(S3Fidelity.HeadError(absent).Code)}/{(int)absent.StatusCode}; want 404 — the refused copy wrote an object");

        // Positive control: the identical copy with a valid directive succeeds and replaces the tags.
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "dest.bin",
            SourceBucket = bucket,
            SourceKey = "src.bin",
            TaggingDirective = TaggingDirective.REPLACE,
            TagSet = [new Tag { Key = "class", Value = "silver" }],
        });
        var body = await S3Fidelity.GetBytes(s3, bucket, "dest.bin");
        Ensure(body.AsSpan().SequenceEqual(Body), $"the valid copy holds {Py.Repr(body)}, want {Py.Repr(Body)}");
        var got = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = "dest.bin" })).Tagging ?? [];
        Ensure(
            got.Select(t => (t.Key, t.Value)).SequenceEqual([("class", "silver")]),
            $"the valid REPLACE copy has tags {Py.Repr(got.Select(t => $"{t.Key}={t.Value}"))}; want [class=silver]");
        var srcTags = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = "src.bin" })).Tagging ?? [];
        Ensure(
            srcTags.Select(t => (t.Key, t.Value)).SequenceEqual([("class", "gold")]),
            $"the source's tags changed: {Py.Repr(srcTags.Select(t => $"{t.Key}={t.Value}"))}");
    }
}
