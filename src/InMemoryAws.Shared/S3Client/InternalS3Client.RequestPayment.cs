using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>Requester Pays: who pays for a bucket's requests (see <see cref="Authorize"/> for its enforcement).</summary>
internal sealed partial class InternalS3Client
{
    public override Task<PutBucketRequestPaymentOutput> PutBucketRequestPaymentAsync(PutBucketRequestPaymentRequest request, CancellationToken cancellationToken)
    {
        var payer = request.RequestPaymentConfiguration?.Payer;
        if (payer is not ("Requester" or "BucketOwner"))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            GetBucket(request.Bucket).Payer = payer;
            return Task.FromResult(new PutBucketRequestPaymentOutput());
        }
    }

    public override Task<GetBucketRequestPaymentOutput> GetBucketRequestPaymentAsync(GetBucketRequestPaymentRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            return Task.FromResult(new GetBucketRequestPaymentOutput { Payer = GetBucket(request.Bucket).Payer });
        }
    }
}
