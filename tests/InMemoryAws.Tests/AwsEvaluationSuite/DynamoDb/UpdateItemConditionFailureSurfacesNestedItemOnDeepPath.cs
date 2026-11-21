// Translated from behavioral/dynamodb/check_update_item_condition_failure_surfaces_nested_item_on_deep_path.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-condition-failure-surfaces-nested-item-on-deep-path</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemConditionFailureSurfacesNestedItemOnDeepPath
{
    private const string Check = "update-item-condition-failure-surfaces-nested-item-on-deep-path";
    private static readonly (string Code, string Message) ConditionalCheckFailed = ("ConditionalCheckFailedException", "The conditional request failed");

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

    /// <summary>Call fn() and grade the error it raises on exact code and message; a success raises.</summary>
    private static async Task<AwsError> ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected {expected.Code} {Py.Repr(expected.Message)}, but the call succeeded: {result}");
        }

        Ensure(err.Code == expected.Code, $"expected code {Py.Repr(expected.Code)}, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
        Ensure(err.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(err.Message)}");
        return err;
    }

    /// <summary>
    /// A rejected UpdateItem whose ConditionExpression checks a deep map leaf surfaces, via
    /// ReturnValuesOnConditionCheckFailure=ALL_OLD, the CURRENT whole item with its nested map and
    /// list intact, exactly as a successful ALL_OLD would, and leaves the item unchanged.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateItem.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-condition-failure-surfaces-nested-item-on-deep-path")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-rvccfe-");
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

        var key = Item(("pk", S("rv-ccfe")));
        var item = Item(
            ("pk", S("rv-ccfe")),
            ("deep", M(("leaf", N("1")), ("other", S("x")))),
            ("lst", L(S("a0"), S("a1"))));
        await ddb.PutItemAsync(table, item);

        // Positive control: a condition on the CURRENT deep leaf value succeeds and also advances
        // that same leaf, so the value the next step's condition checks is genuinely stale.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET deep.leaf = :v, side = :s",
            ConditionExpression = "deep.leaf = :expected",
            ExpressionAttributeValues = Item((":v", N("2")), (":s", S("control")), (":expected", N("1"))),
        });
        var controlled = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(controlled.GetValueOrDefault("side"), S("control")),
            $"the positive control update did not take effect, got {Py.Repr(controlled)}");
        Ensure(
            Py.Equal(controlled["deep"].M["leaf"], N("2")),
            $"the positive control update must have advanced deep.leaf to 2, got {Py.Repr(controlled)}");

        // Break exactly one thing: the condition checks the now-STALE deep.leaf value (1, not 2).
        var exc = await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "SET side = :s",
                ConditionExpression = "deep.leaf = :expected",
                ExpressionAttributeValues = Item((":s", S("clobbered")), (":expected", N("1"))),
                ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
            }),
            ConditionalCheckFailed);
        var surfaced = (exc.Exception as ConditionalCheckFailedException)?.Item;
        Ensure(
            Py.Equal(surfaced, controlled),
            "ReturnValuesOnConditionCheckFailure=ALL_OLD on a deep-path condition must surface the " +
            $"whole current item with its nested map and list intact, got {Py.Repr(surfaced)}, expected {Py.Repr(controlled)}");

        // Effect absent: an independent read shows the rejected update did not touch the item.
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged, controlled), $"a rejected conditional update must not modify the item, got {Py.Repr(unchanged)}");

        // Restore: the identical call, with the condition matching the CURRENT value, now succeeds.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET side = :s",
            ConditionExpression = "deep.leaf = :expected",
            ExpressionAttributeValues = Item((":s", S("restored")), (":expected", N("2"))),
            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
        });
        var restored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(restored.GetValueOrDefault("side"), S("restored")),
            $"an UpdateItem whose deep-path condition matches the current value must succeed, got {Py.Repr(restored)}");
    }
}
