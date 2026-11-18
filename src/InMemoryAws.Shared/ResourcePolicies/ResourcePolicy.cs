using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InMemoryAws;

/// <summary>
/// A resource policy: the IAM policy document an SQS queue, SNS topic, EventBridge event bus or
/// DynamoDB table holds as JSON text, how its service edits and stores it, and the parts of IAM
/// evaluation the library enforces (<see cref="DeniesExplicitly"/>, <see cref="GrantsToEveryone"/>,
/// <see cref="EvaluateServiceDelivery"/>). What differs between the services is its
/// <see cref="ResourcePolicyKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// A value is immutable: an edit (<see cref="WithStatement"/>, <see cref="WithoutStatement"/>)
/// returns a new policy, and the service stores its <see cref="ToJson"/>.
/// </para>
/// <para>
/// IAM lets <c>Statement</c> be one statement object or a list of them; every operation here reads
/// both, and an edit writes a list. A document without statements has none, and one whose
/// <c>Statement</c> is neither shape is read as having none.
/// </para>
/// </remarks>
internal sealed partial class ResourcePolicy
{
    /// <summary>Parsed documents, keyed by the text they were parsed from. A cached document is never changed.</summary>
    private static readonly ConditionalWeakTable<string, JsonObject?> ParsedDocuments = new();

    private readonly JsonObject _document;

    private ResourcePolicy(ResourcePolicyKind kind, JsonObject document)
    {
        Kind = kind;
        _document = document;
    }

    public ResourcePolicyKind Kind { get; }

    /// <summary>
    /// The policy <paramref name="json"/> holds, or <see langword="null"/> when there is none: no
    /// text, or text that is not a JSON object (which no statement can be read from).
    /// </summary>
    public static ResourcePolicy? Parse(ResourcePolicyKind kind, string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var document = ParsedDocuments.GetValue(json!, static text =>
        {
            try
            {
                var parsed = JsonNode.Parse(text) as JsonObject;
                Materialize(parsed);
                return parsed;
            }
            catch (JsonException)
            {
                return null;
            }
        });
        return document is null ? null : new ResourcePolicy(kind, document);
    }

    /// <summary>
    /// The document the service writes from scratch for <paramref name="resourceArn"/>, before its
    /// first statement: its <see cref="ResourcePolicyKind.Version"/>, its <c>Id</c> if it gives one,
    /// and no statements.
    /// </summary>
    public static ResourcePolicy Empty(ResourcePolicyKind kind, string resourceArn)
    {
        var document = new JsonObject { ["Version"] = kind.Version };
        if (kind.DocumentId(resourceArn) is { } id)
        {
            document["Id"] = id;
        }

        document["Statement"] = new JsonArray();
        return new ResourcePolicy(kind, document);
    }

    /// <summary>The policy <paramref name="json"/> holds, or the <see cref="Empty"/> one when it holds none.</summary>
    public static ResourcePolicy ParseOrEmpty(ResourcePolicyKind kind, string? json, string resourceArn) =>
        Parse(kind, json) ?? Empty(kind, resourceArn);

    /// <summary>How many statements the policy has.</summary>
    public int StatementCount => Statements().Count();

    /// <summary>Whether a statement has the <c>Sid</c> <paramref name="sid"/>.</summary>
    public bool HasStatement(string? sid) => Statements().Any(s => IsStatement(s, sid));

    /// <summary>
    /// This policy with <paramref name="statement"/> in place of the statement with the same
    /// <c>Sid</c>, or after the others when there is none. Whether a duplicate <c>Sid</c> is an
    /// error is the service's to decide, with <see cref="HasStatement"/>.
    /// </summary>
    public ResourcePolicy WithStatement(JsonObject statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var sid = StatementId(statement);
        var (document, statements) = EditableCopy();
        var added = (JsonObject)statement.DeepClone();
        var index = IndexOf(statements, sid);
        if (index >= 0)
        {
            statements[index] = added;
        }
        else
        {
            statements.Add((JsonNode)added);
        }

        return new ResourcePolicy(Kind, document);
    }

    /// <summary>
    /// This policy without the statement whose <c>Sid</c> is <paramref name="sid"/>; the same
    /// policy when it has none. A policy left with no statements is still a policy: whether that
    /// removes it is the service's to decide, with <see cref="StatementCount"/>.
    /// </summary>
    public ResourcePolicy WithoutStatement(string? sid)
    {
        if (!HasStatement(sid))
        {
            return this;
        }

        var (document, statements) = EditableCopy();
        statements.RemoveAt(IndexOf(statements, sid));
        return new ResourcePolicy(Kind, document);
    }

    /// <summary>The document as the service stores and returns it.</summary>
    public string ToJson() => _document.ToJsonString();

    public override string ToString() => ToJson();

    /// <summary>
    /// <paramref name="json"/> as the service stores it. SQS stores an <c>AWS</c> principal given
    /// as a bare account id as that account's root ARN
    /// (<see cref="ResourcePolicyKind.StoresAccountPrincipalsAsRootArns"/>); other services, and a
    /// value that is not a JSON object, are stored unchanged, byte for byte.
    /// </summary>
    public static string Normalize(ResourcePolicyKind kind, string json)
    {
        if (!kind.StoresAccountPrincipalsAsRootArns || Parse(kind, json) is not { } policy)
        {
            return json;
        }

        var document = policy._document.DeepClone().AsObject();
        var changed = false;
        foreach (var statement in StatementsOf(document))
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

        return changed ? document.ToJsonString() : json;

        static string? RootArn(JsonNode? node) =>
            AsString(node) is { } value && AwsAccountId.IsValid(value) ? $"arn:aws:iam::{value}:root" : null;
    }

    /// <summary>
    /// Whether two documents are the same policy, whatever their layout; text that is not JSON is
    /// compared as it is. A queue's policies may also differ in their statements' <c>Sid</c>s
    /// (<see cref="ResourcePolicyKind.ComparesWithoutStatementIds"/>).
    /// </summary>
    public static bool AreEquivalent(ResourcePolicyKind kind, string a, string? b)
    {
        JsonNode? first, second;
        try
        {
            first = JsonNode.Parse(a);
            second = JsonNode.Parse(b ?? string.Empty);
        }
        catch (JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        if (kind.ComparesWithoutStatementIds && first is JsonObject x && second is JsonObject y)
        {
            first = WithoutStatementIds(x);
            second = WithoutStatementIds(y);
        }

        return JsonNode.DeepEquals(first, second);

        static JsonObject WithoutStatementIds(JsonObject document)
        {
            foreach (var statement in StatementsOf(document))
            {
                statement.Remove("Sid");
            }

            return document;
        }
    }

    /// <summary>
    /// Builds every object and list of a parsed document now. A parsed node builds its children on
    /// first read, which is not safe on several threads at once, and a cached document is read by many.
    /// </summary>
    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    Materialize(property.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Materialize(item);
                }

                break;
        }
    }

    private IEnumerable<JsonObject> Statements() => StatementsOf(_document);

    private static IEnumerable<JsonObject> StatementsOf(JsonObject document) =>
        document["Statement"] switch
        {
            JsonObject single => [single],
            JsonArray many => many.OfType<JsonObject>(),
            _ => [],
        };

    /// <summary>A copy of the document whose <c>Statement</c> is a list, in the place the original had it, for an edit.</summary>
    private (JsonObject Document, JsonArray Statements) EditableCopy()
    {
        var document = _document.DeepClone().AsObject();
        switch (document["Statement"])
        {
            case JsonArray array:
                return (document, array);
            case JsonObject single:
                // Detach the statement before it moves into the list that takes its place.
                document["Statement"] = null;
                var wrapped = new JsonArray(single);
                document["Statement"] = wrapped;
                return (document, wrapped);
            default:
                var empty = new JsonArray();
                document["Statement"] = empty;
                return (document, empty);
        }
    }

    private static int IndexOf(JsonArray statements, string? sid)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i] is JsonObject statement && IsStatement(statement, sid))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsStatement(JsonObject statement, string? sid) =>
        string.Equals(StatementId(statement), sid, StringComparison.Ordinal);

    private static string? StatementId(JsonObject statement) =>
        statement["Sid"] is JsonValue sid && sid.GetValueKind() == JsonValueKind.String ? sid.GetValue<string>() : null;
}
