// Translated from behavioral/dynamodb/check_batch_write_item_gsi_key_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-item-gsi-key-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteItemGsiKeyTypeMismatchRejected
{
    /// <summary>
    /// BatchWriteItem carrying an index key whose type contradicts the index AttributeDefinition is
    /// refused, and the message names the index. Accepting it corrupts the index silently.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-item-gsi-key-type-mismatch-rejected")]
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
        var left = ctx.TimeLeftSeconds;
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: Math.Max(10, (int)(left == 0 ? 300 : left)));
        var err = await ErrorCode(
            () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new()
                {
                    [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("b")), ("sk", S("1")), ("gpk", N("9"))) } }],
                },
            }),
            "a BatchWriteItem whose GSI key has the wrong type");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expectedMsg = "One or more parameter values were invalid: Type mismatch for Index Key gpk " +
                                   "Expected: S Actual: N IndexName: gsi1";
        Ensure(
            err.Message == expectedMsg,
            $"the message must name the offending index key: expected {Py.Repr(expectedMsg)}, got {Py.Repr(err.Message)}");
        Ensure(
            (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0,
            "the refused write must leave the table empty");
        var errBoth = await ErrorCode(
            () => ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new()
                {
                    [table] =
                    [
                        new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S("b")), ("sk", S("2")), ("gpk", N("9")), ("lsk", S("x"))) } },
                    ],
                },
            }),
            "a BatchWriteItem whose LSI key and GSI key both have the wrong type");
        Ensure(errBoth.Code == "ValidationException", $"expected ValidationException, got {errBoth.Code}");
        const string expectedBoth = "One or more parameter values were invalid: Type mismatch for Index Key gpk " +
                                    "Expected: S Actual: N IndexName: gsi1";
        Ensure(
            errBoth.Message == expectedBoth,
            $"the message must name the offending index key: expected {Py.Repr(expectedBoth)}, got {Py.Repr(errBoth.Message)}");
        Ensure(
            (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0,
            "the refused write must leave the table empty");
    }

    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> call, string what)
    {
        var error = await FidelityContext.CaptureError(call);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }
}
