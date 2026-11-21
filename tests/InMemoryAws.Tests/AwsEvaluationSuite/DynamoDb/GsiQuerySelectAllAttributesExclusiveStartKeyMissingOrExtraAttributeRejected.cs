// Translated from behavioral/dynamodb/check_gsi_query_select_all_attributes_exclusive_start_key_missing_or_extra_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-select-all-attributes-exclusive-start-key-missing-or-extra-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQuerySelectAllAttributesExclusiveStartKeyMissingOrExtraAttributeRejected
{
    private const string Check = "gsi-query-select-all-attributes-exclusive-start-key-missing-or-extra-attribute-rejected";
    private const string IndexName = "StatusIndex";
    private const string ErrValidation = "ValidationException";
    private const string EskMessage = "The provided starting key is invalid";

    /// <summary>
    /// A GSI Query with Select=ALL_ATTRIBUTES rejects an ExclusiveStartKey missing any of its
    /// four key attributes or carrying one outside the key schema, each with ValidationException
    /// and no change to the table's contents.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-select-all-attributes-exclusive-start-key-missing-or-extra-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsiesksel-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("CustomerId", KeyType.HASH), new KeySchemaElement("OrderId", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("CustomerId", ScalarAttributeType.S),
                new AttributeDefinition("OrderId", ScalarAttributeType.S),
                new AttributeDefinition("Status", ScalarAttributeType.S),
                new AttributeDefinition("OrderSeq", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = IndexName,
                    KeySchema = [new KeySchemaElement("Status", KeyType.HASH), new KeySchemaElement("OrderSeq", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("CustomerId", S("cust-01")),
                ("OrderId", S($"order-{i:000}")),
                ("Status", S("PLACED")),
                ("OrderSeq", N(i.ToString(CultureInfo.InvariantCulture)))));
        }

        Task<QueryResponse> Query(int? limit = null, Dictionary<string, AttributeValue>? exclusiveStartKey = null) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = IndexName,
            KeyConditionExpression = "#s = :s",
            ExpressionAttributeNames = Names(("#s", "Status")),
            ExpressionAttributeValues = Item((":s", S("PLACED"))),
            Select = Select.ALL_ATTRIBUTES,
            Limit = limit,
            ExclusiveStartKey = exclusiveStartKey,
        });

        // The GSI is eventually consistent: poll until the index holds all five items.
        var clock = FidelityTime.StartNew();
        var deadline = Math.Max(0.0, ctx.TimeLeftSeconds - 60.0);
        while ((await Query()).Count != 5)
        {
            Ensure(clock.Elapsed.TotalSeconds < deadline, "the index never held all five items");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var page = await Query(limit: 1);
        Ensure(page.Items.Select(i => i["OrderId"].S).SequenceEqual(["order-000"]), Py.Repr(page.Items));
        var realKey = page.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        Ensure(
            Py.Equal(realKey, Item(("CustomerId", S("cust-01")), ("OrderId", S("order-000")), ("Status", S("PLACED")), ("OrderSeq", N("0")))),
            $"expected the first item's four key attributes as the LastEvaluatedKey, got {Py.Repr(realKey)}");

        // Positive control: Select=ALL_ATTRIBUTES with the real, complete ExclusiveStartKey continues the walk.
        var cont = await Query(exclusiveStartKey: realKey);
        var contIds = cont.Items.Select(i => i["OrderId"].S).ToList();
        Ensure(
            contIds.SequenceEqual(Enumerable.Range(1, 4).Select(i => $"order-{i:000}")),
            $"positive control: the real ExclusiveStartKey must resume after order-000, got {Py.Repr(contIds)}");

        // Each of the four key attributes missing from the ExclusiveStartKey is rejected.
        foreach (var missing in new[] { "CustomerId", "OrderId", "Status", "OrderSeq" })
        {
            var badKey = realKey!.Where(e => e.Key != missing).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            var (code, message) = await Error(() => Query(exclusiveStartKey: badKey));
            Ensure(
                code == ErrValidation,
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
            Ensure(
                message == EskMessage,
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected AWS's exact ValidationException " +
                $"message, got {Py.Repr(message)}");
        }

        // The complete key plus one attribute outside the key schema is rejected too.
        var extraKey = new Dictionary<string, AttributeValue>(realKey!, StringComparer.Ordinal) { ["ExtraField"] = S("unexpected") };
        var (extraCode, extraMessage) = await Error(() => Query(exclusiveStartKey: extraKey));
        Ensure(
            extraCode == ErrValidation,
            $"ExclusiveStartKey with an extra attribute: expected {ErrValidation}, got {extraCode}: {Py.Repr(extraMessage)}");
        Ensure(
            extraMessage == EskMessage,
            "ExclusiveStartKey with an extra attribute: expected AWS's exact ValidationException " +
            $"message, got {Py.Repr(extraMessage)}");

        // None of the five rejected calls wrote, deleted or otherwise changed the table.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        var finalIds = final.Select(i => i["OrderId"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            finalIds.SequenceEqual(Enumerable.Range(0, 5).Select(i => $"order-{i:000}")),
            $"expected the original 5 items unchanged after the rejected calls, got {Py.Repr(finalIds)}");

        // A second positive control: the real key still continues the walk after the rejections.
        var cont2 = await Query(exclusiveStartKey: realKey);
        Ensure(
            Py.Equal(cont2.Items, cont.Items),
            "the real ExclusiveStartKey's continuation changed after the rejected calls: " +
            $"{Py.Repr(cont2.Items)} vs {Py.Repr(cont.Items)}");
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutS = 180.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = last.GlobalSecondaryIndexes ?? [];
            if (last.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} or its index did not become ACTIVE: {Py.Repr(last?.TableStatus)}");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary><c>(Code, Message)</c> for the error <paramref name="fn"/> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }
}
