using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// A bucket's attribute-based access control status (Put/GetBucketAbac). The cloud keeps the status;
/// it evaluates no identity policies, so the status changes no request's outcome.
/// </summary>
internal sealed partial class InternalS3Client
{
    /// <summary>Sets the status: <c>Enabled</c> or <c>Disabled</c>; anything else doesn't fit the schema (<c>MalformedXML</c>).</summary>
    public override Task<PutBucketAbacOutput> PutBucketAbacAsync(PutBucketAbacRequest request, CancellationToken cancellationToken)
    {
        var status = request.AbacStatus?.Status;
        if (status is not ("Enabled" or "Disabled"))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            GetBucket(request.Bucket).AbacStatus = status;
            return Task.FromResult(new PutBucketAbacOutput());
        }
    }

    /// <summary>The status: <c>Disabled</c> until it is set.</summary>
    public override Task<GetBucketAbacOutput> GetBucketAbacAsync(GetBucketAbacRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketAbacOutput { AbacStatus = new AbacStatus { Status = bucket.AbacStatus } });
        }
    }
}

/// <summary>What a bucket keeps for attribute-based access control.</summary>
internal sealed partial class S3Bucket
{
    /// <summary><c>Enabled</c> or <c>Disabled</c>.</summary>
    public string AbacStatus { get; set; } = "Disabled";
}
