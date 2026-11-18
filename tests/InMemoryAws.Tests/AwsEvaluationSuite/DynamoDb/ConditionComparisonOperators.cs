// Translated from behavioral/dynamodb/check_condition_comparison_operators.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-comparison-operators</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionComparisonOperators
{
    private const string Check = "condition-comparison-operators";
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";
    private const string ErrConditionalCheckFailedMessage = "The conditional request failed";

    /// <summary>
    /// Every ConditionExpression comparison operator (&lt;, &lt;=, &gt;, &gt;=, =, &lt;&gt;) admits a true
    /// condition and rejects a false one, and a rejected update leaves the item unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-comparison-operators")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("cmp")), ("val", N("10"))));

        // (operator, true-threshold, new value on success, false-threshold once at that value)
        var cases = new[]
        {
            ("<", "20", "15", "10"),
            ("<=", "15", "16", "15"),
            (">", "10", "20", "20"),
            (">=", "20", "25", "30"),
            ("=", "25", "30", "25"),
            ("<>", "999", "35", "35"),
        };
        foreach (var (op, trueThreshold, newVal, falseThreshold) in cases)
        {
            await Update(ddb, table, "cmp", newVal, op, trueThreshold);
            var got = await Read(ddb, table, "cmp");
            Ensure(Py.Equal(got["val"], N(newVal)), $"val {op} {trueThreshold} should admit the update to {newVal}, got {Py.Repr(got["val"])}");

            var (code, message) = await RejectedUpdate(ddb, table, "cmp", "99999", op, falseThreshold);
            Ensure(code == ErrConditionalCheckFailed, $"val {op} {falseThreshold} should reject with {ErrConditionalCheckFailed}, got {code}");
            Ensure(message == ErrConditionalCheckFailedMessage, $"val {op} {falseThreshold}: expected {Py.Repr(ErrConditionalCheckFailedMessage)}, got {Py.Repr(message)}");

            var gotAfter = await Read(ddb, table, "cmp");
            Ensure(Py.Equal(gotAfter["val"], N(newVal)), $"rejected update via {op} must leave val at {newVal}, got {Py.Repr(gotAfter["val"])}");
        }
    }

    private static async Task<Dictionary<string, AttributeValue>> Read(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    private static Task Update(IAmazonDynamoDB ddb, string table, string pk, string newVal, string op, string threshold) =>
        ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk))),
            UpdateExpression = "SET val = :new",
            ConditionExpression = $"val {op} :threshold",
            ExpressionAttributeValues = Item((":new", N(newVal)), (":threshold", N(threshold))),
        });

    /// <summary>The (Code, Message) of an update real AWS refuses. RAISES on unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> RejectedUpdate(IAmazonDynamoDB ddb, string table, string pk, string newVal, string op, string threshold)
    {
        var error = await FidelityContext.CaptureError(() => Update(ddb, table, pk, newVal, op, threshold));
        return error is not null
            ? (error.Code, error.Message)
            : throw Fail($"real AWS refuses the update conditioned on val {op} {threshold}, but it SUCCEEDED");
    }
}
