// Translated from behavioral/s3/check_list_object_versions_pages_one_key_written_more_than_one_thousand_times.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-object-versions-pages-one-key-written-more-than-one-thousand-times</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectVersionsPagesOneKeyWrittenMoreThanOneThousandTimes
{
    private const int Writes = 1001;
    private const int Page = 1000;
    private const string Key = "hot/key.txt";

    /// <summary>
    /// One key written 1,001 times keeps 1,001 versions, listed newest first in pages of 1,000.
    /// The truncated first page's version markers return the oldest version on the second page.
    /// </summary>
    [Test]
    [DisplayName("s3.list-object-versions-pages-one-key-written-more-than-one-thousand-times")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await Bucket(ctx);
        var written = new List<string>();
        for (var i = 0; i < Writes; i++)
        {
            written.Add((await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = Key,
                InputStream = new MemoryStream(Encoding.UTF8.GetBytes($"v{i}")),
            })).VersionId);
        }

        var distinct = written.ToHashSet(StringComparer.Ordinal).Count;
        Ensure(distinct == Writes, $"{Writes} writes must return distinct VersionIds; got {distinct}");
        var newestFirst = Enumerable.Reverse(written).ToList();

        var first = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
        var firstIds = (first.Versions ?? []).Select(v => v.VersionId).ToList();
        Ensure(first.MaxKeys == Page, $"the default MaxKeys must be {Page}; got {first.MaxKeys}");
        Ensure(
            firstIds.SequenceEqual(newestFirst.Take(Page)),
            $"the first page must hold the newest {Page} versions, newest first; got {firstIds.Count} versions");
        Ensure(
            (first.Versions ?? []).Select(v => v.IsLatest == true).SequenceEqual([true, .. Enumerable.Repeat(false, Page - 1)]),
            "only the newest version may be IsLatest");
        Ensure(first.IsTruncated == true, $"the first page must be truncated; got {first.IsTruncated}");
        Ensure(
            (first.NextKeyMarker, first.NextVersionIdMarker) == (Key, newestFirst[Page - 1]),
            $"the markers must name the key and the last version listed; got ({Py.Repr(first.NextKeyMarker)}, {Py.Repr(first.NextVersionIdMarker)})");

        var second = await s3.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = bucket,
            KeyMarker = first.NextKeyMarker,
            VersionIdMarker = first.NextVersionIdMarker,
        });
        var secondIds = (second.Versions ?? []).Select(v => v.VersionId).ToList();
        Ensure(
            secondIds.SequenceEqual(newestFirst.Skip(Page)),
            $"the second page must hold only the oldest version {Py.Repr(newestFirst.Skip(Page).ToList())}; got {Py.Repr(secondIds)}");
        Ensure(second.IsTruncated == false, $"the last page must not be truncated; got {second.IsTruncated}");

        using var oldestResponse = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = Key, VersionId = written[0] });
        var oldest = Encoding.UTF8.GetString(await S3Fidelity.ReadBody(oldestResponse));
        Ensure(oldest == "v0", $"the oldest version must still read its own body; got {Py.Repr(oldest)}");
    }

    /// <summary>A versioned bucket whose purge-and-delete net is registered before the create.</summary>
    private static async Task<string> Bucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = FidelityContext.UniqueName("sim2real-hotkey-");
        ctx.OnCleanup(() => DropBucket(s3, name));
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            // us-east-1 is S3's default region: CreateBucket rejects an explicit LocationConstraint
            // there, so it must be omitted rather than passed.
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        return name;
    }

    /// <summary>The cleanup: delete every version and marker, then the bucket; a missing bucket is fine.</summary>
    private static async Task DropBucket(IAmazonS3 s3, string bucket)
    {
        try
        {
            string? keyMarker = null;
            string? versionMarker = null;
            var objects = new List<KeyVersion>();
            while (true)
            {
                var page = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, KeyMarker = keyMarker, VersionIdMarker = versionMarker });
                objects.AddRange((page.Versions ?? []).Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId }));
                if (page.IsTruncated != true)
                {
                    break;
                }

                (keyMarker, versionMarker) = (page.NextKeyMarker, page.NextVersionIdMarker);
            }

            foreach (var chunk in objects.Chunk(1000))
            {
                var output = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [.. chunk], Quiet = true });
                Ensure((output.DeleteErrors ?? []).Count == 0, "cleanup could not delete every version");
            }

            await s3.DeleteBucketAsync(bucket);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
        }
    }
}
