// Translated from behavioral/dynamodb/check_transact_write_idempotency_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-idempotency-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteIdempotencyToken
{
    /// <summary>
    /// TransactWriteItems with an ADD replayed under the same ClientRequestToken leaves the counter at 1, while a fresh
    /// token applies the ADD again and moves it to 2.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-idempotency-token")]
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

        async Task<string?> Counter()
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("idem-1"))), ConsistentRead = true })).Item;
            return item?["v"].N;
        }

        List<TransactWriteItem> Increment() =>
        [
            new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = table,
                    Key = Item(("pk", S("idem-1"))),
                    UpdateExpression = "ADD v :one",
                    ExpressionAttributeValues = Item((":one", N("1"))),
                },
            },
        ];

        var token = Guid.NewGuid().ToString();
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Increment(), ClientRequestToken = token });
        Ensure(await Counter() == "1", $"first call must set the counter to 1, got {await Counter() ?? "None"}");

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Increment(), ClientRequestToken = token });
        Ensure(
            await Counter() == "1",
            "a replay with the SAME ClientRequestToken must be deduplicated, not re-executed; "
            + $"counter is {await Counter() ?? "None"} (expected 1)");

        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Increment(), ClientRequestToken = Guid.NewGuid().ToString() });
        Ensure(await Counter() == "2", $"a fresh token must apply the ADD again, got {await Counter() ?? "None"}");
    }
}
