// Translated from behavioral/dynamodb/check_transact_get_items_top_level_vs_cancellation_error_class.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-top-level-vs-cancellation-error-class</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsTopLevelVsCancellationErrorClass
{
    /// <summary>
    /// A bad table name and an unparsable projection each fail the whole TransactGetItems call
    /// with their own top-level error; a key missing an attribute instead cancels the transaction
    /// and names the broken slot; a projection matching no attribute still succeeds, Item absent.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-top-level-vs-cancellation-error-class")]
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
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitTableActive(ctx, ddb, table);
        await ddb.PutItemAsync(table, Item(("pk", S("item1")), ("sk", S("main")), ("color", S("blue"))));

        TransactGetItem ValidGet() => new() { Get = new Get { TableName = table, Key = Item(("pk", S("item1")), ("sk", S("main"))) } };

        // Positive control: a well-formed TransactGetItems returns the item.
        var control = (await ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [ValidGet()] })).Responses;
        Ensure(
            control[0].Item is not null && control[0].Item.TryGetValue("color", out var color) && Py.Equal(color, S("blue")),
            $"the positive control must read back color=blue, got {Py.Repr(control.Select(Slot).ToList())}");

        // 1. An invalid TableName at position 2 fails the whole call with its OWN top-level error,
        // never cancellation: DynamoDB cannot start a transaction against a table that is not there.
        var badTableGet = new TransactGetItem { Get = new Get { TableName = missingTable, Key = Item(("pk", S("item1")), ("sk", S("main"))) } };
        var err = await FidelityContext.CaptureError(() => ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [ValidGet(), badTableGet] }))
            ?? throw Fail("a TransactGetItems naming a nonexistent table at position 2 was ACCEPTED");
        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {Py.Repr(err.Code)}");
        Ensure(err.Message == "Requested resource not found", $"expected exact message, got {Py.Repr(err.Message)}");
        Ensure(err.CancellationReasons() is null, "a missing table is a top-level failure, not a per-item cancellation");

        // 2. An unparsable ProjectionExpression also fails the whole call with its OWN top-level
        // error, never cancellation: the request as a whole could not even be parsed.
        var badProjGet = new TransactGetItem
        {
            Get = new Get { TableName = table, Key = Item(("pk", S("item1")), ("sk", S("main"))), ProjectionExpression = "color AND" },
        };
        err = await FidelityContext.CaptureError(() => ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [badProjGet] }))
            ?? throw Fail("a TransactGetItems with an unparsable ProjectionExpression was ACCEPTED");
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(err.Code)}");
        const string expectedProjMessage = "Invalid ProjectionExpression: Syntax error; token: \"AND\", near: \"color AND\"";
        Ensure(err.Message == expectedProjMessage, $"expected {Py.Repr(expectedProjMessage)}, got {Py.Repr(err.Message)}");
        Ensure(err.CancellationReasons() is null, "an unparsable projection expression is a top-level failure, not a per-item cancellation");

        // 3. A Key missing an attribute (the sort key) instead CANCELS the transaction, and the
        // cancellation reason names only the broken slot.
        var incompleteKeyGet = new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("item1"))) } };
        err = await FidelityContext.CaptureError(() => ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [ValidGet(), incompleteKeyGet] }))
            ?? throw Fail("a TransactGetItems with a Key missing the sort key was ACCEPTED");
        Ensure(err.Code == "TransactionCanceledException", $"expected TransactionCanceledException, got {Py.Repr(err.Code)}");
        var reasons = (err.CancellationReasons() ?? []).Select(Reason).ToList();
        Ensure(
            Py.Equal(reasons[0], new Dictionary<string, object?> { ["Code"] = "None" }),
            $"the valid slot must carry no reason, got {Py.Repr(reasons[0])}");
        Ensure(
            Py.Equal(reasons[1], new Dictionary<string, object?> { ["Code"] = "ValidationError", ["Message"] = "The provided key element does not match the schema" }),
            $"the incomplete-key slot must carry the exact schema-mismatch reason, got {Py.Repr(reasons[1])}");

        // 4. A ProjectionExpression matching no attribute of an existing item still succeeds; the
        // response for that slot simply carries no Item.
        var noMatchProjGet = new TransactGetItem
        {
            Get = new Get { TableName = table, Key = Item(("pk", S("item1")), ("sk", S("main"))), ProjectionExpression = "nonexistentattr" },
        };
        var responses = (await ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = [noMatchProjGet] })).Responses;
        Ensure(responses[0].Item is null, $"a projection matching nothing must omit Item entirely, got {Py.Repr(Slot(responses[0]))}");
    }

    /// <summary>Poll DescribeTable to ACTIVE, bounded by the run's remaining time.</summary>
    private static async Task WaitTableActive(FidelityContext ctx, IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(1.0, Math.Min(60.0, (timeLeft > 0 ? timeLeft : 60.0) - 10.0)));
        var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
        while (status != "ACTIVE" && clock.Elapsed < deadline)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            status = (await ddb.DescribeTableAsync(table)).Table.TableStatus.Value;
        }

        Ensure(status == "ACTIVE", $"table did not reach ACTIVE in time, last status {Py.Repr(status)}");
    }

    private static Dictionary<string, object?> Slot(ItemResponse response) =>
        response.Item is null ? [] : new() { ["Item"] = response.Item };

    /// <summary>A cancellation reason as boto3 hands it back.</summary>
    private static Dictionary<string, object?> Reason(CancellationReason reason)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reason.Item is not null)
        {
            result["Item"] = reason.Item;
        }

        if (reason.Code is not null)
        {
            result["Code"] = reason.Code;
        }

        if (reason.Message is not null)
        {
            result["Message"] = reason.Message;
        }

        return result;
    }
}
