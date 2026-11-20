// Translated from behavioral/s3/check_s3_refused_conditional_gets_create_no_version_and_leave_the_object_intact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-refused-conditional-gets-create-no-version-and-leave-the-object-intact</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3RefusedConditionalGetsCreateNoVersionAndLeaveTheObjectIntact
{
    private const string StaleEtag = "\"00000000000000000000000000000000\"";
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("conditional-read-payload-that-must-survive-two-refusals-byte-for-byte");

    /// <summary>
    /// A refused conditional GET leaves a versioned object's version history unchanged.
    /// After a 304 (If-None-Match on the current ETag) and a 412 (stale If-Match), ListObjectVersions
    /// lists the same single version, and If-Match on the current ETag still serves the exact bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-refused-conditional-gets-create-no-version-and-leave-the-object-intact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var versioning = await S3Fidelity.VersioningStatus(s3, bucket);
        Ensure(
            versioning == "Enabled",
            $"the bucket must report versioning Enabled before a refused read can be graded for minting no version, GetBucketVersioning reports {Py.Repr(versioning)}");

        const string key = "conditional-read/payload.bin";
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Body) });
        var liveEtag = put.ETag;
        var liveVersion = put.VersionId;
        Ensure(
            !string.IsNullOrEmpty(liveVersion) && liveVersion != "null",
            $"a versioning-enabled bucket must return a real VersionId from PutObject, got {Py.Repr(liveVersion)} -- the 'null' sentinel is what an unversioned bucket reports, and against it the before/after version lists would match no matter what a read did");

        var versionsBefore = await VersionsForKey(s3, bucket, key);
        Ensure(
            versionsBefore.SequenceEqual([liveVersion]),
            $"expected exactly the one freshly written version {Py.Repr(liveVersion)} before any conditional read, ListObjectVersions reports {Py.Repr(versionsBefore)}");

        // --- Leg 1: If-None-Match carrying the CURRENT ETag must be answered 304.
        var notModified = await S3Fidelity.ExpectError(
            () => Get(s3, bucket, key, ifNoneMatch: liveEtag),
            $"expected GetObject with IfNoneMatch set to the object's own current ETag {Py.Repr(liveEtag)} to be refused with a ClientError, but it succeeded");
        var nmStatus = (int)notModified.StatusCode;
        Ensure(nmStatus == 304, $"expected HTTP 304 Not Modified for an If-None-Match matching the current ETag, got {nmStatus}");

        // --- Leg 2: a stale If-Match must be refused with 412 PreconditionFailed.
        Ensure(
            StaleEtag != liveEtag,
            $"the stale ETag fixture {Py.Repr(StaleEtag)} collided with the live ETag {Py.Repr(liveEtag)}, so this leg cannot distinguish a stale precondition from a current one");
        var precondition = await S3Fidelity.ExpectError(
            () => Get(s3, bucket, key, ifMatch: StaleEtag),
            $"expected GetObject with the stale IfMatch {Py.Repr(StaleEtag)} on an object whose current ETag is {Py.Repr(liveEtag)} to be refused with a ClientError, but it succeeded");
        Ensure(
            precondition.Code == "PreconditionFailed",
            $"expected error code 'PreconditionFailed' for a stale If-Match on a read, got {Py.Repr(precondition.Code)}");
        Ensure(
            precondition.Message == "At least one of the pre-conditions you specified did not hold",
            $"expected error message 'At least one of the pre-conditions you specified did not hold' for a stale If-Match on a read, got {Py.Repr(precondition.Message)}");
        Ensure((int)precondition.StatusCode == 412, $"expected HTTP 412 for PreconditionFailed, got {(int)precondition.StatusCode}");

        // --- Effect absent: neither refusal touched the version history.
        var versionsAfter = await VersionsForKey(s3, bucket, key);
        Ensure(
            versionsAfter.SequenceEqual(versionsBefore),
            $"a refused conditional read mutated the version history: ListObjectVersions now reports {Py.Repr(versionsAfter)}, want the unchanged {Py.Repr(versionsBefore)}");

        // --- POSITIVE CONTROL: If-Match on the CURRENT ETag must be honoured and serve the payload.
        using (var matched = await Get(s3, bucket, key, ifMatch: liveEtag))
        {
            var matchedBody = await S3Fidelity.ReadBody(matched);
            Ensure(
                matched.HttpStatusCode == HttpStatusCode.OK,
                $"positive control: GetObject with IfMatch set to the current ETag {Py.Repr(liveEtag)} must return HTTP 200, got {(int)matched.HttpStatusCode}");
            Ensure(
                matchedBody.AsSpan().SequenceEqual(Body),
                $"positive control: GetObject with IfMatch set to the current ETag must return the exact stored payload, got {Py.Repr(Encoding.ASCII.GetString(matchedBody))}, want {Py.Repr(Encoding.ASCII.GetString(Body))}");
        }

        // --- POSITIVE CONTROL: the unconditional read must still return the exact payload.
        using var served = await s3.GetObjectAsync(bucket, key);
        var servedBody = await S3Fidelity.ReadBody(served);
        Ensure(
            servedBody.AsSpan().SequenceEqual(Body),
            $"positive control: the unconditional GetObject must return the exact stored payload, got {Py.Repr(Encoding.ASCII.GetString(servedBody))}, want {Py.Repr(Encoding.ASCII.GetString(Body))}");
        Ensure(served.ETag == liveEtag, $"positive control: the unconditional GetObject reported ETag {Py.Repr(served.ETag)}, want {Py.Repr(liveEtag)}");
        Ensure(
            served.VersionId == liveVersion,
            $"positive control: the unconditional GetObject served version {Py.Repr(served.VersionId)}, want the only version {Py.Repr(liveVersion)}");
    }

    private static Task<GetObjectResponse> Get(IAmazonS3 s3, string bucket, string key, string? ifMatch = null, string? ifNoneMatch = null) =>
        s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, EtagToMatch = ifMatch, EtagToNotMatch = ifNoneMatch });

    /// <summary><c>_versions_for_key</c>: every version id currently listed for one key (versions + delete markers).</summary>
    private static async Task<List<string>> VersionsForKey(IAmazonS3 s3, string bucket, string key)
    {
        var page = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        return [.. (page.Versions ?? []).Where(o => o.Key == key).Select(o => o.VersionId)];
    }
}
