// Translated from behavioral/dynamodb/check_update_item_return_values_pin_deep_nested_leaf_updates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-return-values-pin-deep-nested-leaf-updates</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemReturnValuesPinDeepNestedLeafUpdates
{
    private const string Check = "update-item-return-values-pin-deep-nested-leaf-updates";

    /// <summary>Run undo() where a failed delete raises; ResourceInUse is retried to the deadline.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until ACTIVE, or fail with the last status read.</summary>
    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutS = 300.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>
    /// UpdateItem ReturnValues after a deep (map-leaf, list-index) update: ALL_NEW/ALL_OLD still
    /// pin the whole item, UPDATED_NEW omits Attributes on a REMOVE, and UPDATED_OLD rebuilds the
    /// touched nested map leaf and list under their parent attribute rather than flattening the path.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-return-values-pin-deep-nested-leaf-updates")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-rvdeep-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, table);

        var key = Item(("pk", S("rv-deep")));
        Dictionary<string, AttributeValue> ItemWith(AttributeValue deep) => Item(
            ("pk", S("rv-deep")),
            ("deep", deep),
            ("lst", L(S("a0"), S("a1"), S("a2"))),
            ("side", S("keep")));
        await ddb.PutItemAsync(table, ItemWith(M(("leaf", N("1")), ("extra", S("x")))));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // ALL_NEW after a deep map-leaf SET pins the WHOLE item, not just the touched leaf.
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET deep.leaf = :v",
            ExpressionAttributeValues = Item((":v", N("2"))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        var afterSet1 = ItemWith(M(("leaf", N("2")), ("extra", S("x"))));
        Ensure(
            Py.Equal(resp.Attributes, afterSet1),
            "ALL_NEW after a deep SET must return the whole item with the leaf updated, " +
            $"got {Py.Repr(resp.Attributes)}, expected {Py.Repr(afterSet1)}");

        // ALL_OLD after a second deep map-leaf SET pins the WHOLE prior item.
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET deep.leaf = :v",
            ExpressionAttributeValues = Item((":v", N("3"))),
            ReturnValues = ReturnValue.ALL_OLD,
        });
        Ensure(
            Py.Equal(resp.Attributes, afterSet1),
            "ALL_OLD after a deep SET must return the whole prior item, " +
            $"got {Py.Repr(resp.Attributes)}, expected {Py.Repr(afterSet1)}");
        var afterSet2 = ItemWith(M(("leaf", N("3")), ("extra", S("x"))));
        var current = await Read();
        Ensure(
            Py.Equal(current, afterSet2),
            $"the second deep SET must have applied, got {Py.Repr(current)}, expected {Py.Repr(afterSet2)}");

        // UPDATED_NEW after a nested-leaf REMOVE omits Attributes entirely (REMOVE has no new value).
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE deep.extra",
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        Ensure(
            resp.Attributes is null,
            $"UPDATED_NEW after REMOVE of a nested map leaf must omit Attributes, got {Py.Repr(resp.Attributes)}");
        var afterRemoveExtra = await Read();
        Ensure(
            Py.Equal(afterRemoveExtra, ItemWith(M(("leaf", N("3"))))),
            $"REMOVE deep.extra must take effect, got {Py.Repr(afterRemoveExtra)}");

        // UPDATED_OLD after a nested-leaf REMOVE returns the removed leaf rebuilt under its parent
        // attribute, not the flat path, and not the whole item.
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE deep.leaf",
            ReturnValues = ReturnValue.UPDATED_OLD,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("deep", M(("leaf", N("3")))))),
            "UPDATED_OLD after REMOVE of a nested map leaf must rebuild {deep: {leaf: <old>}} " +
            $"under its parent, got {Py.Repr(resp.Attributes)}");
        var afterRemoveLeaf = await Read();
        Ensure(
            Py.Equal(afterRemoveLeaf["deep"], M()),
            $"both deep.extra and deep.leaf must now be gone, got {Py.Repr(afterRemoveLeaf["deep"])}");

        // UPDATED_OLD after REMOVE of a list INDEX returns only the removed element, rebuilt as a
        // one-element list under its parent attribute's own name.
        resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "REMOVE lst[1]",
            ReturnValues = ReturnValue.UPDATED_OLD,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("lst", L(S("a1"))))),
            "UPDATED_OLD after REMOVE of a list index must return the removed element alone, " +
            $"rebuilt under its list attribute's name, got {Py.Repr(resp.Attributes)}");
        var final = await Read();
        Ensure(Py.Equal(final["lst"], L(S("a0"), S("a2"))), $"REMOVE lst[1] must drop exactly index 1, got {Py.Repr(final["lst"])}");
        Ensure(Py.Equal(final["side"], S("keep")), $"the untouched sibling attribute must survive, got {Py.Repr(final)}");
    }
}
