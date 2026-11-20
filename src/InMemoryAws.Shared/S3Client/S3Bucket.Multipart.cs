namespace InMemoryAws.S3;

/// <summary>What a bucket keeps about its multipart uploads beyond those in progress.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>Uploads aborted, by upload id, whose abort S3 answers again as done.</summary>
    public HashSet<string> AbortedUploads { get; } = new(StringComparer.Ordinal);
}
