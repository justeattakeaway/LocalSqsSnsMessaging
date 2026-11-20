// Translated from behavioral/s3/check_presigned_post_expired_policy_rejected_and_writes_nothing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.presigned-post-expired-policy-rejected-and-writes-nothing</summary>
[Category("AwsEvaluationSuite")]
public sealed class PresignedPostExpiredPolicyRejectedAndWritesNothing
{
    /// <summary>
    /// A presigned POST whose policy expiration is in the past is refused with 403 AccessDenied 'Invalid
    /// according to Policy: Policy expired.' and writes nothing, while the same form with an unexpired
    /// policy uploads with 201 and stores its bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.presigned-post-expired-policy-rejected-and-writes-nothing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string prefix = "uploads/";
        const int maxBytes = 16;
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        List<S3PostCondition> conditions =
        [
            S3PostCondition.ExactMatch("success_action_status", "201"),
            S3PostCondition.StartsWith("key", prefix),
            S3PostCondition.ContentLengthRange(1, maxBytes),
        ];

        // Positive control: the identical form with an unexpired policy uploads.
        var presigned = await s3.CreatePresignedPostAsync(new CreatePresignedPostRequest
        {
            BucketName = bucket,
            Key = prefix + "${filename}",
            Fields = new Dictionary<string, string> { ["success_action_status"] = "201" },
            Conditions = conditions,
            Expires = ctx.Time.GetUtcNow().UtcDateTime.AddSeconds(600),
        });
        var goodName = $"good-{Guid.NewGuid():N}.txt";
        var goodBody = "ok"u8.ToArray();
        var (status, respBody) = await S3Fidelity.PostForm(ctx, presigned.Url, [.. presigned.Fields], goodName, goodBody);
        Ensure(status == 201, $"the conforming POST was answered {status} with {S3Fidelity.Head(respBody, 300)}; expected 201");
        var goodKey = prefix + goodName;
        var stored = await S3Fidelity.GetBytes(s3, bucket, goodKey);
        Ensure(
            stored.SequenceEqual(goodBody),
            $"GetObject returned {S3Fidelity.Head(stored)} for the POST-uploaded key {Py.Repr(goodKey)}; expected " +
            S3Fidelity.Head(goodBody));

        // Break exactly one thing: the policy's expiration is already in the past.
        var expired = await s3.CreatePresignedPostAsync(new CreatePresignedPostRequest
        {
            BucketName = bucket,
            Key = prefix + "${filename}",
            Fields = new Dictionary<string, string> { ["success_action_status"] = "201" },
            Conditions = conditions,
            Expires = ctx.Time.GetUtcNow().UtcDateTime.AddSeconds(-60),
        });
        var expiredName = $"expired-{Guid.NewGuid():N}.txt";
        (status, respBody) = await S3Fidelity.PostForm(ctx, expired.Url, [.. expired.Fields], expiredName, "ok"u8.ToArray());
        Ensure(
            status == 403,
            "POSTing against a policy whose expiration already elapsed was answered " +
            $"{status} with {S3Fidelity.Head(respBody, 300)}; expected 403");
        var (code, message) = S3Fidelity.XmlCodeMessage(respBody);
        Ensure(code == "AccessDenied", $"the expired-policy POST returned code {Py.Repr(code)}; expected 'AccessDenied': {S3Fidelity.Head(respBody, 400)}");
        const string expectedMessage = "Invalid according to Policy: Policy expired.";
        Ensure(message == expectedMessage, $"the expired-policy POST returned message {Py.Repr(message)}; expected {Py.Repr(expectedMessage)}");

        // Effect absent: the refused POST wrote nothing.
        var expiredKey = prefix + expiredName;
        var head = await S3Fidelity.HeadError(s3, bucket, expiredKey);
        Ensure(head.Code == "404", $"the key {Py.Repr(expiredKey)} exists after a POST refused for an expired policy");
        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            listed.SequenceEqual([goodKey]),
            $"the bucket lists {Py.Repr(listed)}; expected exactly {Py.Repr(new List<string> { goodKey })} — the refused POST " +
            "left an object behind");
    }
}
