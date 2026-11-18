// Translated from behavioral/dynamodb/check_update_item_lsi_key_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-lsi-key-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemLsiKeyTypeMismatchRejected
{
    /// <summary>
    /// UpdateItem SET on an LSI sort key whose value type contradicts the index
    /// AttributeDefinition is refused, and a same-shape update with the declared type succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-lsi-key-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsitype-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsisk", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsisk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("ITEM#1")), ("sk", S("S1")));
        await ddb.PutItemAsync(table, Item(("pk", S("ITEM#1")), ("sk", S("S1")), ("lsisk", S("A")), ("payload", S("p"))));

        var (_, err) = await FidelityContext.Capture(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET lsisk = :v",
            ExpressionAttributeValues = Item((":v", N("5"))),
        }));
        if (err is null)
        {
            throw Fail("an UpdateItem whose LSI key has the wrong type: the call was ACCEPTED; real AWS rejects it");
        }

        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
        const string expected = "One or more parameter values were invalid: Type mismatch for Index Key lsisk " +
            "Expected: S Actual: N IndexName: lsi1";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged["lsisk"], S("A")), $"the refused update must leave the LSI key untouched: {Py.Repr(unchanged)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET lsisk = :v",
            ExpressionAttributeValues = Item((":v", S("B"))),
        });
        var updated = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(updated["lsisk"], S("B")), $"a correctly typed UpdateItem on the LSI key must still succeed: {Py.Repr(updated)}");
    }
}
