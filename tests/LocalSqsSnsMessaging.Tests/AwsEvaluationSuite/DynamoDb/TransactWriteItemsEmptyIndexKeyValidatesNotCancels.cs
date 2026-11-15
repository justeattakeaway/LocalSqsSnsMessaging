// Translated from behavioral/dynamodb/check_transact_write_items_empty_index_key_validates_not_cancels.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-empty-index-key-validates-not-cancels</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsEmptyIndexKeyValidatesNotCancels
{
    /// <summary>
    /// An EMPTY-STRING index key is caught by up-front input validation, so it is a top-level
    /// ValidationException -- the opposite arm from a wrong-type key. An engine that wraps this as
    /// a TransactionCanceledException diverges, so the two arms pin each other.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-empty-index-key-validates-not-cancels")]
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
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var err = await Raises(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("b")), ("sk", S("1")), ("gpk", S(""))) } },
                ],
            }),
            "a transaction whose index key is the empty string");
        Ensure(err.Code == "ValidationException", $"an empty index key is up-front input validation, not a cancellation; got {err.Code}");
        const string expected =
            "One or more parameter values are not valid. A value specified for a secondary index key is "
            + "not supported. The AttributeValue for a key attribute cannot contain an empty string value. "
            + "IndexName: gsi1, IndexKey: gpk";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        Ensure((await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0, "nothing may be written");
        var errUpdate = await Raises(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("pk", S("b")), ("sk", S("1"))),
                            UpdateExpression = "SET gpk = :v",
                            ExpressionAttributeValues = Item((":v", S(""))),
                        },
                    },
                ],
            }),
            "a transaction whose Update sets an index key to the empty string");
        Ensure(
            errUpdate.Code == "ValidationException",
            $"an empty index key via Update is up-front input validation too, not a cancellation; got {errUpdate.Code}");
        const string expectedUpdate =
            "One or more parameter values are not valid. The update expression attempted to update "
            + "a secondary index key to a value that is not supported. The AttributeValue for a key "
            + "attribute cannot contain an empty string value.";
        Ensure(errUpdate.Message == expectedUpdate, $"expected message {Py.Repr(expectedUpdate)}, got {Py.Repr(errUpdate.Message)}");
        Ensure((await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Count == 0, "nothing may be written");
    }

    private static async Task<AwsError> Raises(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call)
        ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
}
