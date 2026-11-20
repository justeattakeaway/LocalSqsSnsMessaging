using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Inventory configurations, kept as sent and answered back. The cloud never writes an inventory
/// report to the destination.
/// </summary>
internal sealed partial class InternalS3Client
{
    public override Task<PutBucketInventoryConfigurationOutput> PutBucketInventoryConfigurationAsync(PutBucketInventoryConfigurationRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.InventoryConfiguration ?? throw S3Errors.MalformedXml();
        if (configuration.Id is null || configuration.IsEnabled is null || configuration.IncludedObjectVersions is null
            || configuration.Schedule?.Frequency is null || configuration.Destination?.S3BucketDestination is not { Bucket: not null, Format: not null })
        {
            throw S3Errors.MalformedXml();
        }

        if (!string.Equals(configuration.Id, request.Id, StringComparison.Ordinal))
        {
            throw S3Errors.InvalidArgument("Configuration id does not match", "id", request.Id);
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.Inventory[configuration.Id] = configuration;
            return Task.FromResult(new PutBucketInventoryConfigurationOutput());
        }
    }

    public override Task<GetBucketInventoryConfigurationOutput> GetBucketInventoryConfigurationAsync(GetBucketInventoryConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketInventoryConfigurationOutput { InventoryConfiguration = Inventory(bucket, request.Id) });
        }
    }

    /// <summary>Lists the bucket's inventory configurations, by id; S3 answers up to 100 a page.</summary>
    public override Task<ListBucketInventoryConfigurationsOutput> ListBucketInventoryConfigurationsAsync(ListBucketInventoryConfigurationsRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var after = request.ContinuationToken;
            var page = bucket.Inventory.Values
                .Where(c => after is null || string.CompareOrdinal(c.Id, after) > 0)
                .Take(101)
                .ToList();
            var truncated = page.Count > 100;
            if (truncated)
            {
                page.RemoveAt(100);
            }

            return Task.FromResult(new ListBucketInventoryConfigurationsOutput
            {
                ContinuationToken = after,
                InventoryConfigurationList = page.Count == 0 ? null : page,
                IsTruncated = truncated,
                NextContinuationToken = truncated ? page[^1].Id : null,
            });
        }
    }

    /// <summary>Deletes an inventory configuration; one that isn't there is <c>NoSuchConfiguration</c>.</summary>
    public override Task<DeleteBucketInventoryConfigurationOutput> DeleteBucketInventoryConfigurationAsync(DeleteBucketInventoryConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            Inventory(bucket, request.Id);
            bucket.Inventory.Remove(request.Id!);
            return Task.FromResult(new DeleteBucketInventoryConfigurationOutput());
        }
    }

    private static InventoryConfiguration Inventory(S3Bucket bucket, string? id) =>
        id is not null && bucket.Inventory.TryGetValue(id, out var configuration)
            ? configuration
            : throw new S3Exception("NoSuchConfiguration", "The specified configuration does not exist.", HttpStatusCode.NotFound);
}
