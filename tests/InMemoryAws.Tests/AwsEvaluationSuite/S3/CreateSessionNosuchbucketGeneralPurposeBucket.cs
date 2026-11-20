// Translated from behavioral/s3/check_create_session_nosuchbucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>
/// s3.create-session-nosuchbucket (named apart from <see cref="CreateSessionNoSuchBucket"/>, whose file name differs from this check's only in case).
/// </summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateSessionNosuchbucketGeneralPurposeBucket
{
    /// <summary>
    /// CreateSession on a bucket that does not exist fails with NoSuchBucket and its exact message,
    /// while the same call on an existing bucket raises nothing.
    /// </summary>
    [Test]
    [DisplayName("s3.create-session-nosuchbucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // Positive control: CreateSession against an existing bucket must not raise.
        await S3Fidelity.CreateSession(s3, bucket);

        var missingBucket = FidelityContext.UniqueName("sim2real-nobucket-");
        var err = await S3Fidelity.ErrorPair(() => S3Fidelity.CreateSession(s3, missingBucket), "expected a ClientError, but the call succeeded");
        Ensure(
            err == ("NoSuchBucket", "The specified bucket does not exist"),
            $"expected NoSuchBucket with AWS's exact message, got {S3Fidelity.Repr(err)}");
    }
}
