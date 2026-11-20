// Translated from behavioral/s3/check_create_session_token_ttl.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-session-token-ttl</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateSessionTokenTtl
{
    /// <summary>
    /// CreateSession returns temporary credentials with a fixed ~5-minute TTL (Expiration ~5 minutes
    /// from issuance) and mints an independent session per call; the directory bucket is genuinely
    /// usable for session-authorized Zonal data-plane calls right after minting.
    /// </summary>
    [Test]
    [DisplayName("s3.create-session-token-ttl")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewDirectoryBucket(ctx, s3);
        const string key = "session-ttl-obj.txt";
        var session = await S3Fidelity.CreateSession(s3, bucket);
        var creds = session.Credentials;
        Ensure(
            creds is { AccessKeyId: not null, SecretAccessKey: not null, SessionToken: not null, Expiration: not null },
            "expected AccessKeyId, SecretAccessKey, SessionToken and Expiration in CreateSession Credentials");
        var issuedAt = FidelityTime.UtcNow;
        var ttl = (new DateTimeOffset(S3Fidelity.Utc(creds.Expiration)) - issuedAt).TotalSeconds;
        Ensure(ttl is >= 4 * 60 and <= 6 * 60, $"expected a fixed ~5-minute TTL, got {ttl}s");

        // A second CreateSession call must mint an independent session (its own AccessKeyId/Expiration).
        var session2 = await S3Fidelity.CreateSession(s3, bucket);
        Ensure(
            session2.Credentials.AccessKeyId != creds.AccessKeyId,
            "each CreateSession call must mint a distinct temporary-credential session");

        // Confirm the bucket is genuinely usable for session-authorized Zonal data-plane calls right
        // after minting (via the same client, which the SDK backs with its S3 Express session cache).
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "session-ttl-payload" });
        var got = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(got.SequenceEqual("session-ttl-payload"u8.ToArray()), $"expected round-tripped payload, got {Py.Repr(got)}");
    }
}
