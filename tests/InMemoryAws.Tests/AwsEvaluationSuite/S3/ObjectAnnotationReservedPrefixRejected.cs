// Translated from behavioral/s3/check_object_annotation_reserved_prefix_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-reserved-prefix-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationReservedPrefixRejected
{
    private const string NoSuchAnnotationMessage = "The specified annotation does not exist.";
    private const string InvalidAnnotationMessage = "Your annotation name cannot start with 'aws' or 's3'.";

    /// <summary>
    /// PutObjectAnnotation rejects names starting with 'aws' or 's3' with InvalidAnnotation and its
    /// exact message and stores nothing under them, while an ordinary name round-trips its payload and
    /// is NoSuchAnnotation after delete.
    /// </summary>
    [Test]
    [DisplayName("s3.object-annotation-reserved-prefix-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-annotneg-");
        const string key = "object-for-invalid-annotation.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "hello" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        var (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.PutAnnotation(s3, bucket, key, "aws:reserved-name", "payload"u8.ToArray()));
        Ensure(code == "InvalidAnnotation", $"expected InvalidAnnotation for 'aws:'-prefixed name, got {code}");
        Ensure(message == InvalidAnnotationMessage, $"unexpected InvalidAnnotation message: {Py.Repr(message)}");

        // The name itself is invalid, not merely absent: GetObjectAnnotation enforces the same
        // reserved-prefix validation up front and rejects it identically, rather than answering
        // NoSuchAnnotation for a name nothing was ever stored under.
        var (absentCode, absentMessage) = await GetError(s3, bucket, key, "aws:reserved-name");
        Ensure(
            absentCode == "InvalidAnnotation",
            $"get on the rejected 'aws:'-prefixed name should re-reject it as InvalidAnnotation, got {Py.Repr(absentCode)}");
        Ensure(absentMessage == InvalidAnnotationMessage, $"unexpected InvalidAnnotation message: {Py.Repr(absentMessage)}");

        var (code2, message2) = await S3Fidelity.ErrorPair(() => S3Fidelity.PutAnnotation(s3, bucket, key, "s3:internal-name", "payload"u8.ToArray()));
        Ensure(code2 == "InvalidAnnotation", $"expected InvalidAnnotation for 's3:'-prefixed name, got {code2}");
        Ensure(message2 == InvalidAnnotationMessage, $"unexpected InvalidAnnotation message: {Py.Repr(message2)}");
        var (absentCode2, absentMessage2) = await GetError(s3, bucket, key, "s3:internal-name");
        Ensure(
            absentCode2 == "InvalidAnnotation",
            $"get on the rejected 's3:'-prefixed name should re-reject it as InvalidAnnotation, got {Py.Repr(absentCode2)}");
        Ensure(absentMessage2 == InvalidAnnotationMessage, $"unexpected InvalidAnnotation message: {Py.Repr(absentMessage2)}");

        // A name unrelated to the reserved prefixes must be accepted, round-trip on an
        // independent read, and be fully removable.
        const string acceptedName = "team-priority-label";
        await S3Fidelity.PutAnnotation(s3, bucket, key, acceptedName, "payload"u8.ToArray());
        var got = await S3Fidelity.AnnotationPayload(s3, bucket, key, acceptedName);
        Ensure(got.SequenceEqual("payload"u8.ToArray()), "accepted annotation must round-trip its payload");
        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = acceptedName });
        var (goneCode, goneMessage) = await GetError(s3, bucket, key, acceptedName);
        Ensure(goneCode == "NoSuchAnnotation", $"deleted annotation must no longer be readable, got {Py.Repr(goneCode)}");
        Ensure(goneMessage == NoSuchAnnotationMessage, $"unexpected NoSuchAnnotation message: {Py.Repr(goneMessage)}");
    }

    private static Task<(string? Code, string? Message)> GetError(IAmazonS3 s3, string bucket, string key, string annotationName) =>
        S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, annotationName));
}
