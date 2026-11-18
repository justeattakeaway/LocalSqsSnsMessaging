// Translated from behavioral/dynamodb/check_transact_write_items_duplicate_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-duplicate-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsDuplicateKeyRejected
{
    /// <summary>
    /// Two actions on the same item in one transaction are refused, with a message naming the
    /// multiple-operations rule rather than a generic parse error.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-duplicate-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var err = await ErrorCode(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("dup"))) } },
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("dup"))) } },
                ],
            }),
            "a transaction with two actions on one item");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        const string expected = "Transaction request cannot include multiple operations on one item";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        Ensure((await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0, "nothing may be written");

        // An ExpressionAttributeValues entry unused by the second action is rejected on its own terms,
        // ahead of any multiple-operations check, and nothing from either action is written.
        var err2 = await ErrorCode(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("a"))) } },
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = Item(("pk", S("b"))),
                            ConditionExpression = "attribute_not_exists(pk)",
                            ExpressionAttributeValues = Item((":unused", S("x"))),
                        },
                    },
                ],
            }),
            "a transaction whose second action declares an unused ExpressionAttributeValues entry");
        Ensure(err2.Code == "ValidationException", $"expected ValidationException, got {err2.Code}");
        const string expected2 = "Value provided in ExpressionAttributeValues unused in expressions: keys: {:unused}";
        Ensure(err2.Message == expected2, $"expected message {Py.Repr(expected2)}, got {Py.Repr(err2.Message)}");
        Ensure((await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0, "nothing may be written");
    }

    private static async Task<AwsError> ErrorCode(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call)
        ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}
