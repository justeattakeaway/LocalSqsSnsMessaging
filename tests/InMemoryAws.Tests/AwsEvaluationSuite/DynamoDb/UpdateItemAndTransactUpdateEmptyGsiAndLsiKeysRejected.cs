// Translated from behavioral/dynamodb/check_update_item_and_transact_update_empty_gsi_and_lsi_keys_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-and-transact-update-empty-gsi-and-lsi-keys-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAndTransactUpdateEmptyGsiAndLsiKeysRejected
{
    private const string GsiMessage =
        "One or more parameter values are not valid. The update expression attempted to " +
        "update a secondary index key to a value that is not supported. The AttributeValue " +
        "for a key attribute cannot contain an empty string value.";

    private const string LsiMessage =
        "One or more parameter values are not valid. The update expression attempted to " +
        "update a secondary index key to a value that is not supported. The AttributeValue " +
        "for a key attribute cannot contain an empty binary value.";

    /// <summary>
    /// UpdateItem and TransactWriteItems Update setting a GSI key to an empty S or an LSI key to
    /// an empty B are refused with ValidationException, up front and never as a cancellation.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-and-transact-update-empty-gsi-and-lsi-keys-rejected")]
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
                new AttributeDefinition("lsk", ScalarAttributeType.B),
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        var key = Item(("pk", S("b")), ("sk", S("1")));
        await ddb.PutItemAsync(table, key);

        var err = await Error(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET gpk = :v",
                ExpressionAttributeValues = Item((":v", S(""))),
            }),
            "UpdateItem setting the GSI key to an empty S");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        Ensure(err.Message == GsiMessage, $"expected message {Py.Repr(GsiMessage)}, got {Py.Repr(err.Message)}");

        err = await Error(
            () => ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET lsk = :v",
                ExpressionAttributeValues = Item((":v", B(Array.Empty<byte>()))),
            }),
            "UpdateItem setting the LSI key to an empty B");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err.Code}");
        Ensure(err.Message == LsiMessage, $"expected message {Py.Repr(LsiMessage)}, got {Py.Repr(err.Message)}");

        err = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = key,
                            UpdateExpression = "SET gpk = :v",
                            ExpressionAttributeValues = Item((":v", S(""))),
                        },
                    },
                ],
            }),
            "TransactWriteItems Update setting the GSI key to an empty S");
        Ensure(
            err.Code == "ValidationException",
            $"an empty index key fails up-front validation rather than cancelling the transaction; got {err.Code}");
        Ensure(err.Message == GsiMessage, $"expected message {Py.Repr(GsiMessage)}, got {Py.Repr(err.Message)}");
        Ensure(
            err.CancellationReasons() is null,
            $"an up-front validation failure carries no CancellationReasons; got {Py.Repr(err.CancellationReasons())}");

        err = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = key,
                            UpdateExpression = "SET lsk = :v",
                            ExpressionAttributeValues = Item((":v", B(Array.Empty<byte>()))),
                        },
                    },
                ],
            }),
            "TransactWriteItems Update setting the LSI key to an empty B");
        Ensure(
            err.Code == "ValidationException",
            $"an empty index key fails up-front validation rather than cancelling the transaction; got {err.Code}");
        Ensure(err.Message == LsiMessage, $"expected message {Py.Repr(LsiMessage)}, got {Py.Repr(err.Message)}");
        Ensure(
            err.CancellationReasons() is null,
            $"an up-front validation failure carries no CancellationReasons; got {Py.Repr(err.CancellationReasons())}");

        var held = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(held, key), $"the four refused updates must leave the item unchanged, got {Py.Repr(held)}");
    }

    private static async Task<AwsError> Error<T>(Func<Task<T>> call, string what)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(call);
        return error ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");
    }
}
