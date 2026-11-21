// Translated from behavioral/dynamodb/check_update_item_add_and_delete_through_nested_placeholders_reject_mismatched_leaf_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-and-delete-through-nested-placeholders-reject-mismatched-leaf-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddAndDeleteThroughNestedPlaceholdersRejectMismatchedLeafTypes
{
    private const string Check = "update-item-add-and-delete-through-nested-placeholders-reject-mismatched-leaf-types";

    private static readonly (string Code, string Message) TypeMismatch = (
        "ValidationException",
        "An operand in the update expression has an incorrect data type");

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; raises on success.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>
    /// ADD and DELETE through a per-segment placeholder path are each refused when the named
    /// nested leaf is not the type the action requires, and the item stays unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-and-delete-through-nested-placeholders-reject-mismatched-leaf-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("orderId", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("orderId", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("orderId", S("ord-7")));
        Dictionary<string, AttributeValue> ItemWithWeight(string weight) => Item(
            ("orderId", S("ord-7")),
            ("label", S("ok")),
            ("shipment", M(
                ("route", M(
                    ("hops", L(S("west"), S("east"))),
                    ("carrier", S("freight-co")))),
                ("metrics", M(("weight", N(weight)))))));
        var original = ItemWithWeight("10");
        await ddb.PutItemAsync(table, original);

        var hopsNames = Names(("#s", "shipment"), ("#r", "route"), ("#h", "hops"));
        var carrierNames = Names(("#s", "shipment"), ("#r", "route"), ("#c", "carrier"));
        var weightNames = Names(("#s", "shipment"), ("#m", "metrics"), ("#w", "weight"));

        // ADD requires a Number or Set stored attribute; shipment.route.hops is a List.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD #s.#r.#h :five",
                ExpressionAttributeNames = hopsNames,
                ExpressionAttributeValues = Item((":five", N("5"))),
            }),
            TypeMismatch);
        var afterAdd = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(afterAdd, original),
            $"the refused ADD through shipment.route.hops must leave the item unchanged, got {Py.Repr(afterAdd)}");

        // DELETE requires a Set stored attribute; shipment.route.carrier is a String.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "DELETE #s.#r.#c :tag",
                ExpressionAttributeNames = carrierNames,
                ExpressionAttributeValues = Item((":tag", SS("freight-co"))),
            }),
            TypeMismatch);
        var afterDelete = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(afterDelete, original),
            $"the refused DELETE through shipment.route.carrier must leave the item unchanged, got {Py.Repr(afterDelete)}");

        // Positive control: ADD through the same style of path onto a Number leaf still applies.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD #s.#m.#w :delta",
            ExpressionAttributeNames = weightNames,
            ExpressionAttributeValues = Item((":delta", N("7"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("shipment", M(("metrics", M(("weight", N("17")))))))),
            $"the positive-control ADD through shipment.metrics.weight returned {Py.Repr(resp.Attributes)}, " +
            "expected weight=17");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var expectedFinal = ItemWithWeight("17");
        Ensure(
            Py.Equal(final, expectedFinal),
            $"after the one successful ADD the item is {Py.Repr(final)}, expected exactly {Py.Repr(expectedFinal)}");
    }
}
