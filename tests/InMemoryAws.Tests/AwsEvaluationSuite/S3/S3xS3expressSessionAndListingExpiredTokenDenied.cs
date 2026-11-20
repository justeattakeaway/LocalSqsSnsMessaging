// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_expired_token_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-expired-token-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingExpiredTokenDenied
{
    /// <summary>Seconds before the server-declared Expiration during which a refusal counts as PREMATURE.</summary>
    private const double EarlySlackS = 15;

    /// <summary>Seconds past the server-declared Expiration within which the refusal must have appeared.</summary>
    private const double LateCeilingS = 120;

    private const double PollIntervalS = 10;

    /// <summary>
    /// A CreateSession token is honoured for its server-declared lifetime and AccessDenied once its
    /// Expiration passes (GetObject and PutObject, the refused write leaving no object), while a fresh
    /// token succeeds; a refusal more than EarlySlackS before Expiration fails as premature.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-expired-token-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        // timing:real-time — ttl: 300 s https://docs.aws.amazon.com/AmazonS3/latest/API/API_CreateSession.html
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-exp-");
        const string key = "exp/object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "live-object" });
        var (session, headers) = await S3Fidelity.WithHeaders(s3, () => S3Fidelity.CreateSession(s3, bucket, "ReadWrite"));
        var started = FidelityTime.StartNew();
        var serverNow = DateTimeOffset.Parse(headers["date"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var ttl = (new DateTimeOffset(S3Fidelity.Utc(session.Credentials.Expiration)) - serverNow).TotalSeconds;
        Ensure(ttl is >= 240 and <= 330, $"CreateSession token lifetime is {ttl:0}s, expected about 300s");
        var (old, stats) = S3Fidelity.SessionClient(ctx, session);

        // Positive control at mint time: the token works.
        var first = await S3Fidelity.GetBytes(old, bucket, key);
        Ensure(first.SequenceEqual("live-object"u8.ToArray()), $"fresh session token could not read the object: {Py.Repr(first)}");

        // Poll through the token's life until it is refused (bounded past Expiration).
        var honoured = 1;
        double elapsed;
        (string? Code, int Status, string? Message) refused;
        while (true)
        {
            elapsed = started.Elapsed.TotalSeconds;
            var error = await FidelityContext.CaptureError(() => S3Fidelity.GetBytes(old, bucket, key));
            if (error is not null)
            {
                refused = (error.Code, (int)error.StatusCode, error.Message);
                break;
            }

            honoured++;
            if (elapsed > ttl + LateCeilingS)
            {
                throw Fail(
                    $"token still honoured {elapsed:0}s after mint, {elapsed - ttl:0}s past its " +
                    $"Expiration ({honoured} successful reads)");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(PollIntervalS));
        }

        Ensure(elapsed >= ttl - EarlySlackS, $"token refused {S3Fidelity.Repr(refused)} only {elapsed:0}s after mint, before its {ttl:0}s lifetime");
        Ensure(
            refused == ("AccessDenied", 403, "Access Denied"),
            $"GetObject with the expired session token returned {S3Fidelity.Repr(refused)}, expected ('AccessDenied', 403, 'Access Denied')");
        var late = await S3Fidelity.ErrorWithStatus(() => old.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "exp/late-write.txt", ContentBody = "late" }));
        Ensure(
            late == ("AccessDenied", 403, "Access Denied"),
            $"PutObject with the expired session token returned {S3Fidelity.Repr(late)}, expected ('AccessDenied', 403, 'Access Denied')");

        // Effect absent: the refused write created nothing (read with the runner's own identity).
        var keys = S3Fidelity.SortedKeys(await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "exp/" }));
        Ensure(keys.SequenceEqual([key]), $"refused expired-token PutObject left keys {Py.Repr(keys)}, expected only [{Py.Repr(key)}]");

        // Positive control: a freshly minted token on the identical GetObject succeeds.
        var (fresh, freshStats) = S3Fidelity.SessionClient(ctx, await S3Fidelity.CreateSession(s3, bucket, "ReadWrite"));
        var again = await S3Fidelity.GetBytes(fresh, bucket, key);
        Ensure(again.SequenceEqual("live-object"u8.ToArray()), $"freshly minted token could not read the object: {Py.Repr(again)}");
        foreach (var (label, st) in new[] { ("expired", stats), ("fresh", freshStats) })
        {
            Ensure(
                !st.Ops.Contains("CreateSession"),
                $"the {label} session-pinned client minted its own session (ops {Py.Repr(st.Ops)}) -- the expired-token path was not exercised");
            Ensure(
                st.Pinned == st.Ops.Count,
                $"{label}: not every call was signed with the pinned session: pinned={st.Pinned} ops={Py.Repr(st.Ops)}");
        }
    }
}
