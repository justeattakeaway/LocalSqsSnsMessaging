// Translated from behavioral/dynamodb/check_putitem_condition_comparison_on_missing_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.putitem-condition-comparison-on-missing-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutitemConditionComparisonOnMissingAttribute
{
    private const string CheckId = "putitem-condition-comparison-on-missing-attribute";
    private static readonly (string Code, string Message) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>Assert fn() raises exactly (code, message).</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// A PutItem ConditionExpression comparing a missing attribute: '=' treats it as absent
    /// and fails with ConditionalCheckFailedException; '&lt;&gt;', alone or OR'd with '&lt;' against a
    /// second missing attribute, treats it as not-equal and the write applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.putitem-condition-comparison-on-missing-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-missattr-");
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

        // The item exists but never carries the "seen" attribute: it is missing, not null.
        var keyEq = Item(("pk", S("eq-target")));
        await ddb.PutItemAsync(table, Item(("pk", S("eq-target"))));

        // Negative: '=' against a missing attribute is false, so the write is refused.
        await ExpectError(
            async () => await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("eq-target")), ("seen", Bool(true))),
                ConditionExpression = "seen = :v",
                ExpressionAttributeValues = Item((":v", Bool(true))),
            }),
            Ccf);
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = keyEq, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, Item(("pk", S("eq-target")))),
            $"PutItem rejected under 'seen = :v' must leave the item unchanged, got {Py.Repr(unchanged)}");

        // Positive control, same missing attribute: '<>' treats it as not-equal, so the write applies.
        var neItem = Item(("pk", S("eq-target")), ("seen", Bool(true)));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = neItem,
            ConditionExpression = "seen <> :v",
            ExpressionAttributeValues = Item((":v", Bool(false))),
        });
        var landed = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = keyEq, ConsistentRead = true })).Item;
        Ensure(Py.Equal(landed, neItem), $"PutItem under 'seen <> :v' on a missing attribute left {Py.Repr(landed)}");

        // A brand-new item (never put before): status and updated_at are both missing.
        var keyOr = Item(("pk", S("or-target")));
        var orItem = Item(("pk", S("or-target")), ("status", S("new")));
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = orItem,
            ConditionExpression = "#s <> :v OR #u < :t",
            ExpressionAttributeNames = Names(("#s", "status"), ("#u", "updated_at")),
            ExpressionAttributeValues = Item((":v", S("retired")), (":t", N("100"))),
        });
        var orLanded = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = keyOr, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(orLanded, orItem),
            $"PutItem under '#s <> :v OR #u < :t' against two missing attributes left {Py.Repr(orLanded)}");
    }
}
