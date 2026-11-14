using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// A queue's <c>Policy</c> attribute: how SQS stores it, and the parts of IAM policy evaluation the
/// library enforces. For callers, only explicit <c>Deny</c> statements are enforced; <c>Allow</c>
/// statements are never needed, because every caller acts for the account that owns the queues.
/// Deliveries by AWS services are evaluated by <see cref="AdmitsServiceDelivery"/>.
/// EventBridge evaluates event bus policies the same way (<see cref="RequestContext.ActionPrefix"/>).
/// </summary>
/// <remarks>
/// A statement is applied only when every part of it is understood. One whose condition uses an
/// operator or a shape the library does not evaluate is skipped, so an unsupported Deny never
/// blocks a request.
/// </remarks>
internal static partial class SqsQueuePolicy
{
    /// <summary>Parsed documents, keyed by the attribute string they were parsed from.</summary>
    private static readonly ConditionalWeakTable<string, JsonObject?> ParsedDocuments = new();

    private static readonly char[] Wildcards = ['*', '?'];

    private static readonly string[] PrincipalKeys = ["Principal", "NotPrincipal"];

    /// <summary>What the request is, as policy evaluation sees it.</summary>
    /// <param name="Action">The SQS action, without the <c>sqs:</c> prefix (e.g. <c>SendMessage</c>).</param>
    /// <param name="Resource">The queue's ARN.</param>
    /// <param name="PrincipalAccount">The caller's account, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="PrincipalArn">The caller's ARN, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="SourceIp">Where the request came from, when known.</param>
    /// <param name="SecureTransport">Whether the request came over TLS.</param>
    internal sealed record RequestContext(
        string Action,
        string Resource,
        string? PrincipalAccount,
        string? PrincipalArn,
        IPAddress? SourceIp,
        bool SecureTransport)
    {
        /// <summary>
        /// The service principal making the request on a resource's behalf (e.g. <c>sns.amazonaws.com</c>
        /// delivering a topic's notification), or <see langword="null"/> when an account or an
        /// anonymous caller makes it.
        /// </summary>
        public string? Service { get; init; }

        /// <summary>The resource a service acts for (<c>aws:SourceArn</c>), e.g. the publishing topic.</summary>
        public string? SourceArn { get; init; }

        /// <summary>The account that owns <see cref="SourceArn"/> (<c>aws:SourceAccount</c>).</summary>
        public string? SourceAccount { get; init; }

        /// <summary>The service prefix policies name the action with: <c>sqs</c>, or <c>events</c> for an event bus.</summary>
        public string ActionPrefix { get; init; } = "sqs";

        /// <summary>
        /// The request's service-specific condition keys (e.g. <c>events:source</c>), in a dictionary that
        /// ignores case as IAM does. A key that is neither here nor a global key the library models makes
        /// its statement unevaluable.
        /// </summary>
        public IReadOnlyDictionary<string, string?>? ServiceKeys { get; init; }

        public bool IsAnonymous => PrincipalAccount is null && Service is null;
    }

    /// <summary>
    /// The document as SQS stores it: an <c>AWS</c> principal given as a bare account id reads back
    /// as that account's root ARN. A value that is not a JSON object is stored unchanged.
    /// </summary>
    public static string Normalize(string policy)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(policy);
        }
        catch (JsonException)
        {
            return policy;
        }

        if (root is not JsonObject document)
        {
            return policy;
        }

        var changed = false;
        foreach (var statement in Statements(document))
        {
            foreach (var key in PrincipalKeys)
            {
                if (statement[key] is not JsonObject principal || principal["AWS"] is not { } aws)
                {
                    continue;
                }

                if (aws is JsonValue && RootArn(aws) is { } arn)
                {
                    principal["AWS"] = arn;
                    changed = true;
                }
                else if (aws is JsonArray array)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (RootArn(array[i]) is { } itemArn)
                        {
                            array[i] = itemArn;
                            changed = true;
                        }
                    }
                }
            }
        }

        return changed ? document.ToJsonString() : policy;

        static string? RootArn(JsonNode? node) =>
            AsString(node) is { } value && IsAccountId(value) ? $"arn:aws:iam::{value}:root" : null;
    }

    /// <summary>Whether a Deny statement in <paramref name="policy"/> applies to the request.</summary>
    public static bool DeniesExplicitly(string policy, RequestContext request) =>
        Parse(policy) is { } document
        && Statements(document).Any(statement =>
            string.Equals(AsString(statement["Effect"]), "Deny", StringComparison.Ordinal)
            && Applies(statement, request) == true);

    /// <summary>
    /// Whether an Allow statement in <paramref name="policy"/> grants the request to everyone
    /// (<c>"Principal": "*"</c>), which is the only way an anonymous request is allowed.
    /// </summary>
    public static bool GrantsToEveryone(string policy, RequestContext request) =>
        Parse(policy) is { } document
        && Statements(document).Any(statement =>
            string.Equals(AsString(statement["Effect"]), "Allow", StringComparison.Ordinal)
            && statement["NotPrincipal"] is null
            && NamesEveryone(statement["Principal"])
            && Applies(statement, request) == true);

    private static JsonObject? Parse(string policy) =>
        ParsedDocuments.GetValue(policy, static json =>
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        });

    private static IEnumerable<JsonObject> Statements(JsonObject document) =>
        document["Statement"] switch
        {
            JsonObject single => [single],
            JsonArray many => many.OfType<JsonObject>(),
            _ => [],
        };

    /// <summary>Whether the statement applies to the request; <see langword="null"/> when it cannot be evaluated.</summary>
    private static bool? Applies(JsonObject statement, RequestContext request)
    {
        var principal = statement["Principal"] is { } p
            ? MatchesPrincipal(p, request)
            : statement["NotPrincipal"] is { } np ? !MatchesPrincipal(np, request) : false;
        var action = statement["Action"] is { } a
            ? MatchesAny(a, $"{request.ActionPrefix}:{request.Action}", StringComparison.OrdinalIgnoreCase)
            : statement["NotAction"] is { } na ? !MatchesAny(na, $"{request.ActionPrefix}:{request.Action}", StringComparison.OrdinalIgnoreCase) : false;
        var resource = statement["Resource"] is { } r
            ? MatchesAny(r, request.Resource, StringComparison.Ordinal)
            : statement["NotResource"] is not { } nr || !MatchesAny(nr, request.Resource, StringComparison.Ordinal);
        if (!principal || !action || !resource)
        {
            return false;
        }

        return statement["Condition"] is { } condition ? EvaluateCondition(condition, request) : true;
    }

    private static bool NamesEveryone(JsonNode? principal) => principal switch
    {
        JsonValue => AsString(principal) == "*",
        JsonObject obj => Values(obj["AWS"]).Contains("*"),
        _ => false,
    };

    private static bool MatchesPrincipal(JsonNode principal, RequestContext request)
    {
        if (NamesEveryone(principal))
        {
            return true;
        }

        if (request.IsAnonymous || principal is not JsonObject obj)
        {
            return false;
        }

        if (request.Service is { } service)
        {
            return Values(obj["Service"]).Contains(service, StringComparer.Ordinal);
        }

        // Federated and canonical-user principals never make SQS API calls here.
        return Values(obj["AWS"]).Any(value =>
            IsAccountId(value)
                ? string.Equals(value, request.PrincipalAccount, StringComparison.Ordinal)
                : string.Equals(value, $"arn:aws:iam::{request.PrincipalAccount}:root", StringComparison.Ordinal)
                  || string.Equals(value, request.PrincipalArn, StringComparison.Ordinal));
    }

    private static bool MatchesAny(JsonNode patterns, string value, StringComparison comparison) =>
        Values(patterns).Any(pattern => Glob(pattern, value, comparison));

    private static bool? EvaluateCondition(JsonNode condition, RequestContext request)
    {
        if (condition is not JsonObject operators)
        {
            return null;
        }

        foreach (var (op, block) in operators)
        {
            if (block is not JsonObject keys)
            {
                return null;
            }

            foreach (var (key, expected) in keys)
            {
                if (!TryGetContextValue(key, request, out var actual))
                {
                    // A key the library does not model cannot be evaluated, so the statement is skipped.
                    return null;
                }

                var result = EvaluateOperator(op, actual, Values(expected).ToList());
                if (result != true)
                {
                    return result;
                }
            }
        }

        return true;
    }

    /// <summary>One condition operator against one context key; <see langword="null"/> for an operator not supported.</summary>
    private static bool? EvaluateOperator(string op, string? actual, List<string> expected)
    {
        var ifExists = op.EndsWith("IfExists", StringComparison.Ordinal);
        var name = ifExists ? op[..^"IfExists".Length] : op;
        if (name.Contains(':', StringComparison.Ordinal))
        {
            // ForAnyValue: / ForAllValues: set operators.
            return null;
        }

        Func<string, string, bool>? match;
        var negated = false;
        switch (name)
        {
            case "StringEquals":
                match = static (e, a) => string.Equals(e, a, StringComparison.Ordinal);
                break;
            case "StringNotEquals":
                match = static (e, a) => string.Equals(e, a, StringComparison.Ordinal);
                negated = true;
                break;
            case "StringEqualsIgnoreCase":
                match = static (e, a) => string.Equals(e, a, StringComparison.OrdinalIgnoreCase);
                break;
            case "StringNotEqualsIgnoreCase":
                match = static (e, a) => string.Equals(e, a, StringComparison.OrdinalIgnoreCase);
                negated = true;
                break;
            case "StringLike" or "ArnEquals" or "ArnLike":
                match = static (e, a) => Glob(e, a, StringComparison.Ordinal);
                break;
            case "StringNotLike" or "ArnNotEquals" or "ArnNotLike":
                match = static (e, a) => Glob(e, a, StringComparison.Ordinal);
                negated = true;
                break;
            case "Bool":
                match = static (e, a) => string.Equals(e, a, StringComparison.OrdinalIgnoreCase);
                break;
            case "IpAddress":
                match = static (e, a) => InCidr(e, a) ?? false;
                break;
            case "NotIpAddress":
                match = static (e, a) => InCidr(e, a) ?? false;
                negated = true;
                break;
            case "Null" when !ifExists:
                return expected.Count == 1 && bool.TryParse(expected[0], out var mustBeAbsent)
                    ? mustBeAbsent == (actual is null)
                    : null;
            default:
                return null;
        }

        if (actual is null)
        {
            // An absent key fails a positive operator and satisfies a negated one.
            return ifExists || negated;
        }

        var any = expected.Exists(e => match(e, actual));
        return negated ? !any : any;
    }

    /// <summary>
    /// The request's value for a global condition key (<see langword="null"/> when the request has none);
    /// <see langword="false"/> when the library does not model the key at all.
    /// </summary>
    private static bool TryGetContextValue(string key, RequestContext request, out string? value)
    {
        switch (key.ToUpperInvariant())
        {
            case "AWS:SOURCEIP":
                value = request.SourceIp?.ToString();
                return true;
            case "AWS:SECURETRANSPORT":
                value = request.SecureTransport ? "true" : "false";
                return true;
            case "AWS:PRINCIPALACCOUNT":
                value = request.PrincipalAccount;
                return true;
            case "AWS:PRINCIPALARN":
                value = request.PrincipalArn;
                return true;
            case "AWS:PRINCIPALTYPE":
                // A service principal, such as SNS delivering a notification, has no principal type.
                value = request.Service is not null ? null : request.IsAnonymous ? "Anonymous" : "Account";
                return true;
            case "AWS:PRINCIPALSERVICENAME":
                value = request.Service;
                return true;
            case "AWS:PRINCIPALISAWSSERVICE":
                value = request.Service is not null ? "true" : "false";
                return true;
            case "AWS:SOURCEARN":
                value = request.SourceArn;
                return true;
            case "AWS:SOURCEACCOUNT":
            case "AWS:SOURCEOWNER":
                value = request.SourceAccount;
                return true;
            case "AWS:USERID":
                value = request.IsAnonymous ? "anonymous" : request.PrincipalAccount;
                return true;
            default:
                value = null;
                return request.ServiceKeys?.TryGetValue(key, out value) == true;
        }
    }

    /// <summary>Whether <paramref name="address"/> is within <paramref name="cidr"/>; <see langword="null"/> when either is malformed.</summary>
    internal static bool? InCidr(string cidr, string address)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        if (!IPAddress.TryParse(slash < 0 ? cidr : cidr[..slash], out var network)
            || !IPAddress.TryParse(address, out var ip))
        {
            return null;
        }

        network = Canonical(network);
        ip = Canonical(ip);
        var networkBytes = network.GetAddressBytes();
        var ipBytes = ip.GetAddressBytes();
        var maxBits = networkBytes.Length * 8;
        var bits = maxBits;
        if (slash >= 0
            && (!int.TryParse(cidr[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bits)
                || bits > maxBits))
        {
            return null;
        }

        if (networkBytes.Length != ipBytes.Length)
        {
            return false;
        }

        for (var i = 0; i < networkBytes.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((networkBytes[i] & mask) != (ipBytes[i] & mask))
            {
                return false;
            }
        }

        return true;

        static IPAddress Canonical(IPAddress value) => value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
    }

    /// <summary>IAM wildcard matching: <c>*</c> matches any run of characters, <c>?</c> any one.</summary>
    internal static bool Glob(string pattern, string value, StringComparison comparison)
    {
        if (pattern.IndexOfAny(Wildcards) < 0)
        {
            return string.Equals(pattern, value, comparison);
        }

        var ignoreCase = comparison == StringComparison.OrdinalIgnoreCase;
        int p = 0, v = 0, star = -1, resume = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], value[v], ignoreCase)))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;

        static bool CharEquals(char a, char b, bool ignoreCase) =>
            a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
    }

    /// <summary>A policy element that may be one value or a list of them, as strings.</summary>
    private static IEnumerable<string> Values(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(AsString).OfType<string>(),
        JsonValue => AsString(node) is { } single ? [single] : [],
        _ => [],
    };

    /// <summary>A JSON scalar as the string IAM compares: strings as they are, booleans and numbers as written.</summary>
    private static string? AsString(JsonNode? node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        }
        : null;

    private static bool IsAccountId(string value) => value.Length == 12 && value.All(c => c is >= '0' and <= '9');
}
