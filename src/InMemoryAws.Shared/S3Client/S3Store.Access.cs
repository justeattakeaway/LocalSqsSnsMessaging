using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// A bucket's access settings (its policy and Block Public Access), and the configurations S3 acts on
/// for requests and writes: default encryption, requester pays, replication, website hosting and
/// inventory.
/// </summary>
internal sealed partial class S3Bucket
{
    /// <summary>The bucket policy, as the text PutBucketPolicy was given; null for none.</summary>
    public string? Policy { get; set; }

    /// <summary>
    /// The bucket's Block Public Access settings; null once DeletePublicAccessBlock removed them. A new
    /// bucket blocks all public access (S3's default since April 2023).
    /// </summary>
    public PublicAccessBlockConfiguration? PublicAccessBlock { get; set; } = new()
    {
        BlockPublicAcls = true,
        IgnorePublicAcls = true,
        BlockPublicPolicy = true,
        RestrictPublicBuckets = true,
    };

    /// <summary>
    /// The default encryption PutBucketEncryption set; null for S3's default (SSE-S3, with uploads
    /// that use SSE-C blocked, as S3 creates every new bucket since April 2026).
    /// </summary>
    public ServerSideEncryptionConfiguration? Encryption { get; set; }

    /// <summary><c>BucketOwner</c>, or <c>Requester</c> for a Requester Pays bucket.</summary>
    public string Payer { get; set; } = "BucketOwner";

    /// <summary>The replication configuration PutBucketReplication set; null for none.</summary>
    public ReplicationConfiguration? Replication { get; set; }

    /// <summary>The website configuration PutBucketWebsite set; null when the bucket is no website.</summary>
    public WebsiteConfiguration? Website { get; set; }

    /// <summary>The inventory configurations, by id.</summary>
    public SortedDictionary<string, InventoryConfiguration> Inventory { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the bucket's default encryption blocks uploads that use SSE-C.</summary>
    public bool BlocksSseC =>
        Encryption is null
        || Encryption.Rules?.Exists(r => r.BlockedEncryptionTypes?.EncryptionType?.Contains("SSE-C") == true) == true;
}

/// <summary>What replication records on an object version.</summary>
internal sealed partial class S3ObjectVersion
{
    /// <summary>
    /// <c>COMPLETED</c> or <c>FAILED</c> on a source version a replication rule selected, <c>REPLICA</c>
    /// on its copy in the destination; null for a version no rule selected.
    /// </summary>
    public string? ReplicationStatus { get; set; }
}
