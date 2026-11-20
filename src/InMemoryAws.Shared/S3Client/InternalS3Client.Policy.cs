using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>Bucket policies and Block Public Access.</summary>
internal sealed partial class InternalS3Client
{
    /// <summary>
    /// Sets the bucket's policy, kept as the text given. A policy S3 can't read is
    /// <c>MalformedPolicy</c>, and a public one (see <see cref="ResourcePolicy.IsPublic"/>) is refused
    /// while the bucket's Block Public Access blocks public policies.
    /// </summary>
    public override Task<PutBucketPolicyOutput> PutBucketPolicyAsync(PutBucketPolicyRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var text = request.Policy ?? string.Empty;
            ValidateBucketPolicy(bucket, text);
            if (bucket.PublicAccessBlock?.BlockPublicPolicy == true
                && ResourcePolicy.Parse(ResourcePolicyKind.Bucket, text)?.IsPublic() == true)
            {
                throw S3Errors.AccessDenied(
                    $"User: {CallerArn} is not authorized to perform: s3:PutBucketPolicy on resource: \"{bucket.Arn}\" " +
                    "because public policies are prevented by the BlockPublicPolicy setting in S3 Block Public Access.");
            }

            bucket.Policy = text;
            return Task.FromResult(new PutBucketPolicyOutput());
        }
    }

    public override Task<GetBucketPolicyOutput> GetBucketPolicyAsync(GetBucketPolicyRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketPolicyOutput { Policy = bucket.Policy ?? throw NoSuchBucketPolicy(bucket) });
        }
    }

    public override Task<DeleteBucketPolicyOutput> DeleteBucketPolicyAsync(DeleteBucketPolicyRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.Policy = null;
            return Task.FromResult(new DeleteBucketPolicyOutput());
        }
    }

    /// <summary>
    /// Whether the bucket's policy is public. It describes the document, whatever Block Public Access
    /// currently enforces; a bucket with no policy is <c>NoSuchBucketPolicy</c>.
    /// </summary>
    public override Task<GetBucketPolicyStatusOutput> GetBucketPolicyStatusAsync(GetBucketPolicyStatusRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var policy = bucket.Policy ?? throw NoSuchBucketPolicy(bucket);
            return Task.FromResult(new GetBucketPolicyStatusOutput
            {
                PolicyStatus = new PolicyStatus { IsPublic = ResourcePolicy.Parse(ResourcePolicyKind.Bucket, policy)?.IsPublic() == true },
            });
        }
    }

    /// <summary>Sets the bucket's Block Public Access; a setting left out is <c>false</c>.</summary>
    public override Task<PutPublicAccessBlockOutput> PutPublicAccessBlockAsync(PutPublicAccessBlockRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.PublicAccessBlockConfiguration ?? throw S3Errors.MalformedXml();
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.PublicAccessBlock = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = configuration.BlockPublicAcls == true,
                IgnorePublicAcls = configuration.IgnorePublicAcls == true,
                BlockPublicPolicy = configuration.BlockPublicPolicy == true,
                RestrictPublicBuckets = configuration.RestrictPublicBuckets == true,
            };
            return Task.FromResult(new PutPublicAccessBlockOutput());
        }
    }

    public override Task<GetPublicAccessBlockOutput> GetPublicAccessBlockAsync(GetPublicAccessBlockRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var configuration = bucket.PublicAccessBlock
                ?? throw new S3Exception(
                    "NoSuchPublicAccessBlockConfiguration",
                    "The public access block configuration was not found",
                    HttpStatusCode.NotFound,
                    ("BucketName", bucket.Name));
            return Task.FromResult(new GetPublicAccessBlockOutput
            {
                PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
                {
                    BlockPublicAcls = configuration.BlockPublicAcls,
                    IgnorePublicAcls = configuration.IgnorePublicAcls,
                    BlockPublicPolicy = configuration.BlockPublicPolicy,
                    RestrictPublicBuckets = configuration.RestrictPublicBuckets,
                },
            });
        }
    }

    /// <summary>Removes the bucket's Block Public Access, which then blocks nothing.</summary>
    public override Task<DeletePublicAccessBlockOutput> DeletePublicAccessBlockAsync(DeletePublicAccessBlockRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.PublicAccessBlock = null;
            return Task.FromResult(new DeletePublicAccessBlockOutput());
        }
    }

    private static S3Exception NoSuchBucketPolicy(S3Bucket bucket) =>
        new("NoSuchBucketPolicy", "The bucket policy does not exist", HttpStatusCode.NotFound, ("BucketName", bucket.Name));

    private static S3Exception MalformedPolicy(string message) =>
        new("MalformedPolicy", message, HttpStatusCode.BadRequest);

    /// <summary>
    /// Refuses a policy document S3 can't use: not a JSON object, no statements, a statement without
    /// a valid <c>Effect</c>, an action outside S3, or a resource outside this bucket.
    /// </summary>
    private static void ValidateBucketPolicy(S3Bucket bucket, string text)
    {
        JsonObject? document;
        try
        {
            var trimmed = text.TrimStart();
            document = trimmed.Length > 0 && trimmed[0] == '{' ? JsonNode.Parse(text) as JsonObject : null;
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null)
        {
            throw MalformedPolicy("Policies must be valid JSON and the first byte must be '{'");
        }

        var statements = document["Statement"] switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject single => [single],
            _ => throw MalformedPolicy("Missing required field Statement"),
        };
        if (statements.Count == 0)
        {
            throw MalformedPolicy("Could not parse the policy: Statement is empty!");
        }

        foreach (var statement in statements)
        {
            var effect = (statement["Effect"] as JsonValue)?.ToString();
            if (effect is not ("Allow" or "Deny"))
            {
                throw MalformedPolicy(effect is null ? "Missing required field Effect" : $"Invalid effect: {effect}");
            }

            if (statement["Principal"] is null && statement["NotPrincipal"] is null)
            {
                throw MalformedPolicy("Missing required field Principal");
            }

            foreach (var action in StringsOf(statement["Action"] ?? statement["NotAction"]))
            {
                if (action != "*" && !action.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
                {
                    throw MalformedPolicy("Policy has invalid action");
                }
            }

            foreach (var resource in StringsOf(statement["Resource"] ?? statement["NotResource"]))
            {
                if (resource != "*"
                    && resource != bucket.Arn
                    && !resource.StartsWith(bucket.Arn + "/", StringComparison.Ordinal)
                    && !(resource.IndexOfAny(['*', '?']) >= 0 && resource.StartsWith("arn:aws:s3:::", StringComparison.Ordinal)))
                {
                    throw MalformedPolicy("Policy has invalid resource");
                }
            }
        }

        static IEnumerable<string> StringsOf(JsonNode? node) => node switch
        {
            JsonArray array => array.OfType<JsonValue>().Select(v => v.ToString()),
            JsonValue value => [value.ToString()],
            _ => [],
        };
    }
}
