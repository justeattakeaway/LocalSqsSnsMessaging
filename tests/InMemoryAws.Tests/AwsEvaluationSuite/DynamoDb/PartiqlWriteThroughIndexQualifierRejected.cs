// Translated from behavioral/dynamodb/check_partiql_write_through_index_qualifier_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-write-through-index-qualifier-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlWriteThroughIndexQualifierRejected
{
    private static async Task<AwsError> ErrorCode(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        if (error is null)
        {
            throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
        }

        return error;
    }

    /// <summary>
    /// PartiQL cannot write through an index. UPDATE, INSERT and DELETE addressed at
    /// "table"."index" are each refused, so a target that silently wrote to the base table would
    /// diverge invisibly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-write-through-index-qualifier-rejected")]
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
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("sk", S("1")), ("gpk", S("g1")), ("lsk", N("5"))));
        var err = await ErrorCode(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"UPDATE \"{table}\".\"gsi1\" SET extra='z' WHERE gpk='g1'" }),
            "a PartiQL UPDATE addressed at an index");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        var expected = "This operation is not supported on an index";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a")), ("sk", S("1"))), ConsistentRead = true })).Item;
        Ensure(!got.ContainsKey("extra"), "the refused UPDATE must leave the base item unchanged");

        err = await ErrorCode(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"INSERT INTO \"{table}\".\"gsi1\" VALUE {{'pk':'z','sk':'9','gpk':'g9'}}" }),
            "a PartiQL INSERT addressed at an index");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        expected = $"FROM clause may only contain a single table name in data manipulation statements at 1:13:{table.Length + 2}";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var inserted = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("z")), ("sk", S("9"))), ConsistentRead = true });
        Ensure(inserted.Item is null, "the refused INSERT must not create the item");

        err = await ErrorCode(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"DELETE FROM \"{table}\".\"gsi1\" WHERE gpk='g1'" }),
            "a PartiQL DELETE addressed at an index");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        expected = "This operation is not supported on an index";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        var stillThere = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a")), ("sk", S("1"))), ConsistentRead = true });
        Ensure(stillThere.Item is not null, "the refused DELETE must leave the base item in place");
    }
}
