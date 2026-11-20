using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the Object Lock ports share: the checks' <c>_new_lock_bucket</c> (a bucket created with
/// Object Lock, emptied at cleanup by lifting legal holds and event holds and bypassing
/// <c>GOVERNANCE</c>), their <c>_client_error</c> helpers, and the date arithmetic of their
/// <c>datetime</c> values.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_new_lock_bucket(ctx, s3)</c>: a bucket named <paramref name="prefix"/> plus 12 hex digits,
    /// created with <c>ObjectLockEnabledForBucket=True</c> in the check's region, emptied and deleted
    /// at cleanup.
    /// </summary>
    public static async Task<string> CreateObjectLockBucket(FidelityContext ctx, string prefix = "sim2real-behav-lockbkt-", AmazonS3Client? client = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var s3 = client ?? ctx.S3;
        var name = FidelityContext.UniqueName(prefix);
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false, ObjectLockEnabledForBucket = true };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => EmptyAndDeleteObjectLockBucket(s3, name));
        return name;
    }

    /// <summary>
    /// The lock checks' teardown: lifts every version's legal hold and event hold, deletes every
    /// version and delete marker with <c>BypassGovernanceRetention</c>, then the bucket. A version
    /// still under <c>COMPLIANCE</c> retention stays, and so does the bucket.
    /// </summary>
    public static async Task EmptyAndDeleteObjectLockBucket(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        try
        {
            var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
            foreach (var version in versions.Versions ?? [])
            {
                if (version.IsDeleteMarker != true)
                {
                    await FidelityContext.CaptureError(() => s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
                    {
                        BucketName = bucket,
                        Key = version.Key,
                        VersionId = version.VersionId,
                        LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
                    }));
                    var (retention, _) = await FidelityContext.Capture(() => s3.GetObjectRetentionAsync(new GetObjectRetentionRequest
                    {
                        BucketName = bucket,
                        Key = version.Key,
                        VersionId = version.VersionId,
                    }));
                    if (retention?.Retention?.EventHold?.Value == "ON")
                    {
                        await FidelityContext.CaptureError(() => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
                        {
                            BucketName = bucket,
                            Key = version.Key,
                            VersionId = version.VersionId,
                            BypassGovernanceRetention = true,
                            Retention = new ObjectLockRetention
                            {
                                Mode = retention.Retention.Mode,
                                RetainUntilDate = retention.Retention.RetainUntilDate,
                                EventHold = "OFF",
                            },
                        }));
                    }
                }

                await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = version.Key,
                    VersionId = version.VersionId,
                    BypassGovernanceRetention = true,
                }));
            }
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucket" or "NoSuchKey")
        {
        }

        await FidelityContext.CaptureError(() => s3.DeleteBucketAsync(bucket));
    }

    /// <summary>
    /// The checks' <c>_client_error(fn)</c>: the <c>(Error.Code, Error.Message)</c> botocore reports
    /// for the error <paramref name="call"/> raises (see <see cref="Botocore"/>); a success fails the
    /// check with <paramref name="unexpectedSuccess"/>.
    /// </summary>
    public static async Task<(string? Code, string? Message)> ClientError(
        Func<Task> call,
        string unexpectedSuccess = "expected a ClientError, but the call SUCCEEDED")
    {
        var error = await FidelityContext.CaptureError(call) ?? throw FidelityAssert.Fail(unexpectedSuccess);
        return Botocore(error);
    }

    /// <summary>
    /// <c>(Error.Code, Error.Message)</c> as botocore reports them. An error response with no body (a
    /// HEAD request's) has no code: botocore reports its HTTP status as the code (<c>"404"</c>) and the
    /// reason phrase as the message (<c>"Not Found"</c>), where the .NET SDK names the status
    /// (<c>NotFound</c>) and writes a message of its own.
    /// </summary>
    public static (string? Code, string? Message) Botocore(AwsError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Message?.StartsWith("Error making request with Error Code ", StringComparison.Ordinal) == true)
        {
            using var status = new HttpResponseMessage(error.StatusCode);
            return (((int)error.StatusCode).ToString(CultureInfo.InvariantCulture), status.ReasonPhrase);
        }

        return (error.Code, error.Message);
    }

    /// <summary><c>dt.replace(microsecond=0)</c>.</summary>
    public static DateTime WholeSeconds(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    /// <summary>A timestamp the SDK answered, in UTC, as botocore's aware <c>datetime</c> compares it.</summary>
    public static DateTime Utc(DateTime? value) =>
        value is not { } v ? throw FidelityAssert.Fail("the response carries no timestamp")
        : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc);
}
