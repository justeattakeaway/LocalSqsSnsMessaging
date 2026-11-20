// Translated from behavioral/s3/check_copy_object_missing_source_permission_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-missing-source-permission-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectMissingSourcePermissionDenied
{
    private static readonly (string, string) HeadAbsent = ("404", "Not Found");
    private static readonly (string, string) NoPolicy = ("NoSuchBucketPolicy", "The bucket policy does not exist");

    /// <summary>CopyObject from a source object explicitly denied s3:GetObject is rejected, not silently permitted.</summary>
    [Test]
    [DisplayName("s3.copy-object-missing-source-permission-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var srcBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-cp-src-");
        var dstBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-cp-dst-");

        const string deniedKey = "denied-object.txt";
        const string allowedKey = "allowed-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = srcBucket, Key = deniedKey, ContentBody = "secret-payload" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = srcBucket, Key = allowedKey, ContentBody = "public-payload" });

        // Explicit Deny on a bucket policy overrides even the bucket owner's own implicit
        // allow -- this is how we remove s3:GetObject on exactly one source object without
        // needing a second IAM principal.
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "DenyGetOnSpecificObject",
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = "s3:GetObject",
                ["Resource"] = $"arn:aws:s3:::{srcBucket}/{deniedKey}",
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = srcBucket, Policy = policy.ToJsonString() });
        ctx.OnCleanup(() => s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = srcBucket }));

        const string destKey = "copied-denied.txt";
        var caller = ctx.CallerArn;
        Task CopyDenied() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = srcBucket, SourceKey = deniedKey, DestinationBucket = dstBucket, DestinationKey = destKey,
        });
        var denied = await PollForError(CopyDenied, expectedCode: "AccessDenied", timeoutSeconds: 20.0);
        var wantDenied = (
            "AccessDenied",
            $"User: {caller} is not authorized to perform: s3:GetObject on resource: " +
            $"\"arn:aws:s3:::{srcBucket}/{deniedKey}\" with an explicit deny in a resource-based policy");
        Ensure(denied == wantDenied, $"expected {S3Fidelity.Repr(wantDenied)}, got {S3Fidelity.Repr(denied)}");

        // effect absent: the destination object was never created
        var absent = await S3Fidelity.HeadError(s3, dstBucket, destKey, "expected a ClientError, but the call SUCCEEDED");
        Ensure(absent == HeadAbsent, $"expected destination object absent {S3Fidelity.Repr(HeadAbsent)}, got {S3Fidelity.Repr(absent)}");

        // restore: removing the deny must let the IDENTICAL call now succeed -- proves the earlier
        // AccessDenied was caused by the policy, not by something else wrong with the request.
        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = srcBucket });
        // The .NET SDK answers NoSuchBucketPolicy as an empty success; the refusal is read off the wire.
        var gone = await S3Fidelity.RawError(ctx, "GET", $"{srcBucket}?policy");
        Ensure((gone.Code, gone.Message) == NoPolicy, $"expected policy gone immediately {S3Fidelity.Repr(NoPolicy)}, got {S3Fidelity.Repr((gone.Code, gone.Message))}");

        // The policy STORE updates immediately (proven above), but S3's authorization cache for the
        // explicit Deny it just enforced drains on its own timeline, independent of the store -- and
        // repeatedly re-issuing the SAME denied CopyObject while it drains was measured to extend how
        // long the negative decision stays cached rather than shorten it. So the drain is waited out on
        // an unrelated, harmless read (get_bucket_policy) that never touches the denied path, and only
        // then is the identical call retried.
        for (var i = 0; i < 6; i++)
        {
            await ctx.Delay(TimeSpan.FromSeconds(5.0));
            // The .NET SDK answers NoSuchBucketPolicy as an empty success; the refusal is read off the wire.
            var recheck = await S3Fidelity.RawError(ctx, "GET", $"{srcBucket}?policy");
            Ensure((recheck.Code, recheck.Message) == NoPolicy, $"policy reappeared: {S3Fidelity.Repr((recheck.Code, recheck.Message))}");
        }

        await PollUntilSuccess(CopyDenied, timeoutSeconds: 30.0, intervalSeconds: 10.0);
        var restoredHead = await s3.GetObjectMetadataAsync(dstBucket, destKey);
        Ensure(
            restoredHead.ContentLength == "secret-payload".Length,
            "expected the restored copy to carry the source object's bytes " +
            $"({"secret-payload".Length}), got {restoredHead.ContentLength}");

        // positive control: copying the object that was never denied succeeds in the same bucket pair
        const string controlKey = "copied-allowed.txt";
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = srcBucket, SourceKey = allowedKey, DestinationBucket = dstBucket, DestinationKey = controlKey,
        });
        var head = await s3.GetObjectMetadataAsync(dstBucket, controlKey);
        Ensure(head.ContentLength == "public-payload".Length, "assert head[\"ContentLength\"] == len(b\"public-payload\")");
    }

    /// <summary>
    /// Retry the call until it raises <paramref name="expectedCode"/> (a freshly written policy propagates);
    /// return that refusal's (code, message). Still not refused at the deadline fails the check.
    /// </summary>
    private static async Task<(string?, string?)> PollForError(Func<Task> call, string expectedCode, double timeoutSeconds = 20.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        string last = "None";
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var error = await FidelityContext.CaptureError(call);
            if (error is not null)
            {
                last = S3Fidelity.Repr((error.Code, error.Message));
                if (error.Code == expectedCode)
                {
                    return (error.Code, error.Message);
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"expected {expectedCode} within {timeoutSeconds}s, last outcome: {last}");
    }

    private static async Task PollUntilSuccess(Func<Task> call, double timeoutSeconds = 20.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        AwsError? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = await FidelityContext.CaptureError(call);
            if (last is null)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"expected success within {timeoutSeconds}s, last error: {last}");
    }
}
