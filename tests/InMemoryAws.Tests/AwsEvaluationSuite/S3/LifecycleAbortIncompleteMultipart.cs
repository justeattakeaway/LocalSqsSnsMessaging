// Translated from behavioral/s3/check_lifecycle_abort_incomplete_multipart.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.lifecycle-abort-incomplete-multipart</summary>
[Category("AwsEvaluationSuite")]
public sealed class LifecycleAbortIncompleteMultipart
{
    /// <summary>
    /// An AbortIncompleteMultipartUpload lifecycle rule is accepted and reflected by GetBucketLifecycleConfiguration.
    /// A fresh (not-yet-stale) multipart upload matching its prefix is still listed by ListMultipartUploads --
    /// the actual auto-abort fires only once the real DaysAfterInitiation window (minimum 1 day) elapses.
    /// </summary>
    [Test]
    [DisplayName("s3.lifecycle-abort-incomplete-multipart")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "sim2real-ttl-abort-mpu-rule",
                        Filter = new LifecycleFilter { Prefix = "mpu-ttl-" },
                        Status = LifecycleRuleStatus.Enabled,
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 1 },
                    },
                ],
            },
        });

        var cfg = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var rule = cfg.Configuration.Rules.Where(r => r.Id == "sim2real-ttl-abort-mpu-rule").Single();
        Ensure(
            rule.AbortIncompleteMultipartUpload?.DaysAfterInitiation == 1,
            $"expected DaysAfterInitiation=1 reflected back, got {Py.Repr(S3Fidelity.Boto(rule))}");

        const string key = "mpu-ttl-stale-upload.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        try
        {
            var uploads = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = "mpu-ttl-" })).MultipartUploads;
            var uploadIds = uploads.Select(u => u.UploadId).ToHashSet(StringComparer.Ordinal);
            Ensure(uploadIds.Contains(uploadId), "expected the fresh (not yet past its DaysAfterInitiation TTL) upload still listed");
        }
        finally
        {
            await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId });
        }

        var uploadsAfterAbort = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = "mpu-ttl-" })).MultipartUploads ?? [];
        Ensure(
            !uploadsAfterAbort.Any(u => u.UploadId == uploadId),
            $"expected upload_id={uploadId} gone from ListMultipartUploads after AbortMultipartUpload, still present: {Py.Repr(uploadsAfterAbort.Select(u => u.UploadId).ToList())}");
    }
}
