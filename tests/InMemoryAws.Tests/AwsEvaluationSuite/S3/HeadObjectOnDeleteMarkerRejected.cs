// Translated from behavioral/s3/check_head_object_on_delete_marker_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.S3CoreFidelity;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.head-object-on-delete-marker-rejected</summary>
/// <remarks>
/// botocore keeps an error response's headers in <c>ResponseMetadata.HTTPHeaders</c>; the .NET SDK
/// does not, so each refused request is also sent over raw HTTP to read its headers.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class HeadObjectOnDeleteMarkerRejected
{
    /// <summary>
    /// A delete marker is reported by headers: unversioned HEAD/GET are 404, addressed by version id they
    /// are 405 with allow: DELETE; a never-existed key carries no marker header. The listing split and
    /// resurrection by deleting the marker are asserted too.
    /// </summary>
    [Test]
    [DisplayName("s3.head-object-on-delete-marker-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        const string key = "marked.txt";
        var body = Encoding.UTF8.GetBytes("still-here");
        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) })).VersionId;

        // --- Control before the delete: HEAD is a plain 200.
        Ensure((await s3.GetObjectMetadataAsync(bucket, key)).ContentLength == body.Length, "assert head_object(...)['ContentLength'] == len(body)");

        // --- Break exactly one thing: a versioned delete, which writes a marker.
        var delResp = await s3.DeleteObjectAsync(bucket, key);
        Ensure(delResp.DeleteMarker == "true", $"a versioned delete must report DeleteMarker=True, got {Py.Repr(delResp.DeleteMarker)}");
        var markerId = delResp.VersionId;
        Ensure(
            !string.IsNullOrEmpty(markerId) && markerId != v1,
            $"the delete marker needs its own VersionId distinct from {Py.Repr(v1)}, got {Py.Repr(markerId)}");

        // --- HEAD on the delete-marked KEY (unversioned): 404, but a 404 that says WHY. The
        // resurrect-vs-create distinction lives in the headers, not the status code.
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(exc is not null, "HeadObject on a delete-marked key succeeded; the marker is not honoured");
        var status = (int)exc.StatusCode;
        Ensure(
            status == 404,
            $"HeadObject on the delete-marked key (no VersionId) must be 404, got {status}; the 405 "
            + "belongs to the version-addressed form asserted below");
        using (var headers = await RawObjectRequest(ctx, HttpMethod.Head, bucket, key))
        {
            Ensure(
                Header(headers, "x-amz-delete-marker") == "true",
                "the 404 must carry x-amz-delete-marker: true — that header is the ONLY thing separating "
                + "\"the latest version is a delete marker\" from \"this key never existed\", since both are "
                + $"404 — got {Py.Repr(Header(headers, "x-amz-delete-marker"))}");
            Ensure(
                Header(headers, "x-amz-version-id") == markerId,
                $"the 404 must report the marker's version id {Py.Repr(markerId)}, got "
                + $"{Py.Repr(Header(headers, "x-amz-version-id"))}");
            Ensure(
                !HasHeader(headers, "allow"),
                "the unversioned 404 must NOT carry an allow header; that belongs to the 405, got "
                + $"{Py.Repr(Header(headers, "allow"))}");
        }

        // --- HEAD on the MARKER ITSELF, addressed by version id: 405 MethodNotAllowed, because a
        // delete marker is not an object and the only method it accepts is DELETE.
        exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, VersionId = markerId }));
        Ensure(
            exc is not null,
            "HeadObject on a delete marker addressed by VersionId succeeded; a delete marker has no "
            + "retrievable representation");
        status = (int)exc.StatusCode;
        Ensure(
            status == 405,
            $"HeadObject(VersionId=<delete marker>) must be 405 MethodNotAllowed, got {status}; "
            + "answering 404 here means the emulator cannot distinguish a marker that exists from a "
            + "version that does not, and answering 200 means it serves a marker as an object");
        using (var headers = await RawObjectRequest(ctx, HttpMethod.Head, bucket, key, markerId))
        {
            Ensure(
                Header(headers, "allow") == "DELETE",
                "the 405 must advertise the one permitted method with allow: DELETE, got "
                + $"{Py.Repr(Header(headers, "allow"))} — that header is how a client learns the marker can only be "
                + "removed, never read");
            Ensure(
                Header(headers, "x-amz-delete-marker") == "true",
                "the 405 must also carry x-amz-delete-marker: true, got "
                + $"{Py.Repr(Header(headers, "x-amz-delete-marker"))}");
            Ensure(
                Header(headers, "x-amz-version-id") == markerId,
                $"the 405 must echo the addressed version id {Py.Repr(markerId)}, got "
                + $"{Py.Repr(Header(headers, "x-amz-version-id"))}");
            Ensure(
                HasHeader(headers, "last-modified"),
                "the 405 carries the marker's last-modified: S3 knows the marker exists and when it was "
                + "written, it just refuses to serve it as an object");
        }

        // --- GET on the marker by version id: the same 405, with the error CODE spelled out (a GET
        // has a response body, so botocore surfaces MethodNotAllowed instead of the bare status).
        exc = await FidelityContext.CaptureError(() => GetBytes(s3, bucket, key, markerId));
        Ensure(exc is not null, "GetObject on a delete marker addressed by VersionId succeeded");
        Ensure(
            exc.Code == "MethodNotAllowed",
            $"GetObject(VersionId=<delete marker>) must be MethodNotAllowed, got {Py.Repr(exc.Code)}");
        Ensure((int)exc.StatusCode == 405, ((int)exc.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using (var headers = await RawObjectRequest(ctx, HttpMethod.Get, bucket, key, markerId))
        {
            Ensure(Header(headers, "allow") == "DELETE", "the GET 405 must advertise allow: DELETE too");
        }

        Ensure(exc.Message == "The specified method is not allowed against this resource.", exc.Message ?? "None");

        // --- GET on the delete-marked KEY (unversioned): 404 NoSuchKey, with the marker headers.
        exc = await FidelityContext.CaptureError(() => GetBytes(s3, bucket, key));
        Ensure(exc is not null, "GetObject on a delete-marked key succeeded");
        Ensure(exc.Code == "NoSuchKey", $"expected NoSuchKey from GetObject on a delete marker, got {Py.Repr(exc.Code)}");
        Ensure((int)exc.StatusCode == 404, $"expected HTTP 404 from GetObject on a delete marker, got {(int)exc.StatusCode}");
        Ensure(exc.Message == "The specified key does not exist.", exc.Message ?? "None");
        using (var headers = await RawObjectRequest(ctx, HttpMethod.Get, bucket, key))
        {
            Ensure(
                Header(headers, "x-amz-delete-marker") == "true",
                "GetObject's 404 on a delete marker must also carry x-amz-delete-marker: true, got "
                + $"{Py.Repr(Header(headers, "x-amz-delete-marker"))}");
        }

        // --- Listing split: the flat listing hides the key, the version listing shows both entries.
        var flat = await ListAllKeys(s3, bucket);
        Ensure(!flat.Contains(key), $"a delete-marked key must not appear in ListObjectsV2, got {Py.Repr(flat)}");
        var versionsResp = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        var markers = (versionsResp.Versions ?? []).Where(v => v.IsDeleteMarker == true).ToList();
        var dataVersions = (versionsResp.Versions ?? []).Where(v => v.IsDeleteMarker != true).ToList();
        Ensure(
            markers.Select(m => m.VersionId).SequenceEqual([markerId]),
            $"expected exactly the marker {Py.Repr(markerId)} in DeleteMarkers, got {Py.Repr(markers.Select(m => m.VersionId).ToList())}");
        Ensure(markers[0].IsLatest == true, "the delete marker must be the latest version");
        Ensure(
            dataVersions.Select(v => v.VersionId).SequenceEqual([v1]),
            $"expected the data version {Py.Repr(v1)} to survive, got {Py.Repr(dataVersions.Select(v => v.VersionId).ToList())}");
        Ensure(dataVersions[0].IsLatest == false, "the data version must no longer be latest");

        // --- Effect absent: the data was NOT destroyed. Reading it by version id still works.
        Ensure((await GetBytes(s3, bucket, key, v1)).AsSpan().SequenceEqual(body), "the shadowed version's body changed");
        Ensure(
            (await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, VersionId = v1 })).ContentLength == body.Length,
            "assert head_object(..., VersionId=v1)['ContentLength'] == len(body)");

        // --- Control still works: removing the marker resurrects the key.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = markerId });
        Ensure((await GetBytes(s3, bucket, key)).AsSpan().SequenceEqual(body), "deleting the delete marker must resurrect the previous version");
        var flatAfter = await ListAllKeys(s3, bucket);
        Ensure(flatAfter.SequenceEqual([key]), $"the resurrected key must be listed again, got {Py.Repr(flatAfter)}");

        // --- A key that genuinely never existed is a plain 404 with NO marker header — the contrast
        // that makes the 405 meaningful.
        exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, "never-existed.txt"));
        Ensure(exc is not null, "HeadObject on an absent key succeeded");
        Ensure((int)exc.StatusCode == 404, "a key that never existed must be 404, not 405");
        using (var headers = await RawObjectRequest(ctx, HttpMethod.Head, bucket, "never-existed.txt"))
        {
            Ensure(
                !HasHeader(headers, "x-amz-delete-marker"),
                $"a never-existed key must not report x-amz-delete-marker, got {Py.Repr(Header(headers, "x-amz-delete-marker"))}");
        }
    }

    private static async Task<byte[]> GetBytes(IAmazonS3 s3, string bucket, string key, string? versionId = null)
    {
        using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId });
        return await S3Fidelity.ReadBody(response);
    }
}
