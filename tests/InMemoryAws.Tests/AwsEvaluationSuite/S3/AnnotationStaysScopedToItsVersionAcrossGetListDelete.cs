// Translated from behavioral/s3/check_annotation_stays_scoped_to_its_version_across_get_list_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.annotation-stays-scoped-to-its-version-across-get-list-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class AnnotationStaysScopedToItsVersionAcrossGetListDelete
{
    private static readonly (string Code, string Message) NoSuchAnnotation =
        ("NoSuchAnnotation", "The specified annotation does not exist.");

    /// <summary>
    /// An annotation written with no VersionId binds to the version current at that moment: a
    /// later PutObject's version has none of it on Get or List, a VersionId-qualified Get and List
    /// still see it, and a VersionId-qualified Delete clears it from only that version.
    /// </summary>
    [Test]
    [DisplayName("s3.annotation-stays-scoped-to-its-version-across-get-list-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-annot-scope-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);

        const string key = "fulfillment-record.json";
        const string name = "shipment-tracking-id";
        var payloadV1 = "warehouse-west-dock-12"u8.ToArray();
        var payloadV2 = "warehouse-east-dock-04"u8.ToArray();

        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{\"stage\":\"received\"}" })).VersionId;

        var putV1 = await S3Fidelity.PutAnnotation(s3, bucket, key, name, payloadV1);
        Ensure(
            putV1.ObjectVersionId == v1,
            $"a PutObjectAnnotation with no VersionId bound to {Py.Repr(putV1.ObjectVersionId)}, expected the version current at the time, {Py.Repr(v1)}");

        var v2 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{\"stage\":\"shipped\"}" })).VersionId;
        Ensure(v2 != v1, $"a second PutObject must mint a distinct version, got {Py.Repr(v2)} again");

        // The new version carries no annotation of its own yet: an unqualified Get and List must
        // not see the one written against v1.
        await ExpectError(() => S3Fidelity.GetAnnotation(s3, bucket, key, name), NoSuchAnnotation);
        var listedLatestBefore = await S3Fidelity.ListAnnotations(s3, bucket, key);
        Ensure(
            listedLatestBefore.AnnotationCount == 0,
            $"the new version should list no annotations yet, got {listedLatestBefore.AnnotationCount}");

        // The control: reading v1 explicitly still finds it, on both Get and List.
        var controlPayloadV1 = await GetPayload(s3, bucket, key, name, v1);
        Ensure(
            controlPayloadV1.SequenceEqual(payloadV1),
            $"VersionId={Py.Repr(v1)} should still read {Py.Repr(payloadV1)}, got {Py.Repr(controlPayloadV1)}");
        var namesV1 = S3Fidelity.AnnotationNames(await List(s3, bucket, key, v1));
        Ensure(namesV1.SequenceEqual([name]), $"VersionId={Py.Repr(v1)} should list [{Py.Repr(name)}], got {Py.Repr(namesV1)}");

        // Write v2's own annotation of the same name, under the same name, explicitly scoped to v2.
        await s3.PutObjectAnnotationAsync(new PutObjectAnnotationRequest
        {
            BucketName = bucket,
            Key = key,
            AnnotationName = name,
            AnnotationPayload = new MemoryStream(payloadV2),
            VersionId = v2,
        });
        var unqualifiedPayload = await S3Fidelity.AnnotationPayload(s3, bucket, key, name);
        Ensure(
            unqualifiedPayload.SequenceEqual(payloadV2),
            $"an unqualified Get should now follow the current version and read {Py.Repr(payloadV2)}, got {Py.Repr(unqualifiedPayload)}");

        // Delete v1's annotation only; v2's own annotation of the same name must survive untouched.
        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest
        {
            BucketName = bucket,
            Key = key,
            AnnotationName = name,
            VersionId = v1,
        });

        await ExpectError(() => GetPayload(s3, bucket, key, name, v1), NoSuchAnnotation);
        var listedV1AfterDelete = await List(s3, bucket, key, v1);
        Ensure(
            listedV1AfterDelete.AnnotationCount == 0,
            $"VersionId={Py.Repr(v1)} should list no annotations after its delete, got {listedV1AfterDelete.AnnotationCount}");

        // The positive control: v2's annotation is untouched by a delete scoped to v1.
        var survivingPayload = await GetPayload(s3, bucket, key, name, v2);
        Ensure(
            survivingPayload.SequenceEqual(payloadV2),
            $"deleting v1's annotation must not touch v2's; expected {Py.Repr(payloadV2)}, got {Py.Repr(survivingPayload)}");
        var namesV2AfterDelete = S3Fidelity.AnnotationNames(await List(s3, bucket, key, v2));
        Ensure(
            namesV2AfterDelete.SequenceEqual([name]),
            $"VersionId={Py.Repr(v2)} should still list [{Py.Repr(name)}] after v1's delete, got {Py.Repr(namesV2AfterDelete)}");
    }

    /// <summary><c>s3.get_object_annotation(..., VersionId=...)["AnnotationPayload"].read()</c>.</summary>
    private static async Task<byte[]> GetPayload(IAmazonS3 s3, string bucket, string key, string name, string versionId)
    {
        using var response = await s3.GetObjectAnnotationAsync(new GetObjectAnnotationRequest
        {
            BucketName = bucket,
            Key = key,
            AnnotationName = name,
            VersionId = versionId,
        });
        using var memory = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memory);
        return memory.ToArray();
    }

    /// <summary><c>s3.list_object_annotations(Bucket=..., Key=..., VersionId=...)</c>.</summary>
    private static Task<ListObjectAnnotationsResponse> List(IAmazonS3 s3, string bucket, string key, string versionId) =>
        s3.ListObjectAnnotationsAsync(new ListObjectAnnotationsRequest { BucketName = bucket, Key = key, VersionId = versionId });

    /// <summary>
    /// <c>expect_error(fn, code, message)</c>: <paramref name="call"/> fails with exactly this code and
    /// message; an unexpected success fails the check.
    /// </summary>
    private static async Task ExpectError(Func<Task> call, (string Code, string Message) expected)
    {
        var error = await FidelityContext.CaptureError(call)
            ?? throw Fail($"expected ({Py.Repr(expected.Code)}, {Py.Repr(expected.Message)}) but the call succeeded");
        Ensure(error.Code == expected.Code, $"expected code {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
    }
}
