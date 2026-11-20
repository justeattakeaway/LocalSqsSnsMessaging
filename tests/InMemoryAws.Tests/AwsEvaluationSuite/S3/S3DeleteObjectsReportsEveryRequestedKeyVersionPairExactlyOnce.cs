// Translated from behavioral/s3/check_s3_delete_objects_reports_every_requested_key_version_pair_exactly_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-delete-objects-reports-every-requested-key-version-pair-exactly-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3DeleteObjectsReportsEveryRequestedKeyVersionPairExactlyOnce
{
    private const string BogusVersion = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly byte[] V1 = Encoding.ASCII.GetBytes("v1-bytes");
    private static readonly byte[] V2 = Encoding.ASCII.GetBytes("v2-bytes-and-this-version-must-survive-the-batch");

    /// <summary>
    /// DeleteObjects reports each requested (Key, VersionId) pair exactly once, with its id echoed.
    /// A live version lands in Deleted[], a non-existent one in Errors[] as NoSuchVersion; only the
    /// named version is removed, no delete marker appears, and the current bytes are unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-delete-objects-reports-every-requested-key-version-pair-exactly-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var status = await S3Fidelity.VersioningStatus(s3, bucket);
        Ensure(status == "Enabled", $"versioning did not take effect: Status is {Py.Repr(status)}");

        const string key = "batch/versioned.bin";
        var first = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V1) });
        var doomedVersion = first.VersionId;
        Ensure(
            !string.IsNullOrEmpty(doomedVersion),
            $"a versioning-enabled bucket must return a VersionId from PutObject, got {Py.Repr(doomedVersion)} -- without one this check cannot name a live version to delete");
        var second = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V2) });
        var survivingVersion = second.VersionId;
        Ensure(
            !string.IsNullOrEmpty(survivingVersion) && survivingVersion != doomedVersion,
            $"the overwrite must produce a distinct VersionId, got {Py.Repr(survivingVersion)} against {Py.Repr(doomedVersion)}");

        var baseline = Sorted(await VersionsForKey(s3, bucket, key));
        var expectedBaseline = Sorted([doomedVersion, survivingVersion]);
        Ensure(
            baseline.SequenceEqual(expectedBaseline),
            $"before the batch the key must hold exactly the two versions just written {Py.Repr(expectedBaseline)}, ListObjectVersions reports {Py.Repr(baseline)} -- unless the doomed version is really there first, a later 'it is gone' assertion passes trivially against a store that never kept it");

        List<KeyVersion> requested = [new() { Key = key, VersionId = doomedVersion }, new() { Key = key, VersionId = BogusVersion }];
        // The .NET SDK raises DeleteObjectsException for a response with Errors; botocore returns it.
        DeleteObjectsResponse resp;
        try
        {
            resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [.. requested] });
        }
        catch (DeleteObjectsException ex)
        {
            resp = ex.Response;
        }

        var deleted = (resp.DeletedObjects ?? []).Select(d => (d.Key, d.VersionId)).ToList();
        var errored = (resp.DeleteErrors ?? []).Select(e => (e.Key, e.VersionId)).ToList();
        var reported = deleted.Concat(errored).ToList();

        Ensure(
            reported.Count == requested.Count,
            $"DeleteObjects must report every requested entry: asked for {requested.Count} entries, got {reported.Count} (Deleted={Show(deleted)}, Errors={Show(errored)})");

        foreach (var entry in requested)
        {
            var pair = (entry.Key, entry.VersionId);
            var occurrences = reported.Count(p => p == pair);
            Ensure(
                occurrences == 1,
                $"requested entry {pair} must be reported exactly once with its version id echoed back, it appears {occurrences} time(s) (Deleted={Show(deleted)}, Errors={Show(errored)})");
        }

        var bothSides = deleted.Intersect(errored).ToList();
        Ensure(bothSides.Count == 0, $"Deleted[] and Errors[] must be disjoint, these pairs appear on both sides: {Show(bothSides)}");

        var doomedPair = (key, doomedVersion);
        var bogusPair = (key, BogusVersion);
        Ensure(
            deleted.Count(p => p == doomedPair) == 1,
            $"the live version named in the batch must be reported on the SUCCESS side exactly once: {doomedPair} appears {deleted.Count(p => p == doomedPair)} time(s) in Deleted={Show(deleted)} (Errors={Show(errored)})");
        Ensure(
            errored.Count(p => p == bogusPair) == 1,
            $"the non-existent version named in the batch must be reported on the ERROR side exactly once rather than laundered into a success: {bogusPair} appears {errored.Count(p => p == bogusPair)} time(s) in Errors={Show(errored)} (Deleted={Show(deleted)})");

        var bogusError = resp.DeleteErrors!.First(e => (e.Key, e.VersionId) == bogusPair);
        Ensure(
            bogusError.Code == "NoSuchVersion",
            $"expected Errors[] Code 'NoSuchVersion' for the non-existent version {bogusPair}, got {Py.Repr(bogusError.Code)}");
        Ensure(
            bogusError.Message == "The specified version does not exist.",
            $"expected Errors[] Message 'The specified version does not exist.' for the non-existent version {bogusPair}, got {Py.Repr(bogusError.Message)}");

        var remaining = Sorted(await VersionsForKey(s3, bucket, key));
        Ensure(
            remaining.SequenceEqual([survivingVersion]),
            $"after the batch the key must hold exactly the one surviving version [{Py.Repr(survivingVersion)}] (deleted: {Py.Repr(doomedVersion)}), ListObjectVersions reports {Py.Repr(remaining)} -- a version-pinned delete must remove only the version it names, without inventing a delete marker or stranding an extra version");
        var currentBody = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            currentBody.AsSpan().SequenceEqual(V2),
            $"the current version's bytes changed during the batch delete: got {Py.Repr(Encoding.ASCII.GetString(currentBody))}, want {Py.Repr(Encoding.ASCII.GetString(V2))}");
    }

    /// <summary><c>_versions_for_key</c>: every version id currently listed for one key (versions + delete markers).</summary>
    private static async Task<List<string>> VersionsForKey(IAmazonS3 s3, string bucket, string key)
    {
        var found = new List<string>();
        string? keyMarker = null;
        string? versionMarker = null;
        while (true)
        {
            var page = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key, KeyMarker = keyMarker, VersionIdMarker = versionMarker });
            found.AddRange((page.Versions ?? []).Where(o => o.Key == key).Select(o => o.VersionId));
            if (page.IsTruncated != true)
            {
                return found;
            }

            (keyMarker, versionMarker) = (page.NextKeyMarker, page.NextVersionIdMarker);
        }
    }

    private static List<string> Sorted(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];

    private static string Show(IEnumerable<(string, string)> pairs) => "[" + string.Join(", ", pairs) + "]";
}
