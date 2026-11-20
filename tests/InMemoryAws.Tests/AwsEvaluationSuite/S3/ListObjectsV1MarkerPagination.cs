// Translated from behavioral/s3/check_list_objects_v1_marker_pagination.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v1-marker-pagination</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV1MarkerPagination
{
    /// <summary>ListObjects (v1) paginates by Marker=last-returned-key and reports IsTruncated correctly.</summary>
    [Test]
    [DisplayName("s3.list-objects-v1-marker-pagination")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = "true" }] });
        List<string> keys = [.. Enumerable.Range(0, 5).Select(i => $"v1/{i:00}.txt")];
        foreach (var k in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, InputStream = new MemoryStream(Encoding.UTF8.GetBytes("x")) });
        }

        var seen = new List<string>();
        var marker = "";
        var pages = 0;
        while (true)
        {
            var page = await s3.ListObjectsAsync(new ListObjectsRequest { BucketName = bucket, Prefix = "v1/", MaxKeys = 2, Marker = marker });
            pages++;
            var batch = (page.S3Objects ?? []).Select(o => o.Key).ToList();
            seen.AddRange(batch);
            Ensure(batch.Count <= 2, $"MaxKeys=2 violated, got {batch.Count} keys");
            if (page.IsTruncated != true)
            {
                break;
            }

            marker = batch[^1];
            Ensure(pages < 10, "pagination did not terminate");
        }

        Ensure(seen.SequenceEqual(keys), $"expected {Py.Repr(keys)} in lexicographic order, got {Py.Repr(seen)}");
        Ensure(pages == 3, $"expected 3 pages for 5 keys at MaxKeys=2, got {pages}");
    }
}
