// Translated from behavioral/dynamodb/check_transact_write_items_first_fatal_defect_reported_depends_on_defect_class.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-first-fatal-defect-reported-depends-on-defect-class</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsFirstFatalDefectReportedDependsOnDefectClass
{
    private static async Task<AwsError> Error(Func<Task> call, string what) =>
        await FidelityContext.CaptureError(call)
        ?? throw Fail($"{what}: the call was ACCEPTED; real AWS rejects it");

    /// <summary>Poll DescribeTable to ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var limit = Math.Max(1.0, Math.Min(60.0, (timeLeft == 0 ? 60.0 : timeLeft) - 10.0));
        var clock = FidelityTime.StartNew();
        var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
        while (status != "ACTIVE" && clock.Elapsed < TimeSpan.FromSeconds(limit))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
        }

        Ensure(status == "ACTIVE", $"table did not reach ACTIVE in time, last status {Py.Repr(status)}");
    }

    /// <summary>A map nested <paramref name="depth"/> levels deep, past DynamoDB's 32-level document limit.</summary>
    private static AttributeValue NestedString(int depth)
    {
        var value = S("leaf");
        for (var i = 0; i < depth - 1; i++)
        {
            value = M(("n", value));
        }

        return value;
    }

    /// <summary>
    /// Across two actions each carrying one defect, an unused ExpressionAttributeValues key beats
    /// a missing table regardless of which action lists it first, while a malformed condition
    /// expression and a 33-level-deep item are each reported only when listed first.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-first-fatal-defect-reported-depends-on-defect-class")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        var missingTable = FidelityContext.UniqueName("sim2real-behav-missing-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableActive(ctx, ddb, table);
        await ddb.PutItemAsync(table, Item(("pk", S("existing")), ("v", N("1"))));

        // Positive control: a well-formed transaction is accepted.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("control"))) } }],
        });
        var control = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item;
        Ensure(control is { Count: > 0 }, "the positive control item must exist after a well-formed TransactWriteItems");

        TransactWriteItem UnusedValueAction() => new()
        {
            Update = new Update
            {
                TableName = table,
                Key = Item(("pk", S("existing"))),
                UpdateExpression = "SET v = v + :inc",
                ExpressionAttributeValues = Item((":inc", N("1")), (":unused", N("99"))),
            },
        };
        TransactWriteItem MissingTableAction() => new() { Put = new Put { TableName = missingTable, Item = Item(("pk", S("x"))) } };
        const string unusedValueMessage = "Value provided in ExpressionAttributeValues unused in expressions: keys: {:unused}";

        var errA1 = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [MissingTableAction(), UnusedValueAction()] }),
            "missing table first, unused expression value second");
        Ensure(errA1.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(errA1.Code)}");
        Ensure(errA1.Message == unusedValueMessage, $"expected {Py.Repr(unusedValueMessage)}, got {Py.Repr(errA1.Message)}");

        var errA2 = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [UnusedValueAction(), MissingTableAction()] }),
            "unused expression value first, missing table second");
        Ensure(errA2.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(errA2.Code)}");
        Ensure(errA2.Message == unusedValueMessage, $"expected {Py.Repr(unusedValueMessage)}, got {Py.Repr(errA2.Message)}");

        TransactWriteItem MalformedConditionAction() => new()
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = table,
                Key = Item(("pk", S("cc-key"))),
                ConditionExpression = "attribute_exists(pk AND",
            },
        };
        TransactWriteItem DeepItemAction() => new() { Put = new Put { TableName = table, Item = Item(("pk", S("deep33")), ("blob", NestedString(33))) } };
        const string conditionSyntaxMessage = "Invalid ConditionExpression: Syntax error; token: \"AND\", near: \"pk AND\"";
        const string nestingMessage = "Nesting Levels have exceeded supported limits: Attributes in the item have "
            + "nested levels beyond supported limit";

        var errB1 = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [MalformedConditionAction(), DeepItemAction()] }),
            "malformed condition first, 33-level item second");
        Ensure(errB1.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(errB1.Code)}");
        Ensure(
            errB1.Message == conditionSyntaxMessage,
            $"listing the malformed condition first must report it; expected {Py.Repr(conditionSyntaxMessage)}, got {Py.Repr(errB1.Message)}");

        var errB2 = await Error(
            () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [DeepItemAction(), MalformedConditionAction()] }),
            "33-level item first, malformed condition second");
        Ensure(errB2.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(errB2.Code)}");
        Ensure(
            errB2.Message == nestingMessage,
            $"listing the 33-level item first must report it; expected {Py.Repr(nestingMessage)}, got {Py.Repr(errB2.Message)}");

        var remaining = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("existing"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(remaining["v"], N("1")),
            $"none of the four rejected transactions may touch the existing item; v is {Py.Repr(remaining["v"])}");
        var items = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            items.SetEquals(["control", "existing"]),
            $"none of the four rejected transactions may write anything; table holds {Py.Repr(items.Order(StringComparer.Ordinal).ToList())}");
    }
}
