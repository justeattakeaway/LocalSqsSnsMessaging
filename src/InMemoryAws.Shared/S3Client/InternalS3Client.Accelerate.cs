using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>Transfer Acceleration: kept and answered; requests are served the same with it on or off.</summary>
internal sealed partial class InternalS3Client
{
    /// <summary>
    /// Sets the bucket's Transfer Acceleration status, <c>Enabled</c> or <c>Suspended</c>. A bucket
    /// whose name has a period can't be accelerated.
    /// </summary>
    public override Task<PutBucketAccelerateConfigurationOutput> PutBucketAccelerateConfigurationAsync(PutBucketAccelerateConfigurationRequest request, CancellationToken cancellationToken)
    {
        var status = request.AccelerateConfiguration?.Status;
        if (status is not ("Enabled" or "Suspended"))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (bucket.Name.Contains('.', StringComparison.Ordinal))
            {
                throw S3Errors.InvalidRequest("S3 Transfer Acceleration is not supported for buckets with periods (.) in their names");
            }

            bucket.AccelerateStatus = status;
            return Task.FromResult(new PutBucketAccelerateConfigurationOutput());
        }
    }

    /// <summary>The bucket's Transfer Acceleration status; none for a bucket where it was never set.</summary>
    public override Task<GetBucketAccelerateConfigurationOutput> GetBucketAccelerateConfigurationAsync(GetBucketAccelerateConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            return Task.FromResult(new GetBucketAccelerateConfigurationOutput { Status = GetBucket(request.Bucket).AccelerateStatus });
        }
    }
}

/// <summary>A bucket's Transfer Acceleration status.</summary>
internal sealed partial class S3Bucket
{
    /// <summary><c>Enabled</c> or <c>Suspended</c> once set; null for a bucket where it was never set.</summary>
    public string? AccelerateStatus { get; set; }
}
