// Translated from behavioral/s3/check_list_object_annotations_max_annotation_results_pagination_returns_each_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-object-annotations-max-annotation-results-pagination-returns-each-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectAnnotationsMaxAnnotationResultsPaginationReturnsEachOnce
{
    private const string Check = "list-object-annotations-max-annotation-results-pagination-returns-each-once";
    private const int NAnnotations = 5;
    private const int MaxResults = 2;

    /// <summary>
    /// ListObjectAnnotations with MaxAnnotationResults=2 over 5 annotations pages with a
    /// NextContinuationToken; following ContinuationToken returns every annotation name, size and
    /// ETag exactly once, and the final page carries no NextContinuationToken.
    /// </summary>
    [Test]
    [DisplayName("s3.list-object-annotations-max-annotation-results-pagination-returns-each-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-loap-");
        const string key = "annotated-object.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });

        var written = new Dictionary<string, (long Size, string ETag)>(StringComparer.Ordinal);
        for (var i = 0; i < NAnnotations; i++)
        {
            var name = $"pagination-annotation-{i}";
            var payload = System.Text.Encoding.UTF8.GetBytes($"value-{i}");
            var put = await S3Fidelity.PutAnnotation(s3, bucket, key, name, payload);
            written[name] = (payload.Length, put.ETag);
        }

        var seen = new Dictionary<string, (long Size, string ETag)>(StringComparer.Ordinal);
        string? token = null;
        var pages = 0;
        while (true)
        {
            var resp = await s3.ListObjectAnnotationsAsync(new ListObjectAnnotationsRequest
            {
                BucketName = bucket,
                Key = key,
                MaxAnnotationResults = MaxResults,
                ContinuationToken = token,
            });
            pages++;
            Ensure(pages <= NAnnotations, $"pagination did not converge after {pages} pages: seen {Py.Repr(seen.Keys.Order(StringComparer.Ordinal).ToList())}");
            var pageAnnotations = resp.Annotations ?? [];
            Ensure(
                pageAnnotations.Count <= MaxResults,
                $"page returned {pageAnnotations.Count} annotation(s), more than MaxAnnotationResults={MaxResults}: {Py.Repr(pageAnnotations.Select(a => a.AnnotationName).ToList())}");
            foreach (var a in pageAnnotations)
            {
                Ensure(!seen.ContainsKey(a.AnnotationName), $"annotation {Py.Repr(a.AnnotationName)} was returned on more than one page");
                seen[a.AnnotationName] = (a.Size ?? 0, a.ETag);
            }

            var nextToken = resp.NextContinuationToken;
            if (nextToken is null)
            {
                break;
            }

            Ensure(nextToken != token, $"NextContinuationToken did not advance past {Py.Repr(token)}");
            token = nextToken;
        }

        Ensure(
            seen.Keys.ToHashSet().SetEquals(written.Keys),
            $"paginating ListObjectAnnotations with MaxAnnotationResults={MaxResults} must return every one of {Py.Repr(written.Keys.Order(StringComparer.Ordinal).ToList())} exactly once; got {Py.Repr(seen.Keys.Order(StringComparer.Ordinal).ToList())}");
        foreach (var (name, expect) in written)
        {
            Ensure(
                seen[name] == expect,
                $"annotation {Py.Repr(name)}'s paginated {{Size, ETag}} {seen[name]} != PutObjectAnnotation's own {expect}");
        }
    }
}
