// Translated from behavioral/dynamodb/check_execute_transaction_read_responses_three_way_shape_and_write_consumed_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-read-responses-three-way-shape-and-write-consumed-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionReadResponsesThreeWayShapeAndWriteConsumedCapacity
{
    private const string CheckId = "execute-transaction-read-responses-three-way-shape-and-write-consumed-capacity";

    /// <summary>
    /// A read-only ExecuteTransaction returns one Responses entry per SELECT, in order: {Item:
    /// {...}} found with the attribute, {Item: {}} found without it, {} when the key is absent. A
    /// write-only ExecuteTransaction returns Responses == [] and one ConsumedCapacity entry of 2 units per write.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-read-responses-three-way-shape-and-write-consumed-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("present")), ("attr", S("hit"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("sparse"))));
        // sk "absent" is never written.

        ParameterizedStatement Select(string sk) => new()
        {
            Statement = $"SELECT attr FROM \"{table}\" WHERE pk=? AND sk=?",
            Parameters = [S("p1"), S(sk)],
        };

        var readResp = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = [Select("present"), Select("sparse"), Select("absent")],
        });
        var responses = Responses(readResp.Responses)!;
        Ensure(responses.Count == 3, $"expected 3 per-statement responses, got {responses.Count}: {Py.Repr(responses)}");
        Ensure(
            Py.Equal(responses[0], new Dictionary<string, object?> { ["Item"] = Item(("attr", S("hit"))) }),
            $"expected the existing item's projected attribute, got {Py.Repr(responses[0])}");
        Ensure(
            Py.Equal(responses[1], new Dictionary<string, object?> { ["Item"] = Item() }),
            $"expected an existing item lacking the projected attribute to answer an empty Item, got {Py.Repr(responses[1])}");
        Ensure(
            responses[2].Count == 0,
            $"expected a non-existent key to answer with no Item key at all, got {Py.Repr(responses[2])}");

        var insertedKey = Item(("pk", S("p1")), ("sk", S("inserted")));
        var writeResp = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"INSERT INTO \"{table}\" VALUE {{'pk': ?, 'sk': ?}}", Parameters = [S("p1"), S("inserted")] },
                new ParameterizedStatement { Statement = $"DELETE FROM \"{table}\" WHERE pk=? AND sk=?", Parameters = [S("p1"), S("sparse")] },
            ],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(writeResp.Responses is { Count: 0 }, $"expected a write transaction to answer Responses == [], got {Py.Repr(Responses(writeResp.Responses))}");
        var consumed = writeResp.ConsumedCapacity;
        Ensure(consumed.Count == 1, $"expected exactly one ConsumedCapacity entry, got {consumed.Count}: {Py.Repr(consumed.Select(Repr).ToList())}");
        Ensure(consumed[0].TableName == table, $"expected the ConsumedCapacity entry to name the table, got {Py.Repr(Repr(consumed[0]))}");
        Ensure(
            consumed[0].CapacityUnits == 4.0,
            $"a transactional INSERT plus DELETE of two small items must bill 2.0 units each, 4.0 in all, got {Py.Repr(Repr(consumed[0]))}");

        // Prove the write transaction's effects with independent reads.
        var inserted = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = insertedKey, ConsistentRead = true });
        Ensure(Py.Equal(inserted.Item, insertedKey), $"expected the INSERTed item to read back, got {Py.Repr(inserted.Item)}");
        var deleted = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1")), ("sk", S("sparse"))), ConsistentRead = true });
        Ensure(deleted.Item is null, $"expected the DELETEd item to be gone, got {Py.Repr(deleted.Item)}");
    }

    /// <summary>The Responses list as boto3 hands it back: <c>{'Item': ...}</c>, or <c>{}</c> with no Item key.</summary>
    private static List<Dictionary<string, object?>>? Responses(List<ItemResponse>? responses) =>
        responses?.Select(r => r.Item is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["Item"] = r.Item }).ToList();

    /// <summary>The ConsumedCapacity entry as boto3 hands it back: only the members present.</summary>
    private static Dictionary<string, object?> Repr(ConsumedCapacity c)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            dict["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is { } units)
        {
            dict["CapacityUnits"] = units;
        }

        return dict;
    }
}
