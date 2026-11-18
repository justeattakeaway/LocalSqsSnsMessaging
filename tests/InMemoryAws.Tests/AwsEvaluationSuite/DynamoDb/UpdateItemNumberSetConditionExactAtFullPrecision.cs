// Translated from behavioral/dynamodb/check_update_item_number_set_condition_exact_at_full_precision.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-number-set-condition-exact-at-full-precision</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemNumberSetConditionExactAtFullPrecision
{
    private const string CheckId = "update-item-number-set-condition-exact-at-full-precision";
    private static readonly (string Code, string Message) ConditionalFailed = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// A Number Set equality condition compares at full decimal precision, not as float64:
    /// an exact-value NS matches, and a value one digit off is ConditionalCheckFailedException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-number-set-condition-exact-at-full-precision")]
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

        var pk = FidelityContext.UniqueName("item-");
        const string stored = "100000000000000001";
        // Same float64 as `stored`; a double-based comparison would treat the two as equal.
        const string offByOne = "100000000000000002";

        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("ns", NS(stored)), ("v", N("0"))));

        // Positive control: an exact-value NS condition matches and the update applies.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "SET v = :nv",
            ConditionExpression = "ns = :match",
            ExpressionAttributeValues = Item((":nv", N("1")), (":match", NS(stored))),
        });
        var afterMatch = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(afterMatch["v"], N("1")), $"an exact-value NS condition must let the update through, got v={Py.Repr(afterMatch["v"])}");
        Ensure(Py.Equal(afterMatch["ns"], NS(stored)), $"the NS attribute must be unchanged by the update, got {Py.Repr(afterMatch["ns"])}");

        // Negative: an NS one decimal digit off is a different value at full precision, even though
        // both round to the same float64, so the condition must fail and the update must not apply.
        var (result, error) = await FidelityContext.Capture(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "SET v = :nv",
            ConditionExpression = "ns = :off",
            ExpressionAttributeValues = Item((":nv", N("2")), (":off", NS(offByOne))),
        }));
        if (error is null)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        var got = (error.Code, error.Message);
        Ensure(
            got == ConditionalFailed,
            $"an NS condition off by one digit at full precision must be {Py.Repr(ConditionalFailed)}, got {Py.Repr(got)}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged["v"], N("1")), $"a write rejected by its condition must leave the item unmodified, got v={Py.Repr(unchanged["v"])}");
        Ensure(Py.Equal(unchanged["ns"], NS(stored)), $"the NS attribute must still read back exactly, got {Py.Repr(unchanged["ns"])}");
    }
}
