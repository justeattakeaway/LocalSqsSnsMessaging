using System.Text.Json.Nodes;

namespace InMemoryAws;

/// <summary>
/// Whether a policy is public, as S3 Block Public Access and <c>GetBucketPolicyStatus</c> judge it.
/// </summary>
internal sealed partial class ResourcePolicy
{
    /// <summary>
    /// The condition keys that, held to a fixed value, keep a grant to everyone from being public:
    /// they tie the grant to known networks, accounts, organizations or resources. A grant held to
    /// any other key (<c>aws:Referer</c>, say, which any caller can send) is still public.
    /// </summary>
    private static readonly HashSet<string> FixingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "aws:SourceIp",
        "aws:SourceArn",
        "aws:SourceVpc",
        "aws:SourceVpce",
        "aws:SourceOwner",
        "aws:SourceAccount",
        "aws:SourceOrgID",
        "aws:userid",
        "aws:PrincipalArn",
        "aws:PrincipalAccount",
        "aws:PrincipalOrgID",
        "aws:PrincipalServiceName",
        "s3:DataAccessPointArn",
        "s3:DataAccessPointAccount",
    };

    /// <summary>The operators that hold a key to the values they list.</summary>
    private static readonly HashSet<string> FixingOperators = new(StringComparer.Ordinal)
    {
        "StringEquals",
        "StringEqualsIgnoreCase",
        "StringLike",
        "ArnEquals",
        "ArnLike",
        "IpAddress",
    };

    /// <summary>
    /// Whether the policy grants access to everyone: an <c>Allow</c> statement whose principal is
    /// <c>"*"</c> (or <c>{"AWS": "*"}</c>), or that excludes principals with <c>NotPrincipal</c>,
    /// and that no condition holds to fixed values of a key such as <c>aws:SourceIp</c>,
    /// <c>aws:SourceArn</c> or <c>aws:PrincipalAccount</c>.
    /// </summary>
    public bool IsPublic() => Statements().Any(IsPublicStatement);

    private static bool IsPublicStatement(JsonObject statement)
    {
        if (!string.Equals(AsString(statement["Effect"]), "Allow", StringComparison.Ordinal))
        {
            return false;
        }

        if (statement["NotPrincipal"] is null && !NamesEveryone(statement["Principal"]))
        {
            return false;
        }

        if (statement["Condition"] is not JsonObject condition)
        {
            return true;
        }

        foreach (var (op, block) in condition)
        {
            var name = op.EndsWith("IfExists", StringComparison.Ordinal) ? op[..^"IfExists".Length] : op;
            if (!FixingOperators.Contains(name) || block is not JsonObject keys)
            {
                continue;
            }

            foreach (var (key, values) in keys)
            {
                var listed = Values(values).ToList();
                if (FixingKeys.Contains(key) && listed.Count > 0 && listed.TrueForAll(v => FixesValue(name, v)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a condition value names a fixed value rather than a wildcard or a wide network.</summary>
    private static bool FixesValue(string op, string value)
    {
        if (op == "IpAddress")
        {
            var slash = value.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                return true;
            }

            // A network wider than /8 (IPv4) or /32 (IPv6) admits too much of the internet to fix anything.
            var isV6 = value.Contains(':', StringComparison.Ordinal);
            return int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bits)
                && bits >= (isV6 ? 32 : 8);
        }

        return value.IndexOfAny(Wildcards) < 0;
    }
}
