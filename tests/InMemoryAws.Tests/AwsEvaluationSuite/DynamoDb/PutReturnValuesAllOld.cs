// Translated from behavioral/dynamodb/check_put_return_values_all_old.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-return-values-all-old</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutReturnValuesAllOld
{
    private const string CheckId = "put-return-values-all-old";

    /// <summary>PutItem with ReturnValues=ALL_OLD returns the previous item.</summary>
    [Test]
    [DisplayName("dynamodb.put-return-values-all-old")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var key = Item(("pk", S("rv-test")));
        await ddb.PutItemAsync(table, Item(("pk", S("rv-test")), ("v", N("1"))));
        var resp = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("rv-test")), ("v", N("2"))),
            ReturnValues = ReturnValue.ALL_OLD,
        });
        var old = resp.Attributes;
        Ensure(old is not null, $"PutItem with ReturnValues=ALL_OLD must return Attributes, got {Py.Repr(new Dictionary<string, object?> { ["Attributes"] = old })}");
        Ensure(
            Py.Equal(old, Item(("pk", S("rv-test")), ("v", N("1")))),
            $"ALL_OLD must return the complete prior item including the key, got {Py.Repr(old)}");

        var current = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(current, Item(("pk", S("rv-test")), ("v", N("2")))),
            $"independent read after the overwrite must show the new item, got {Py.Repr(current)}");
    }
}
