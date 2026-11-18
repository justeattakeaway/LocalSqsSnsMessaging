using System.Text.Json;
using InMemoryAws.DynamoDb.Model;
using InMemoryAws.Http.Handlers;

namespace InMemoryAws.DynamoDb;

/// <summary>
/// The refusals of a request's shape that DynamoDB words its own way rather than as its model's
/// generic constraint clauses. The generated dispatcher calls <c>Check</c> after collecting the
/// model's violations and before throwing them.
/// </summary>
/// <remarks>
/// The keyed reads and the transactions (Query, BatchGetItem, TransactGetItems,
/// TransactWriteItems) state a collection or number past its bound in the item-request form: the
/// member by its wire name and no value (<c>Value at 'Limit' failed to satisfy constraint: ...</c>),
/// and such a refusal is the only one the call reports.
/// </remarks>
internal static class DynamoDbRequestChecks
{
    private const string TableNameKeys =
        "Map keys must satisfy constraint: [Member must have length less than or equal to 255, "
        + "Member must have length greater than or equal to 3, Member must satisfy regular expression pattern: [a-zA-Z0-9_.-]+]";

    /// <summary>The value set DynamoDB states when it checks <c>ReturnValues</c> itself, in its own order and spelling.</summary>
    private const string ReturnValuesRequirement =
        "Failed to satisfy constraint: Member must satisfy enum value set: [ALL_OLD, UPDATED_OLD, ALL_NEW, UPDATE_NEW, NONE]";

    /// <summary>Operations DynamoDB words no differently from its model.</summary>
    public static void Check(object request, List<string> violations)
    {
        _ = request;
        _ = violations;
    }

    public static void Check(PutItemRequest request, List<string> violations)
    {
        // An attribute name past its length is refused in DynamoDB's own words, not as the item map's key constraint.
        var tooLong = violations.FindIndex(v => v.EndsWith("' at 'item' failed to satisfy constraint: Map keys must satisfy constraint: [Member must have length less than or equal to 65535]", StringComparison.Ordinal));
        if (tooLong >= 0)
        {
            violations[tooLong] = "Attribute name is too large, must be less than 65536 bytes";
        }

        CheckReturnValues(request.ReturnValues, violations);
    }

    public static void Check(CreateTableRequest request, List<string> violations) => StateAttributeTypes(violations);

    public static void Check(UpdateTableRequest request, List<string> violations) => StateAttributeTypes(violations);

    public static void Check(UpdateItemRequest request, List<string> violations) => CheckReturnValues(request.ReturnValues, violations);

    /// <summary>SearchVectors states a <c>ReturnConsumedCapacity</c> outside its enum bare, not in the validation envelope.</summary>
    public static void Check(SearchVectorsRequest request, List<string> violations)
    {
        var returnConsumedCapacity = violations.Find(v => v.Contains("' at 'returnConsumedCapacity' failed to satisfy constraint", StringComparison.Ordinal));
        if (returnConsumedCapacity is not null)
        {
            throw DynamoDbErrors.Validation(returnConsumedCapacity);
        }

        _ = request;
    }

    public static void Check(DeleteItemRequest request, List<string> violations) => CheckReturnValues(request.ReturnValues, violations);

    public static void Check(QueryRequest request, List<string> violations)
    {
        var clauses = new List<string>();
        if (request.AttributesToGet is { Count: 0 })
        {
            clauses.Add(WireClause("AttributesToGet", "Member must have length greater than or equal to 1"));
        }

        if (request.Limit is < 1)
        {
            clauses.Add(WireClause("Limit", "Member must have value greater than or equal to 1"));
        }

        AwsValidation.ThrowIfAny(clauses);
        _ = violations;
    }

    public static void Check(BatchGetItemRequest request, List<string> violations)
    {
        if (request.RequestItems is not { } requestItems)
        {
            return;
        }

        var clauses = new List<string>();
        if (requestItems.Count < 1)
        {
            clauses.Add(WireClause("RequestItems", "Member must have length greater than or equal to 1"));
        }
        else if (requestItems.Count > 100)
        {
            clauses.Add(WireClause("RequestItems", "Member must have length less than or equal to 100"));
        }

        foreach (var (table, keys) in requestItems)
        {
            if (keys.Keys is { Count: < 1 or > 100 } list)
            {
                clauses.Add(WireClause($"RequestItems.{table}.member.Keys", list.Count < 1
                    ? "Member must have length greater than or equal to 1"
                    : "Member must have length less than or equal to 100"));
            }

            if (keys.AttributesToGet is { Count: 0 })
            {
                clauses.Add(WireClause($"RequestItems.{table}.member.AttributesToGet", "Member must have length greater than or equal to 1"));
            }
        }

        AwsValidation.ThrowIfAny(clauses);

        // A key that is no table name is checked against the table name's own constraints,
        // and BatchGetItem echoes the request map as the JSON it arrived as.
        if (requestItems.Keys.Any(IsNotTableName))
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                DynamoDbJsonSerializers.Write_BatchGetRequestMap(writer, requestItems);
            }

            violations.Add(AwsValidation.Clause(Encoding.UTF8.GetString(buffer.ToArray()), "requestItems", TableNameKeys));
        }
    }

    public static void Check(BatchWriteItemRequest request, List<string> violations)
    {
        if (request.RequestItems is not { } requestItems)
        {
            return;
        }

        if (requestItems.Count == 0)
        {
            throw DynamoDbErrors.Validation("The requestItems parameter is required for BatchWriteItem");
        }

        if (requestItems.Count > 25)
        {
            // BatchWriteItem names its map by the validator's path, but echoes no value either.
            AwsValidation.ThrowIfAny(["Value at 'requestItems' failed to satisfy constraint: Member must have length less than or equal to 25"]);
        }

        if (requestItems.Keys.Any(IsNotTableName))
        {
            violations.Insert(0, AwsValidation.Clause(JavaRendering.Map(requestItems, item => JavaRendering.List(item, i => i.ToString())), "requestItems", TableNameKeys));
        }
    }

    public static void Check(TransactGetItemsRequest request, List<string> violations)
    {
        var clauses = new List<string>();
        if (request.TransactItems is { Count: < 1 or > 100 } items)
        {
            clauses.Add(WireClause("TransactItems", items.Count < 1
                ? "Member must have length greater than or equal to 1"
                : "Member must have length less than or equal to 100"));
        }

        AwsValidation.ThrowIfAny(clauses);
        _ = violations;
    }

    public static void Check(TransactWriteItemsRequest request, List<string> violations)
    {
        var clauses = new List<string>();
        if (request.TransactItems is { Count: < 1 or > 100 } items)
        {
            clauses.Add(WireClause("TransactItems", items.Count < 1
                ? "Member must have length greater than or equal to 1"
                : "Member must have length less than or equal to 100"));
        }

        if (request.ClientRequestToken is { Length: < 1 or > 36 } token)
        {
            clauses.Add(WireClause("ClientRequestToken", token.Length < 1
                ? "Member must have length greater than or equal to 1"
                : "Member must have length less than or equal to 36"));
        }

        AwsValidation.ThrowIfAny(clauses);

        if (request.TransactItems?.Any(i => i.ConditionCheck is null && i.Put is null && i.Delete is null && i.Update is null) == true)
        {
            violations.Insert(0, AwsValidation.Clause(string.Empty, "transactItems", "TransactWriteRequest should contain Delete or Put or Update or ConditionCheck"));
        }
    }

    /// <summary>
    /// <c>ReturnValues</c> is checked by DynamoDB itself: alone, its refusal names neither the value
    /// nor the member, and states the set in DynamoDB's own order and spelling.
    /// </summary>
    private static void CheckReturnValues(string? returnValues, List<string> violations)
    {
        if (returnValues is not null
            && violations.Count == 1
            && violations[0].StartsWith($"Value '{returnValues}' at 'returnValues' ", StringComparison.Ordinal))
        {
            violations[0] = ReturnValuesRequirement;
        }
    }

    /// <summary>DynamoDB states the attribute types in an order of its own, not the model's.</summary>
    private static void StateAttributeTypes(List<string> violations)
    {
        for (var i = 0; i < violations.Count; i++)
        {
            if (violations[i].EndsWith(".attributeType' failed to satisfy constraint: Member must satisfy enum value set: [S, N, B]", StringComparison.Ordinal))
            {
                violations[i] = violations[i][..^"[S, N, B]".Length] + "[B, N, S]";
            }
        }
    }

    private static bool IsNotTableName(string key) =>
        !key.StartsWith("arn:", StringComparison.Ordinal)
        && (key.Length is < 3 or > 255 || key.Any(c => !(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-')));

    private static string WireClause(string member, string requirement) =>
        $"Value at '{member}' failed to satisfy constraint: {requirement}";
}
