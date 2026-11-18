// Translated from behavioral/dynamodb/check_execute_transaction_read_response_shapes_and_write_consumed_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-read-response-shapes-and-write-consumed-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionReadResponseShapesAndWriteConsumedCapacity
{
    private const string CheckId = "execute-transaction-read-response-shapes-and-write-consumed-capacity";

    /// <summary>
    /// ExecuteTransaction's Responses entries are a full item, an empty Item, or no Item key
    /// for a hit, a hit missing the projected attribute, and a miss, respectively. A write
    /// transaction returns an empty Responses list and one ConsumedCapacity entry per table.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ExecuteTransaction.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-read-response-shapes-and-write-consumed-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s1")), ("attr", S("v1"))));
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s2"))));

        var read = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"SELECT attr FROM \"{t}\" WHERE pk='p1' AND sk='s1'" },
                new ParameterizedStatement { Statement = $"SELECT attr FROM \"{t}\" WHERE pk='p1' AND sk='s2'" },
                new ParameterizedStatement { Statement = $"SELECT attr FROM \"{t}\" WHERE pk='p1' AND sk='s3'" },
            ],
        });
        var responses = Responses(read.Responses);
        var expectedResponses = new List<Dictionary<string, object?>>
        {
            new() { ["Item"] = Item(("attr", S("v1"))) },
            new() { ["Item"] = Item() },
            new(),
        };
        Ensure(Py.Equal(responses, expectedResponses), $"expected [hit, exists-without-attr, absent] shapes, got {Py.Repr(responses)}");

        var write = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements =
            [
                new ParameterizedStatement { Statement = $"UPDATE \"{t}\" SET other = ? WHERE pk = ? AND sk = ?", Parameters = [S("touched"), S("p1"), S("s1")] },
                new ParameterizedStatement { Statement = $"UPDATE \"{t}\" SET other = ? WHERE pk = ? AND sk = ?", Parameters = [S("touched"), S("p1"), S("s2")] },
            ],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(write.Responses is { Count: 0 }, $"expected an empty Responses list for a write transaction, got {Py.Repr(Responses(write.Responses))}");
        var consumed = write.ConsumedCapacity;
        Ensure(consumed.Count == 1, $"expected exactly one ConsumedCapacity entry, got {Py.Repr(consumed.Select(c => (c.TableName, c.CapacityUnits)).ToList())}");
        Ensure(consumed[0].TableName == t, $"expected the entry to name the table, got {Py.Repr((consumed[0].TableName, consumed[0].CapacityUnits))}");

        var after1 = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("p1")), ("sk", S("s1"))), ConsistentRead = true })).Item;
        var after2 = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("p1")), ("sk", S("s2"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after1.GetValueOrDefault("other"), S("touched")), $"expected the write transaction to have applied, got {Py.Repr(after1)}");
        Ensure(Py.Equal(after2.GetValueOrDefault("other"), S("touched")), $"expected the write transaction to have applied, got {Py.Repr(after2)}");
    }

    /// <summary>The Responses list as boto3 hands it back: <c>{'Item': ...}</c>, or <c>{}</c> with no Item key.</summary>
    private static List<Dictionary<string, object?>>? Responses(List<ItemResponse>? responses) =>
        responses?.Select(r => r.Item is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["Item"] = r.Item }).ToList();
}
