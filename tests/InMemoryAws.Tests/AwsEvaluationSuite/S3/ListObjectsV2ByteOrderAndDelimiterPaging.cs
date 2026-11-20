// Translated from behavioral/s3/check_list_objects_v2_byte_order_and_delimiter_paging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v2-byte-order-and-delimiter-paging</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV2ByteOrderAndDelimiterPaging
{
    /// <summary>
    /// ListObjectsV2 returns keys in ascending UTF-8 byte order, including across pages.
    /// MaxKeys bounds Contents plus CommonPrefixes together, and a continuation token resumes the stream.
    /// </summary>
    [Test]
    [DisplayName("s3.list-objects-v2-byte-order-and-delimiter-paging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // ---------- Part 1: the server's ordering is ascending UTF-8 byte order. ----------
        var orderBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        // Chosen to separate byte order from the two orderings emulators substitute for it:
        // case-insensitive ('A','a','Z','z') and locale collation (which files 'é' next to 'e').
        string[] unordered = ["z.txt", "é.txt", "A.txt", "_.txt", "0.txt", "a.txt", "Z.txt"];
        foreach (var k in unordered)
        {
            await Put(s3, orderBucket, k);
        }

        List<string> expectedOrder = ["0.txt", "A.txt", "Z.txt", "_.txt", "a.txt", "z.txt", "é.txt"];
        // Self-check: the literal above really is UTF-8 byte order, so a failure below is the
        // emulator's, not this check's.
        Ensure(
            expectedOrder.SequenceEqual(expectedOrder.OrderBy(k => Encoding.UTF8.GetBytes(k), ByteOrder.Instance)),
            "the expected_order literal is not UTF-8-byte-sorted");
        Ensure(
            expectedOrder.ToHashSet(StringComparer.Ordinal).SetEquals(unordered),
            $"expected_order and unordered must contain the same keys; got {Py.Repr(expectedOrder)} vs {Py.Repr(unordered.ToList())}");

        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = orderBucket });
        var got = Keys(resp);
        Ensure(
            got.SequenceEqual(expectedOrder),
            $"ListObjectsV2 must return keys in ascending UTF-8 byte order {Py.Repr(expectedOrder)}, got {Py.Repr(got)} "
            + "(insertion order, case-insensitive order, or locale collation all fail here)");
        Ensure(resp.IsTruncated == false, "a 7-key listing under the default MaxKeys is not truncated");
        Ensure(resp.KeyCount == 7, $"expected KeyCount=7, got {resp.KeyCount}");

        // Ordering must survive paging: one key per page, in the same order, with no repeats.
        // (the paginator with PaginationConfig={"PageSize": 1})
        var paged = new List<string>();
        string? pageToken = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = orderBucket, MaxKeys = 1, ContinuationToken = pageToken });
            paged.AddRange(Keys(page));
            pageToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (pageToken is not null);
        Ensure(
            paged.SequenceEqual(expectedOrder),
            "paging one key at a time must reproduce the same byte order exactly once each; expected "
            + $"{Py.Repr(expectedOrder)}, got {Py.Repr(paged)}");

        // ---------- Part 2: MaxKeys is a budget over Contents AND CommonPrefixes. ----------
        var treeBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        foreach (var k in new[] { "root-a.txt", "root-z.txt", "x/1.txt", "x/2.txt", "y/1.txt", "y/2.txt" })
        {
            await Put(s3, treeBucket, k);
        }

        // Ungrouped baseline: 2 root objects + 2 rolled-up prefixes, merged in byte order
        // ('r' < 'x' < 'y'), so the logical stream is root-a.txt, root-z.txt, x/, y/.
        var full = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = treeBucket, Delimiter = "/" });
        Ensure(Keys(full).SequenceEqual(["root-a.txt", "root-z.txt"]), $"expected the two root keys, got {Py.Repr(Keys(full))}");
        Ensure(
            Prefixes(full).SequenceEqual(["x/", "y/"]),
            $"expected CommonPrefixes ['x/', 'y/'] in byte order, got {Py.Repr(Prefixes(full))}");
        Ensure(
            full.KeyCount == 4,
            $"KeyCount must count CommonPrefixes as well as Contents (2 + 2 = 4), got {full.KeyCount}");
        Ensure(
            full.IsTruncated == false,
            $"an ungrouped 4-entry listing under the default MaxKeys must not be truncated, got {full.IsTruncated}");

        // MaxKeys=3 must cut the merged stream after root-a.txt, root-z.txt, x/ — leaving y/ for the
        // next page. An emulator that applies MaxKeys to Contents only returns all of x/ and y/ here.
        var page1 = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = treeBucket, Delimiter = "/", MaxKeys = 3 });
        Ensure(Keys(page1).SequenceEqual(["root-a.txt", "root-z.txt"]), $"page 1 Contents wrong: {Py.Repr(Keys(page1))}");
        Ensure(
            Prefixes(page1).SequenceEqual(["x/"]),
            "with MaxKeys=3 the budget is spent after 2 keys + 1 common prefix, so only 'x/' fits; got "
            + Py.Repr(Prefixes(page1)));
        Ensure(page1.KeyCount == 3, $"expected KeyCount=3, got {page1.KeyCount}");
        Ensure(page1.IsTruncated == true, "MaxKeys=3 over a 4-entry stream must be truncated");
        var token = page1.NextContinuationToken;
        Ensure(!string.IsNullOrEmpty(token), "a truncated page must supply NextContinuationToken");

        // Resuming must yield exactly the remainder — the tail of the merged stream, not a restart.
        var page2 = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = treeBucket, Delimiter = "/", MaxKeys = 3, ContinuationToken = token });
        Ensure(Keys(page2).Count == 0, $"page 2 must contain no objects, got {Py.Repr(Keys(page2))}");
        Ensure(
            Prefixes(page2).SequenceEqual(["y/"]),
            $"resuming must return only the remaining prefix 'y/', got {Py.Repr(Prefixes(page2))}");
        Ensure(page2.IsTruncated == false, "the second page exhausts the stream");
        Ensure(
            page2.ContinuationToken == token,
            $"the response must echo the ContinuationToken it was given, got {Py.Repr(page2.ContinuationToken)}");

        // Whole-stream integrity across the smallest possible pages: every entry appears exactly once,
        // in byte order, with prefixes and objects interleaved correctly.
        var merged = new List<string>();
        string? nextToken = null;
        for (var i = 0; i < 10; i++)
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = treeBucket, Delimiter = "/", MaxKeys = 1, ContinuationToken = nextToken });
            merged.AddRange(Keys(page));
            merged.AddRange(Prefixes(page));
            if (page.IsTruncated != true)
            {
                break;
            }

            nextToken = page.NextContinuationToken;
            Ensure(!string.IsNullOrEmpty(nextToken), "a truncated MaxKeys=1 page must supply NextContinuationToken");
        }

        Ensure(
            merged.SequenceEqual(["root-a.txt", "root-z.txt", "x/", "y/"]),
            "paging one entry at a time must reproduce the merged byte-ordered stream exactly once "
            + $"each; got {Py.Repr(merged)}");
    }

    private static Task Put(Amazon.S3.IAmazonS3 s3, string bucket, string key) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Encoding.UTF8.GetBytes("x")) });

    private static List<string> Keys(ListObjectsV2Response response) => [.. (response.S3Objects ?? []).Select(o => o.Key)];

    private static List<string> Prefixes(ListObjectsV2Response response) => response.CommonPrefixes ?? [];

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
