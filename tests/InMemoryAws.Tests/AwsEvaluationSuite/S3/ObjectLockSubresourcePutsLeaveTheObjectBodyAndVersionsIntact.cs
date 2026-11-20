// Translated from behavioral/s3/check_object_lock_subresource_puts_leave_the_object_body_and_versions_intact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-lock-subresource-puts-leave-the-object-body-and-versions-intact</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLockSubresourcePutsLeaveTheObjectBodyAndVersionsIntact
{
    private const string Key = "locked/report.csv";
    private static readonly byte[] Body = "id,amount\n1,42\n2,17\n"u8.ToArray();

    /// <summary>
    /// PutObjectLegalHold and PutObjectRetention change only lock metadata: the object keeps its body,
    /// ETag, VersionId and ContentType, the key has one version and no delete marker, and HeadObject and
    /// both lock GETs report the hold ON and GOVERNANCE retention.
    /// </summary>
    [Test]
    [DisplayName("s3.object-lock-subresource-puts-leave-the-object-body-and-versions-intact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-lockmeta-");

        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(Body), ContentType = "text/csv" });
        var (etag, versionId) = (put.ETag, put.VersionId);

        var hold = await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest { BucketName = bucket, Key = Key, LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On } });
        Ensure(hold.HttpStatusCode == HttpStatusCode.OK, $"{hold.HttpStatusCode}");
        var retainUntil = S3Fidelity.WholeSeconds(FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromDays(1));
        var retention = await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = Key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });
        Ensure(retention.HttpStatusCode == HttpStatusCode.OK, $"{retention.HttpStatusCode}");

        using (var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = Key }))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(body.AsSpan().SequenceEqual(Body), $"the current object body became {Py.Repr(body)}, expected {Py.Repr(Body)}");
            Ensure(got.ETag == etag, $"the current ETag became {Py.Repr(got.ETag)}, expected {Py.Repr(etag)}");
            Ensure(
                got.VersionId == versionId,
                $"the current version moved to a new VersionId after the lock PUTs (same id: {(got.VersionId == versionId ? "True" : "False")})");
            Ensure(got.Headers.ContentType == "text/csv", $"ContentType became {Py.Repr(got.Headers.ContentType)}");
        }

        var (versions, markers) = await Versions(s3, bucket);
        Ensure(
            versions.Count == 1 && versions[0].Key == Key && versions[0].VersionId == versionId && versions[0].IsLatest == true,
            $"the bucket must hold exactly the one original version of {Py.Repr(Key)}, got [{string.Join(", ", versions.Select(v => $"({Py.Repr(v.Key)}, {v.IsLatest}, {v.Size})"))}]");
        Ensure(markers.Count == 0, $"the lock PUTs created {markers.Count} delete marker(s)");

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = Key, VersionId = versionId });
        Ensure(head.ObjectLockLegalHoldStatus == "ON", Py.Repr(head.ObjectLockLegalHoldStatus?.Value));
        Ensure(head.ObjectLockMode == "GOVERNANCE", Py.Repr(head.ObjectLockMode?.Value));
        Ensure(
            head.ObjectLockRetainUntilDate is not null && S3Fidelity.Utc(head.ObjectLockRetainUntilDate) == retainUntil,
            $"HeadObject reports retain-until {head.ObjectLockRetainUntilDate:O}, expected {retainUntil:O}");
        Ensure(head.ContentLength == Body.Length, $"{head.ContentLength}");

        var legal = await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = Key, VersionId = versionId });
        Ensure(legal.LegalHold.Status == "ON", $"{{'Status': {Py.Repr(legal.LegalHold.Status?.Value)}}}");
        var kept = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = Key, VersionId = versionId })).Retention;
        Ensure(
            kept.Mode == "GOVERNANCE" && kept.RetainUntilDate is not null && S3Fidelity.Utc(kept.RetainUntilDate) == retainUntil
                && kept.EventHold is null && kept.EventHoldDuration is null,
            $"GetObjectRetention returned {{'Mode': {Py.Repr(kept.Mode?.Value)}, 'RetainUntilDate': {kept.RetainUntilDate:O}}}, expected GOVERNANCE until {retainUntil:O}");

        // Lifting the hold is also metadata-only: the body and the version count stay put.
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = bucket,
            Key = Key,
            VersionId = versionId,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
        });
        var lifted = await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = Key, VersionId = versionId });
        Ensure(lifted.LegalHold.Status == "OFF", $"{{'Status': {Py.Repr(lifted.LegalHold.Status?.Value)}}}");
        using (var again = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = Key }))
        {
            Ensure(
                (await S3Fidelity.ReadBody(again)).AsSpan().SequenceEqual(Body) && again.VersionId == versionId,
                "lifting the legal hold changed the current object");
        }

        (versions, markers) = await Versions(s3, bucket);
        Ensure(
            versions.Count == 1 && markers.Count == 0,
            $"after lifting the hold the bucket holds {versions.Count} version(s) and {markers.Count} delete marker(s); expected 1 and 0");
    }

    /// <summary>Every version and every delete marker of the bucket, paginated.</summary>
    private static async Task<(List<S3ObjectVersion> Versions, List<S3ObjectVersion> Markers)> Versions(IAmazonS3 s3, string bucket)
    {
        var all = new List<S3ObjectVersion>();
        var request = new ListVersionsRequest { BucketName = bucket };
        while (true)
        {
            var page = await s3.ListVersionsAsync(request);
            all.AddRange(page.Versions ?? []);
            if (page.IsTruncated != true)
            {
                break;
            }

            request.KeyMarker = page.NextKeyMarker;
            request.VersionIdMarker = page.NextVersionIdMarker;
        }

        return ([.. all.Where(v => v.IsDeleteMarker != true)], [.. all.Where(v => v.IsDeleteMarker == true)]);
    }
}
