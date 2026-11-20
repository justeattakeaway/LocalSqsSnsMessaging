// Translated from behavioral/s3/check_s3_delete_object_tagging_clears_only_the_addressed_version_tag_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-delete-object-tagging-clears-only-the-addressed-version-tag-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3DeleteObjectTaggingClearsOnlyTheAddressedVersionTagSet
{
    private static readonly byte[] BodyV1 = Times("first-generation-payload-that-a-tag-delete-must-not-touch");
    private static readonly byte[] BodyV2 = Times("second-generation-payload-that-a-tag-delete-must-not-touch");
    private static readonly byte[] BodyV3 = Times("third-generation-payload-destined-to-be-permanently-deleted");

    /// <summary>
    /// DeleteObjectTagging clears only the addressed version's tag set; the object is unchanged.
    /// A pinned or unversioned (current-version) delete leaves sibling versions' tags intact, and a
    /// delete aimed at a permanently deleted version is refused 404 NoSuchVersion.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-delete-object-tagging-clears-only-the-addressed-version-tag-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var status = await S3Fidelity.VersioningStatus(s3, bucket);
        Ensure(status == "Enabled", $"versioning did not take effect: Status is {Py.Repr(status)}");

        const string key = "per-version-tag-set.bin";

        // --- Three generations of one key, each carrying its own one-tag tag set. Setup deliberately
        // uses only the UNVERSIONED PutObjectTagging, which lands on whichever version is current at
        // that moment — i.e. the one just written. Every version-scoping claim this check makes is
        // therefore carried by a graded assertion below rather than smuggled into the setup.
        var puts = new List<PutObjectResponse>();
        foreach (var (body, value) in new[] { (BodyV1, "first"), (BodyV2, "second"), (BodyV3, "third") })
        {
            puts.Add(await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) }));
            await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = bucket,
                Key = key,
                Tagging = new Tagging { TagSet = [new Tag { Key = "generation", Value = value }] },
            });
        }

        var (put1, put2, put3) = (puts[0], puts[1], puts[2]);
        var (v1, v2, v3) = (put1.VersionId, put2.VersionId, put3.VersionId);
        Ensure(
            new HashSet<string>([v1, v2, v3], StringComparer.Ordinal).Count == 3,
            $"three PUTs into a versioned bucket must mint three distinct version ids, got {Py.Repr(new List<string> { v1, v2, v3 })}");
        foreach (var (label, vid) in new[] { ("v1", v1), ("v2", v2), ("v3", v3) })
        {
            Ensure(!string.IsNullOrEmpty(vid) && vid != "null", $"{label} came back with VersionId {Py.Repr(vid)}; a PUT into a versioned bucket must mint a real version id");
        }

        var (etag1, etag2) = (put1.ETag, put2.ETag);

        // --- Baseline: each version answers with its own tag set, and the unversioned read resolves
        // to the newest version. If this disagrees the rest of the check is meaningless.
        foreach (var (vid, value) in new[] { (v1, "first"), (v2, "second"), (v3, "third") })
        {
            var got = await Tags(s3, bucket, key, vid);
            Ensure(
                S3Fidelity.TagsAre(got.Tagging, ("generation", value)),
                $"baseline tag set for version {Py.Repr(vid)} is {S3Fidelity.TagRepr(got.Tagging)}, want the single tag generation={Py.Repr(value)} — the three versions' tag sets are not independent");
            Ensure(got.VersionId == vid, $"GetObjectTagging pinned to {Py.Repr(vid)} echoed VersionId {Py.Repr(got.VersionId)}");
        }

        var latestTags = await Tags(s3, bucket, key);
        Ensure(latestTags.VersionId == v3, $"the unversioned GetObjectTagging resolved to {Py.Repr(latestTags.VersionId)}, want the current version {Py.Repr(v3)}");
        Ensure(
            S3Fidelity.TagsAre(latestTags.Tagging, ("generation", "third")),
            $"the unversioned read returned {S3Fidelity.TagRepr(latestTags.Tagging)}, want the current version's tag generation='third'");

        // --- Negative leg: permanently remove v3, then aim a tag delete at its now-dead version id.
        // A VersionId is an address, not a hint, so S3 refuses this rather than resolving it to the
        // current version (which would clear v2's tags).
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v3 });
        var refused = await S3Fidelity.ExpectError(
            () => s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key, VersionId = v3 }),
            $"expected DeleteObjectTagging aimed at the permanently-deleted version {Py.Repr(v3)} to be refused with a ClientError, but it succeeded");
        // The S3 Smithy model declares no `errors` array for DeleteObjectTagging; the code, message and
        // status below are the ones real AWS was observed to return for this exact request.
        Ensure(
            refused.Code == "NoSuchVersion",
            $"expected error code 'NoSuchVersion' for a tag delete on the permanently-deleted version {Py.Repr(v3)}, got {Py.Repr(refused.Code)}");
        Ensure(
            refused.Message == "The specified version does not exist.",
            $"expected error message 'The specified version does not exist.' for a tag delete on the permanently-deleted version {Py.Repr(v3)}, got {Py.Repr(refused.Message)}");
        Ensure(
            (int)refused.StatusCode == 404,
            $"expected HTTP 404 for NoSuchVersion on the permanently-deleted version {Py.Repr(v3)}, got {(int)refused.StatusCode}");
        foreach (var (vid, value) in new[] { (v1, "first"), (v2, "second") })
        {
            var survived = await Tags(s3, bucket, key, vid);
            Ensure(
                S3Fidelity.TagsAre(survived.Tagging, ("generation", value)),
                $"the refused tag delete disturbed version {Py.Repr(vid)}: its tag set is now {S3Fidelity.TagRepr(survived.Tagging)}, want generation={Py.Repr(value)}");
        }

        // --- Positive leg A: clear the tag set of the NON-current version. The transition is v1's
        // tag set emptying; the invariant is v2's tag set NOT emptying.
        var clearedV1 = await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key, VersionId = v1 });
        Ensure(
            clearedV1.VersionId == v1,
            $"the tag delete pinned to {Py.Repr(v1)} echoed VersionId {Py.Repr(clearedV1.VersionId)}; the response must name the version it actually operated on");
        var afterV1 = await Tags(s3, bucket, key, v1);
        Ensure(S3Fidelity.TagsAre(afterV1.Tagging), $"version {Py.Repr(v1)} still reports tags after its tag set was deleted: {S3Fidelity.TagRepr(afterV1.Tagging)}");
        var sibling = await Tags(s3, bucket, key, v2);
        Ensure(
            S3Fidelity.TagsAre(sibling.Tagging, ("generation", "second")),
            $"deleting version {Py.Repr(v1)}'s tag set also cleared sibling version {Py.Repr(v2)}, whose tag set is now {S3Fidelity.TagRepr(sibling.Tagging)} instead of generation='second' — tag sets are being stored per key rather than per version");
        var unversionedAfterV1 = await Tags(s3, bucket, key);
        Ensure(
            unversionedAfterV1.VersionId == v2,
            $"the unversioned read now resolves to {Py.Repr(unversionedAfterV1.VersionId)}, want the current version {Py.Repr(v2)}");
        Ensure(
            S3Fidelity.TagsAre(unversionedAfterV1.Tagging, ("generation", "second")),
            $"the unversioned read returned {S3Fidelity.TagRepr(unversionedAfterV1.Tagging)}; deleting an older version's tag set must not change what the current version reports");

        // --- Re-tag the already-cleared non-current version. Without this the unversioned leg below
        // would run with every surviving sibling already empty, and a key-wide tag delete would
        // satisfy it by accident. A distinct value also proves a version whose tag set was deleted
        // can be re-tagged, version-scoped.
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = v1,
            Tagging = new Tagging { TagSet = [new Tag { Key = "generation", Value = "requeued" }] },
        });
        var requeued = await Tags(s3, bucket, key, v1);
        Ensure(
            S3Fidelity.TagsAre(requeued.Tagging, ("generation", "requeued")),
            $"version {Py.Repr(v1)} did not take a fresh tag set after its tags were deleted: {S3Fidelity.TagRepr(requeued.Tagging)}");
        var stillSecond = await Tags(s3, bucket, key, v2);
        Ensure(
            S3Fidelity.TagsAre(stillSecond.Tagging, ("generation", "second")),
            $"re-tagging version {Py.Repr(v1)} changed sibling version {Py.Repr(v2)}, whose tag set is now {S3Fidelity.TagRepr(stillSecond.Tagging)} instead of generation='second'");

        // --- Positive leg B: the same call with NO VersionId must address the CURRENT version and
        // say which one that was, rather than defaulting to the oldest or to all of them.
        var clearedCurrent = await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
        Ensure(
            clearedCurrent.VersionId == v2,
            $"the unversioned tag delete echoed VersionId {Py.Repr(clearedCurrent.VersionId)}, want the current version {Py.Repr(v2)}");
        var afterV2 = await Tags(s3, bucket, key, v2);
        Ensure(S3Fidelity.TagsAre(afterV2.Tagging), $"version {Py.Repr(v2)} still reports tags after the unversioned tag delete: {S3Fidelity.TagRepr(afterV2.Tagging)}");
        var siblingAfterCurrent = await Tags(s3, bucket, key, v1);
        Ensure(
            S3Fidelity.TagsAre(siblingAfterCurrent.Tagging, ("generation", "requeued")),
            $"the unversioned tag delete also cleared non-current version {Py.Repr(v1)}, whose tag set is now {S3Fidelity.TagRepr(siblingAfterCurrent.Tagging)} instead of generation='requeued' — the unversioned form is deleting tags key-wide instead of addressing the current version");

        // --- Invariants: a tag delete touches a sub-resource only. No ETag moved, no payload byte
        // changed, and the version list is exactly the two survivors with no marker invented.
        foreach (var (vid, etag, body) in new[] { (v1, etag1, BodyV1), (v2, etag2, BodyV2) })
        {
            var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, VersionId = vid });
            Ensure(
                head.ETag == etag,
                $"version {Py.Repr(vid)} reports ETag {Py.Repr(head.ETag)} after its tags were deleted, want the original {Py.Repr(etag)} — a tag delete must not rewrite the object");
            Ensure(head.ContentLength == body.Length, $"version {Py.Repr(vid)} reports ContentLength {head.ContentLength}, want {body.Length}");
            using var fetched = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = vid });
            var payload = await S3Fidelity.ReadBody(fetched);
            Ensure(
                payload.AsSpan().SequenceEqual(body),
                $"version {Py.Repr(vid)} no longer serves its payload byte-for-byte after its tag set was deleted: got {payload.Length} bytes, want {body.Length}");
        }

        var (versions, markers) = await S3Fidelity.AllVersions(s3, bucket);
        var listedIds = versions.Select(v => v.VersionId).Order(StringComparer.Ordinal).ToList();
        var survivors = new[] { v1, v2 }.Order(StringComparer.Ordinal).ToList();
        Ensure(
            listedIds.SequenceEqual(survivors),
            $"ListObjectVersions reports {Py.Repr(listedIds)}, want exactly the two survivors {Py.Repr(survivors)} — a tag delete must neither mint nor remove a version");
        Ensure(markers.Count == 0, $"a tag delete invented delete markers: {Py.Repr(markers.Select(m => m.VersionId).ToList())}");
    }

    private static byte[] Times(string text) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, 4)));

    private static Task<GetObjectTaggingResponse> Tags(IAmazonS3 s3, string bucket, string key, string? versionId = null) =>
        s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key, VersionId = versionId });
}
