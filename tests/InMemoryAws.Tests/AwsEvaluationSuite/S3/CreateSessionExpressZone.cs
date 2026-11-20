// Translated from behavioral/s3/check_create_session_express_zone.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-session-express-zone</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateSessionExpressZone
{
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist.");

    /// <summary>CreateSession issues scoped temporary credentials for a directory bucket; NoSuchBucket otherwise.</summary>
    [Test]
    [DisplayName("s3.create-session-express-zone")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (bucket, az) = await S3Fidelity.CreateDirectoryBucket(
            ctx, s3, FidelityContext.UniqueName("sim2real-s3exp-").ToLowerInvariant().TrimEnd('-'), S3Fidelity.ExpressZoneIds[ctx.Region]);
        var resp = await S3Fidelity.CreateSession(s3, bucket);
        var creds = resp.Credentials;
        Ensure(!string.IsNullOrEmpty(creds.AccessKeyId), "missing AccessKeyId in session credentials");
        Ensure(!string.IsNullOrEmpty(creds.SecretAccessKey), "missing SecretAccessKey in session credentials");
        Ensure(!string.IsNullOrEmpty(creds.SessionToken), "missing SessionToken in session credentials");
        var now = FidelityTime.UtcNow;
        var expiration = new DateTimeOffset(S3Fidelity.Utc(creds.Expiration));
        Ensure(expiration > now, "session credentials are already expired");
        Ensure(expiration <= now + TimeSpan.FromMinutes(6), $"expected a TTL of roughly 5 minutes, got expiration {expiration}");

        // The minted session is not just well-shaped, it is genuinely usable: an independent
        // operation (put_object/get_object, not create_session) against the same bucket proves the
        // zone-scoped credentials actually authorize Zonal data-plane calls.
        var key = FidelityContext.UniqueName("sim2real-s3exp-obj-");
        var payload = "create-session-express-zone-payload"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(payload) });
        var got = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(got.SequenceEqual(payload), $"expected round-tripped payload {Py.Repr(payload)}, got {Py.Repr(got)}");

        // Positive control established above; now break the one precondition: bucket existence.
        // The AZ segment MUST belong to the region under test, otherwise S3 Express endpoint
        // resolution can't even build a resolvable hostname (DNS/connect failure) and we'd never
        // reach the service to observe NoSuchBucket. Reuse the same (region-valid) AZ used above.
        var missing = $"{FidelityContext.UniqueName("sim2real-s3exp-").ToLowerInvariant().TrimEnd('-')}--{az}--x-s3";
        var refusal = await S3Fidelity.ErrorPair(() => S3Fidelity.CreateSession(s3, missing), "expected a ClientError, but the call succeeded");
        Ensure(refusal == NoSuchBucket, $"expected {S3Fidelity.Repr(NoSuchBucket)}, got {S3Fidelity.Repr(refusal)}");
    }
}
