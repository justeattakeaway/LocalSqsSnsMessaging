// Translated from behavioral/s3/check_list_objects_v2_is_exact_on_the_first_read_after_a_bulk_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v2-is-exact-on-the-first-read-after-a-bulk-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV2IsExactOnTheFirstReadAfterABulkDelete
{
    private const string Prefix = "batch/";
    private const int Total = 120;
    private const int Page = 25;
    private const int Threads = 12;

    /// <summary>
    /// The first paginated ListObjectsV2 after bulk writes and a bulk delete is exact, with no retry.
    /// Keys stay in UTF-8 byte order across pages, tokens replay, and repeated listings are identical.
    /// </summary>
    [Test]
    [DisplayName("s3.list-objects-v2-is-exact-on-the-first-read-after-a-bulk-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        List<string> written = [.. Enumerable.Range(0, Total).Select(i => $"{Prefix}{i:0000}.txt")];
        using (ctx.Concurrently())
        {
            await Parallel.ForEachAsync(
                written,
                new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = cancellationToken },
                async (key, _) => await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = new MemoryStream(Encoding.UTF8.GetBytes(key)),
                }));
        }

        // No retry: S3 LIST is strongly consistent, and a poll would hide an asynchronous listing index.
        var (listed, pages) = await ListAll(s3, bucket);
        Ensure(
            listed.SequenceEqual(written),
            $"the first paginated listing after {Total} writes returned {listed.Count} keys; expected "
            + $"{Total}. Missing {Py.Repr(written.Except(listed).Order(StringComparer.Ordinal).Take(5).ToList())}, unexpected "
            + Py.Repr(listed.Except(written).Order(StringComparer.Ordinal).Take(5).ToList()));
        Ensure(
            listed.SequenceEqual(listed.Order(StringComparer.Ordinal)),
            $"ListObjectsV2 did not return keys in ascending UTF-8 byte order; the first disorder is at index {FirstDisorder(listed)}");
        var expectedPages = (Total + Page - 1) / Page;
        Ensure(pages.Count == expectedPages, $"MaxKeys={Page} over {Total} keys produced {pages.Count} pages, expected {expectedPages}");
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var keys = (page.S3Objects ?? []).Select(o => o.Key).ToList();
            Ensure(page.KeyCount == keys.Count, $"page {index} reports KeyCount {page.KeyCount} but carries {keys.Count} keys");
            var last = index == pages.Count - 1;
            Ensure(
                page.IsTruncated == !last,
                $"page {index} of {pages.Count} reports IsTruncated {page.IsTruncated}; only the last page may be untruncated");
            Ensure(
                (page.NextContinuationToken is not null) == !last,
                $"page {index} of {pages.Count} carries NextContinuationToken "
                + $"{Py.Repr(page.NextContinuationToken)}; a token must be present exactly when the page is truncated");
        }

        // --- A saved token reproduces its page exactly.
        var token = pages[0].NextContinuationToken;
        var replayed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = Prefix, MaxKeys = Page, ContinuationToken = token });
        Ensure(
            (replayed.S3Objects ?? []).Select(o => o.Key).SequenceEqual((pages[1].S3Objects ?? []).Select(o => o.Key)),
            "re-issuing the continuation token from page 0 returned a different page 1 than the original pagination did");

        // --- Bulk delete every even-numbered key, then read ONCE.
        var doomed = written.Where((_, i) => i % 2 == 0).ToList();
        var survivors = written.Where((_, i) => i % 2 == 1).ToList();
        var deleted = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [.. doomed.Select(k => new KeyVersion { Key = k })],
            Quiet = false,
        });
        var deletedKeys = (deleted.DeletedObjects ?? []).Select(d => d.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            deletedKeys.SequenceEqual(doomed.Order(StringComparer.Ordinal)),
            $"DeleteObjects reported {deletedKeys.Count} deletions and {(deleted.DeleteErrors ?? []).Count} errors; expected "
            + $"{doomed.Count} deletions and no errors");
        var (after, _) = await ListAll(s3, bucket);
        Ensure(
            after.SequenceEqual(survivors),
            $"the FIRST paginated listing after deleting {doomed.Count} keys returned {after.Count} keys; "
            + $"expected exactly the {survivors.Count} survivors. Still listed but deleted: "
            + $"{Py.Repr(after.Intersect(doomed).Order(StringComparer.Ordinal).Take(5).ToList())}; missing survivors: "
            + $"{Py.Repr(survivors.Except(after).Order(StringComparer.Ordinal).Take(5).ToList())} — the listing index is not strongly consistent "
            + "with the delete");
        Ensure(
            after.SequenceEqual(after.Order(StringComparer.Ordinal)),
            $"the post-delete listing is not in ascending UTF-8 byte order at index {FirstDisorder(after)}");

        // --- Freeze detector: reading does not advance state.
        var repeats = new List<List<string>>();
        for (var i = 0; i < 3; i++)
        {
            repeats.Add((await ListAll(s3, bucket)).Keys);
        }

        Ensure(
            repeats.All(r => r.SequenceEqual(survivors)),
            "three consecutive full paginations disagreed: lengths "
            + $"{Py.Repr(repeats.Select(r => r.Count).ToList())} — a listing must not be consumed by being read");

        // --- Terminal reads through a different operation, at the destination.
        Ensure(
            (await s3.GetObjectMetadataAsync(bucket, survivors[0])).ContentLength == Encoding.UTF8.GetByteCount(survivors[0]),
            $"HeadObject on the surviving key {Py.Repr(survivors[0])} reports the wrong size");
        var err = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, doomed[0]))
            ?? throw Fail("the call was expected to fail and it succeeded");
        // botocore's code for a code-less HEAD refusal is its HTTP status
        Ensure(S3CoreFidelity.IsBareHead404(err), $"HeadObject still finds the deleted key {Py.Repr(doomed[0])}");
    }

    /// <summary>
    /// Page through ListObjectsV2 with MaxKeys=_PAGE. Returns (keys, raw pages).
    /// Deliberately NOT a poll: the caller asserts the FIRST result. The page budget is a hard bound
    /// so a target that never stops truncating fails loudly instead of looping.
    /// </summary>
    private static async Task<(List<string> Keys, List<ListObjectsV2Response> Pages)> ListAll(IAmazonS3 s3, string bucket)
    {
        var keys = new List<string>();
        var pages = new List<ListObjectsV2Response>();
        string? token = null;
        var budget = (4 * ((Total + Page - 1) / Page)) + 4;
        while (true)
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = Prefix, MaxKeys = Page, ContinuationToken = token });
            pages.Add(page);
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            Ensure(
                pages.Count <= budget,
                $"ListObjectsV2 produced more than {budget} pages of at most {Page} keys for a bucket "
                + $"holding at most {Total} keys; it is not terminating");
            if (page.IsTruncated != true)
            {
                return (keys, pages);
            }

            token = page.NextContinuationToken;
        }
    }

    /// <summary>The index of the first key that is smaller than its predecessor, or -1.</summary>
    private static int FirstDisorder(List<string> keys)
    {
        for (var i = 1; i < keys.Count; i++)
        {
            if (string.CompareOrdinal(keys[i], keys[i - 1]) < 0)
            {
                return i;
            }
        }

        return -1;
    }
}
