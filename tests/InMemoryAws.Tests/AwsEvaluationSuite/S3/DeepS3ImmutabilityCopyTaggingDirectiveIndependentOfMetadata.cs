// Translated from behavioral/s3/check_deep_s3_immutability_copy_tagging_directive_independent_of_metadata.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-copy-tagging-directive-independent-of-metadata</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityCopyTaggingDirectiveIndependentOfMetadata
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("tagged-source-bytes");
    private static readonly Dictionary<string, string> SrcMeta = new() { ["owner"] = "team-a" };
    private static readonly (string Key, string Value)[] SrcTags = [("class", "gold"), ("tier", "hot")];

    /// <summary>
    /// CopyObject applies TaggingDirective and MetadataDirective independently across all four
    /// combinations, an omitted TaggingDirective means COPY, TaggingDirective=REPLACE without Tagging
    /// yields an empty tag set, and the source's tags and metadata stay unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-copy-tagging-directive-independent-of-metadata")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-imm-");
        var srcTags = SrcTags.Order().ToList();
        var put = new PutObjectRequest
        {
            BucketName = bucket,
            Key = "src.bin",
            InputStream = new MemoryStream(Body),
            TagSet = [.. SrcTags.Select(t => new Tag { Key = t.Key, Value = t.Value })],
        };
        put.Metadata.Add("owner", "team-a");
        await s3.PutObjectAsync(put);
        Ensure((await TagsOf(s3, bucket, "src.bin")).SequenceEqual(srcTags), "the source's inline tags did not land");

        CopyObjectRequest Copy(string key) => new() { DestinationBucket = bucket, DestinationKey = key, SourceBucket = bucket, SourceKey = "src.bin" };

        // (metadata COPY, tagging REPLACE)
        var mcTr = Copy("mc-tr.bin");
        mcTr.MetadataDirective = S3MetadataDirective.COPY;
        mcTr.TaggingDirective = TaggingDirective.REPLACE;
        mcTr.TagSet = [new Tag { Key = "class", Value = "bronze" }];
        await s3.CopyObjectAsync(mcTr);
        // (metadata REPLACE, tagging COPY)
        var mrTc = Copy("mr-tc.bin");
        mrTc.MetadataDirective = S3MetadataDirective.REPLACE;
        mrTc.Metadata.Add("owner", "team-b");
        mrTc.TaggingDirective = TaggingDirective.COPY;
        await s3.CopyObjectAsync(mrTc);
        // (metadata REPLACE, tagging REPLACE)
        var mrTr = Copy("mr-tr.bin");
        mrTr.MetadataDirective = S3MetadataDirective.REPLACE;
        mrTr.Metadata.Add("owner", "team-c");
        mrTr.TaggingDirective = TaggingDirective.REPLACE;
        mrTr.TagSet = [new Tag { Key = "tier", Value = "cold" }];
        await s3.CopyObjectAsync(mrTr);
        // (both omitted: both default to COPY)
        await s3.CopyObjectAsync(Copy("default.bin"));
        // (tagging REPLACE with no Tagging at all: the tag set is emptied)
        var trEmpty = Copy("tr-empty.bin");
        trEmpty.TaggingDirective = TaggingDirective.REPLACE;
        await s3.CopyObjectAsync(trEmpty);

        var want = new Dictionary<string, (Dictionary<string, string> Meta, List<(string, string)> Tags)>
        {
            ["mc-tr.bin"] = (SrcMeta, [("class", "bronze")]),
            ["mr-tc.bin"] = (new() { ["owner"] = "team-b" }, srcTags),
            ["mr-tr.bin"] = (new() { ["owner"] = "team-c" }, [("tier", "cold")]),
            ["default.bin"] = (SrcMeta, srcTags),
            ["tr-empty.bin"] = (SrcMeta, []),
        };
        var observed = new Dictionary<string, (Dictionary<string, string> Meta, List<(string, string)> Tags)>();
        foreach (var key in want.Keys)
        {
            var head = await s3.GetObjectMetadataAsync(bucket, key);
            observed[key] = (S3Fidelity.UserMetadata(head.Metadata), await TagsOf(s3, bucket, key));
            Ensure(
                (head.TagsCount ?? 0) == want[key].Tags.Count,
                $"{key}: HeadObject TagCount {Py.Repr(head.TagsCount)} disagrees with the {want[key].Tags.Count} tags expected");
            var body = await S3Fidelity.GetBytes(s3, bucket, key);
            Ensure(body.AsSpan().SequenceEqual(Body), $"{key}: the copy holds {Py.Repr(body)}, want {Py.Repr(Body)}");
        }

        Ensure(
            want.All(w => Py.Equal(observed[w.Key].Meta, w.Value.Meta) && observed[w.Key].Tags.SequenceEqual(w.Value.Tags)),
            $"(metadata, tags) per destination were {View(observed)}; want {View(want)} — the two directives must act independently");

        // The source is untouched by any of the five copies.
        Ensure((await TagsOf(s3, bucket, "src.bin")).SequenceEqual(srcTags), "copying changed the source's tags");
        Ensure(
            Py.Equal(S3Fidelity.UserMetadata((await s3.GetObjectMetadataAsync(bucket, "src.bin")).Metadata), SrcMeta),
            "copying changed the source's metadata");
    }

    private static async Task<List<(string, string)>> TagsOf(IAmazonS3 s3, string bucket, string key)
    {
        var tagging = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging ?? [];
        return [.. tagging.Select(t => (t.Key, t.Value)).Order()];
    }

    private static string View(Dictionary<string, (Dictionary<string, string> Meta, List<(string, string)> Tags)> perKey) =>
        "{" + string.Join(", ", perKey.Select(kv => $"'{kv.Key}': ({Py.Repr(kv.Value.Meta)}, [{string.Join(", ", kv.Value.Tags.Select(t => $"('{t.Item1}', '{t.Item2}')"))}])")) + "}";
}
