using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>A bucket's default encryption, and the encryption types it blocks.</summary>
internal sealed partial class InternalS3Client
{
    private static readonly string[] SseAlgorithms = ["AES256", "aws:kms", "aws:kms:dsse"];

    /// <summary>
    /// Sets the bucket's default encryption. A rule that names no <c>BlockedEncryptionTypes</c> keeps
    /// the bucket's current block on SSE-C uploads.
    /// </summary>
    public override Task<PutBucketEncryptionOutput> PutBucketEncryptionAsync(PutBucketEncryptionRequest request, CancellationToken cancellationToken)
    {
        var rules = request.ServerSideEncryptionConfiguration?.Rules;
        if (rules is null || rules.Count == 0)
        {
            throw S3Errors.MalformedXml();
        }

        foreach (var rule in rules)
        {
            var algorithm = rule.ApplyServerSideEncryptionByDefault?.SSEAlgorithm;
            if (algorithm is not null && Array.IndexOf(SseAlgorithms, algorithm) < 0)
            {
                throw S3Errors.MalformedXml();
            }

            if (rule.ApplyServerSideEncryptionByDefault?.KMSMasterKeyID is not null && algorithm is not ("aws:kms" or "aws:kms:dsse"))
            {
                throw S3Errors.InvalidArgument("a KMSMasterKeyID is not applicable if the default sse algorithm is not aws:kms or aws:kms:dsse");
            }

            if (rule.BlockedEncryptionTypes?.EncryptionType is { } types && types.Exists(t => t is not ("SSE-C" or "NONE")))
            {
                throw S3Errors.MalformedXml();
            }
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var blocksSseC = bucket.BlocksSseC;
            bucket.Encryption = new ServerSideEncryptionConfiguration
            {
                Rules = [.. rules.Select(rule => new ServerSideEncryptionRule
                {
                    ApplyServerSideEncryptionByDefault = rule.ApplyServerSideEncryptionByDefault,
                    BucketKeyEnabled = rule.BucketKeyEnabled ?? false,
                    BlockedEncryptionTypes = rule.BlockedEncryptionTypes ?? BlockedTypes(blocksSseC),
                })],
            };
            return Task.FromResult(new PutBucketEncryptionOutput());
        }
    }

    /// <summary>The bucket's default encryption: S3's default (SSE-S3, SSE-C blocked) until one is set.</summary>
    public override Task<GetBucketEncryptionOutput> GetBucketEncryptionAsync(GetBucketEncryptionRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketEncryptionOutput
            {
                ServerSideEncryptionConfiguration = bucket.Encryption ?? new ServerSideEncryptionConfiguration
                {
                    Rules =
                    [
                        new ServerSideEncryptionRule
                        {
                            ApplyServerSideEncryptionByDefault = new ServerSideEncryptionByDefault { SSEAlgorithm = "AES256" },
                            BucketKeyEnabled = false,
                            BlockedEncryptionTypes = BlockedTypes(blocksSseC: true),
                        },
                    ],
                },
            });
        }
    }

    /// <summary>Returns the bucket to S3's default encryption.</summary>
    public override Task<DeleteBucketEncryptionOutput> DeleteBucketEncryptionAsync(DeleteBucketEncryptionRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.Encryption = null;
            return Task.FromResult(new DeleteBucketEncryptionOutput());
        }
    }

    /// <summary>The encryption a write that names none gets from the bucket's default; null for S3's default (SSE-S3).</summary>
    private static ServerSideEncryptionByDefault? DefaultEncryption(S3Bucket bucket) =>
        bucket.Encryption?.Rules?.FirstOrDefault()?.ApplyServerSideEncryptionByDefault;

    /// <summary>Whether the bucket's default encryption uses an S3 Bucket Key, for an SSE-KMS default; null otherwise.</summary>
    private static bool? DefaultBucketKey(S3Bucket bucket) =>
        DefaultEncryption(bucket)?.SSEAlgorithm is "aws:kms" or "aws:kms:dsse" ? bucket.Encryption!.Rules![0].BucketKeyEnabled : null;

    private static BlockedEncryptionTypes BlockedTypes(bool blocksSseC) =>
        new() { EncryptionType = [blocksSseC ? "SSE-C" : "NONE"] };
}
