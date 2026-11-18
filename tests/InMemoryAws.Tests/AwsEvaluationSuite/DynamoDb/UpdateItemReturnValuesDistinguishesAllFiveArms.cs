// Translated from behavioral/dynamodb/check_update_item_return_values_distinguishes_all_five_arms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-return-values-distinguishes-all-five-arms</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemReturnValuesDistinguishesAllFiveArms
{
    private static readonly string[] Arms = ["NONE", "ALL_OLD", "UPDATED_OLD", "ALL_NEW", "UPDATED_NEW"];

    /// <summary>
    /// The five ReturnValues arms are five different answers, and each pins the others: NONE returns
    /// nothing, ALL_OLD/ALL_NEW the whole item before/after, UPDATED_OLD/UPDATED_NEW ONLY the
    /// attribute that changed. An engine collapsing any pair fails here.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-return-values-distinguishes-all-five-arms")]
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

        Task Reset() => ddb.PutItemAsync(table, Item(("pk", S("rv")), ("v", N("1")), ("w", N("1"))));

        var got = new Dictionary<string, Dictionary<string, AttributeValue>?>(StringComparer.Ordinal);
        foreach (var arm in Arms)
        {
            await Reset();
            var res = await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("rv"))),
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", N("2"))),
                ReturnValues = new ReturnValue(arm),
            });
            got[arm] = res.Attributes;
        }

        Ensure(got["NONE"] is null, $"NONE must return no Attributes, got {Py.Repr(got["NONE"])}");
        Ensure(
            Py.Equal(got["UPDATED_OLD"], Item(("v", N("1")))),
            $"UPDATED_OLD returns ONLY the changed attribute's old value, got {Py.Repr(got["UPDATED_OLD"])}");
        Ensure(
            Py.Equal(got["UPDATED_NEW"], Item(("v", N("2")))),
            $"UPDATED_NEW returns ONLY the changed attribute's new value, got {Py.Repr(got["UPDATED_NEW"])}");
        Ensure(
            Py.Equal(got["ALL_OLD"], Item(("pk", S("rv")), ("v", N("1")), ("w", N("1")))),
            $"ALL_OLD returns the whole prior item, got {Py.Repr(got["ALL_OLD"])}");
        Ensure(
            Py.Equal(got["ALL_NEW"], Item(("pk", S("rv")), ("v", N("2")), ("w", N("1")))),
            $"ALL_NEW returns the whole updated item, got {Py.Repr(got["ALL_NEW"])}");
        Ensure(
            !Py.Equal(got["UPDATED_NEW"], got["ALL_NEW"]),
            "UPDATED_NEW and ALL_NEW must differ: the unchanged attribute w belongs only to ALL_NEW");
    }
}
