// Translated from behavioral/s3/check_deep_s3_storage_class_multipart_class_complete_after_abort_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-multipart-class-complete-after-abort-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassMultipartClassCompleteAfterAbortRejected
{
    /// <summary>
    /// Multipart uploads report their class in ListMultipartUploads/ListParts and the completed
    /// GLACIER object refuses GetObject; an aborted upload is unlisted, NoSuchUpload, and leaves no
    /// object.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-multipart-class-complete-after-abort-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string noSuchUpload = "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.";
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-mp-");
        const string prefix = "mpu/";
        const string doneKey = prefix + "glacier.bin", doneClass = "GLACIER";
        const string goneKey = prefix + "onezone.bin", goneClass = "ONEZONE_IA";
        var partBody = Enumerable.Repeat((byte)'p', 2048).ToArray();
        var uploads = new Dictionary<string, (string UploadId, string ETag, string Class)>();
        foreach (var (key, sc) in new[] { (doneKey, doneClass), (goneKey, goneClass) })
        {
            var uid = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, StorageClass = new S3StorageClass(sc) })).UploadId;
            ctx.OnCleanup(() => AbortQuietly(s3, bucket, key, uid));
            var etag = (await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket, Key = key, UploadId = uid, PartNumber = 1, InputStream = new MemoryStream(partBody),
            })).ETag;
            uploads[key] = (uid, etag, sc);
        }

        var inFlight = (await ListUploads(s3, bucket, prefix)).ToDictionary(u => (u.Key, u.UploadId), u => u.StorageClass?.Value);
        foreach (var (key, (uid, _, sc)) in uploads)
        {
            Ensure(
                inFlight.GetValueOrDefault((key, uid)) == sc,
                $"ListMultipartUploads for {key}: StorageClass {Py.Repr(inFlight.GetValueOrDefault((key, uid)))}, expected {Py.Repr(sc)}");
            var parts = await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uid });
            Ensure(parts.StorageClass == sc, $"ListParts {key}: StorageClass {Py.Repr(parts.StorageClass)}");
            Ensure((parts.Parts ?? []).Select(p => p.Size).SequenceEqual([(long?)partBody.Length]), $"ListParts {key}: {Py.Repr((parts.Parts ?? []).Select(p => p.Size).ToList())}");
        }

        // Positive control: complete the GLACIER upload.
        var (doneUid, doneEtag, _) = uploads[doneKey];
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = doneKey, UploadId = doneUid, PartETags = [new PartETag(1, doneEtag)],
        });
        var head = await s3.GetObjectMetadataAsync(bucket, doneKey);
        Ensure(head.StorageClass?.Value == doneClass, $"completed object StorageClass {Py.Repr(head.StorageClass?.Value)}");
        Ensure(head.ContentLength == partBody.Length, $"completed object ContentLength {head.ContentLength}");
        var (got, gotMessage) = await ErrorCode(() => s3.GetObjectAsync(bucket, doneKey));
        Ensure(got == "InvalidObjectState", $"GetObject on completed GLACIER upload returned {Py.Repr(got)}");
        Ensure(gotMessage == "The operation is not valid for the object's storage class", gotMessage ?? "None");

        // Break one thing: abort the ONEZONE_IA upload, then try to complete it.
        var (goneUid, goneEtag, _) = uploads[goneKey];
        await s3.AbortMultipartUploadAsync(bucket, goneKey, goneUid);
        var remaining = await Poll(
            ctx,
            async () => (await ListUploads(s3, bucket, prefix)).Select(u => (u.Key, u.UploadId)).ToHashSet(),
            listed => !listed.Contains((goneKey, goneUid)));
        Ensure(!remaining.Contains((goneKey, goneUid)), $"aborted upload still listed: {Py.Repr(remaining.Select(r => r.Key).ToList())}");
        var (lp, lpMessage) = await ErrorCode(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = goneKey, UploadId = goneUid }));
        Ensure(lp == "NoSuchUpload", $"ListParts after abort returned {Py.Repr(lp)}, expected NoSuchUpload");
        Ensure(lpMessage == noSuchUpload, lpMessage ?? "None");
        var (cm, cmMessage) = await ErrorCode(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket, Key = goneKey, UploadId = goneUid, PartETags = [new PartETag(1, goneEtag)],
        }));
        Ensure(cm == "NoSuchUpload", $"CompleteMultipartUpload after abort returned {Py.Repr(cm)}, expected NoSuchUpload");
        Ensure(cmMessage == noSuchUpload, cmMessage ?? "None");

        // HeadObject's 404 carries no body, so there is no AWS message to assert.
        var (absent, _) = await ErrorCode(() => s3.GetObjectMetadataAsync(bucket, goneKey));
        Ensure(absent == "404", $"HeadObject on aborted key returned {Py.Repr(absent)}, expected 404");
        var keys = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix })).S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(keys.SequenceEqual([doneKey]), $"objects under {prefix}: {Py.Repr(keys)}, expected only the completed one");
    }

    /// <summary>_error_code(fn): (code, message) of the ClientError fn raises; an unexpected success raises.</summary>
    private static Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "the call succeeded where an error was expected");

    private static async Task<List<MultipartUpload>> ListUploads(AmazonS3Client s3, string bucket, string prefix)
    {
        var all = new List<MultipartUpload>();
        string? keyMarker = null, uploadIdMarker = null;
        while (true)
        {
            var page = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = prefix, KeyMarker = keyMarker, UploadIdMarker = uploadIdMarker });
            all.AddRange(page.MultipartUploads ?? []);
            if (page.IsTruncated != true)
            {
                return all;
            }

            (keyMarker, uploadIdMarker) = (page.NextKeyMarker, page.NextUploadIdMarker);
        }
    }

    /// <summary>_poll(ctx, fn, done): calls fn every 2 s until done(result) or a 60 s deadline capped by the time left.</summary>
    private static async Task<T> Poll<T>(FidelityContext ctx, Func<Task<T>> fn, Func<T, bool> done, double timeoutSeconds = 60.0)
    {
        var budget = Math.Min(timeoutSeconds, ctx.TimeLeftSeconds);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var result = await fn();
            if (done(result) || clock.Elapsed.TotalSeconds >= budget)
            {
                return result;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task AbortQuietly(AmazonS3Client s3, string bucket, string key, string uploadId)
    {
        // Registered after the bucket teardown, so LIFO runs it while the bucket still exists;
        // an upload already completed or aborted by the check is the only expected refusal.
        if (await FidelityContext.CaptureError(() => s3.AbortMultipartUploadAsync(bucket, key, uploadId)) is { Code: not "NoSuchUpload" } error)
        {
            throw error.Exception;
        }
    }
}
