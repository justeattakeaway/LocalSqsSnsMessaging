using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// The V1 S3 Metadata operations (Create, Get and DeleteBucketMetadataTableConfiguration), which S3 has
/// closed to accounts that never used them: each is refused with <c>V1APIsNotAllowed</c>, naming the V2
/// operation to use instead.
/// </summary>
internal sealed partial class InternalS3Client
{
    /// <summary>Refused: <c>V1APIsNotAllowed</c>, pointing at CreateBucketMetadataConfiguration.</summary>
    public override Task<CreateBucketMetadataTableConfigurationOutput> CreateBucketMetadataTableConfigurationAsync(
        CreateBucketMetadataTableConfigurationRequest request, CancellationToken cancellationToken) =>
        throw RefuseV1Metadata(request.Bucket, "CreateBucketMetadataTableConfiguration", "CreateBucketMetadataConfiguration");

    /// <summary>Refused: <c>V1APIsNotAllowed</c>, pointing at GetBucketMetadataConfiguration.</summary>
    public override Task<GetBucketMetadataTableConfigurationOutput> GetBucketMetadataTableConfigurationAsync(
        GetBucketMetadataTableConfigurationRequest request, CancellationToken cancellationToken) =>
        throw RefuseV1Metadata(request.Bucket, "GetBucketMetadataTableConfiguration", "GetBucketMetadataConfiguration");

    /// <summary>Refused: <c>V1APIsNotAllowed</c>, pointing at DeleteBucketMetadataConfiguration.</summary>
    public override Task<DeleteBucketMetadataTableConfigurationOutput> DeleteBucketMetadataTableConfigurationAsync(
        DeleteBucketMetadataTableConfigurationRequest request, CancellationToken cancellationToken) =>
        throw RefuseV1Metadata(request.Bucket, "DeleteBucketMetadataTableConfiguration", "DeleteBucketMetadataConfiguration");

    /// <summary>The bucket must exist (and admit the caller); then the V1 operation is refused.</summary>
    private S3Exception RefuseV1Metadata(string? bucketName, string v1Operation, string v2Operation)
    {
        lock (Store.Gate)
        {
            GetBucket(bucketName);
        }

        return new S3Exception(
            "V1APIsNotAllowed",
            $"The V1 {v1Operation} API operation isn't available for this account. Use the corresponding V2 {v2Operation} API operation instead.",
            HttpStatusCode.BadRequest);
    }
}
