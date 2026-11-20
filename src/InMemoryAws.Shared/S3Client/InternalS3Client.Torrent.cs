using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    /// <summary>
    /// S3 has withdrawn BitTorrent delivery: GetObjectTorrent on an object in an existing bucket is
    /// <c>MethodNotAllowed</c>, and the object is untouched.
    /// </summary>
    public override Task<GetObjectTorrentOutput> GetObjectTorrentAsync(GetObjectTorrentRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            GetBucket(request.Bucket);
            CheckKey(request.Key);
            throw S3Errors.MethodNotAllowed("GET", "OBJECT");
        }
    }
}
