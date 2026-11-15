// Translated from behavioral/dynamodb/check_put_get_roundtrip_string.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-get-roundtrip-string</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetRoundtripString
{

    /// <summary>PutItem/GetItem round-trips a string attribute exactly.</summary>
    [Test]
    [DisplayName("dynamodb.put-get-roundtrip-string")]
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
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var item = Item(("pk", S("test-1")), ("data", S("hello world")));
        await ddb.PutItemAsync(table, item);
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("test-1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, item), $"expected {Py.Repr(item)}, got {Py.Repr(got)}");
    }
}
