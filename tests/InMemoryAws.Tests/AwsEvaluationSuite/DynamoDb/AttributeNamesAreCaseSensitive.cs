// Translated from behavioral/dynamodb/check_attribute_names_are_case_sensitive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.attribute-names-are-case-sensitive</summary>
[Category("AwsEvaluationSuite")]
public sealed class AttributeNamesAreCaseSensitive
{
    private const string Check = "attribute-names-are-case-sensitive";
    private const string ErrConditionFailed = "ConditionalCheckFailedException";

    private static Dictionary<string, AttributeValue> ItemValue() => Item(
        ("pk", S("case")),
        ("body", S("lower")),
        ("Body", S("title")),
        ("BODY", S("upper")));

    /// <summary>
    /// Attribute names differing only in case are distinct attributes: they coexist on one item,
    /// and a projection, an update and attribute_exists each resolve exactly the matching spelling.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.attribute-names-are-case-sensitive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-attrcase-");
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

        await ddb.PutItemAsync(table, ItemValue());

        async Task<Dictionary<string, AttributeValue>> Current() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("case"))), ConsistentRead = true })).Item;

        Ensure(
            Py.Equal(await Current(), ItemValue()),
            "three attribute names differing only in case are three attributes and must all survive " +
            $"one write: expected {Py.Repr(ItemValue())}, got {Py.Repr(await Current())}");

        // Projection and condition are separate lookups from the round-trip: a store can keep three
        // attributes yet still resolve an expression name case-insensitively.
        foreach (var (name, expected) in new[] { ("body", "lower"), ("Body", "title"), ("BODY", "upper") })
        {
            var projected = (await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("case"))),
                ProjectionExpression = "#n",
                ExpressionAttributeNames = Names(("#n", name)),
                ConsistentRead = true,
            })).Item;
            Ensure(
                Py.Equal(projected, Item((name, S(expected)))),
                $"a projection on {Py.Repr(name)} must return that attribute alone, got {Py.Repr(projected)}");
        }

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("case"))),
            UpdateExpression = "SET #n = :v",
            ExpressionAttributeNames = Names(("#n", "Body")),
            ExpressionAttributeValues = Item((":v", S("title-updated"))),
        });
        var after = await Current();
        var expectedAfter = ItemValue();
        expectedAfter["Body"] = S("title-updated");
        Ensure(
            Py.Equal(after, expectedAfter),
            "updating Body must leave body and BODY untouched: expected only Body to change, got " +
            $"{Py.Repr(after)}");

        await ddb.PutItemAsync(table, Item(("pk", S("guarded")), ("Marker", S("present"))));
        var error = await FidelityContext.CaptureError(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("guarded"))),
            UpdateExpression = "SET touched = :t",
            ExpressionAttributeValues = Item((":t", Bool(true))),
            ConditionExpression = "attribute_exists(marker)",
        }));
        if (error is null)
        {
            throw Fail(
                "the item carries Marker, not marker, so attribute_exists(marker) must not match and " +
                "the update must be refused; it SUCCEEDED");
        }

        Ensure(
            error.Code == ErrConditionFailed,
            "a condition on an attribute name whose case does not match must fail the condition, " +
            $"not the request: expected {ErrConditionFailed}, got {error.Code}");
        Ensure(
            error.Message == "The conditional request failed",
            $"expected AWS's exact ConditionalCheckFailedException message, got {Py.Repr(error.Message)}");

        var guarded = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("guarded"))), ConsistentRead = true })).Item;
        Ensure(!guarded.ContainsKey("touched"), $"the refused update must not have written its attribute, got {Py.Repr(guarded)}");

        // Control: the same condition with the matching case succeeds.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("guarded"))),
            UpdateExpression = "SET touched = :t",
            ExpressionAttributeValues = Item((":t", Bool(true))),
            ConditionExpression = "attribute_exists(Marker)",
        });
        guarded = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("guarded"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(guarded.GetValueOrDefault("touched"), Bool(true)),
            $"the same condition spelled to match must succeed and apply the update, got {Py.Repr(guarded)}");
    }
}
