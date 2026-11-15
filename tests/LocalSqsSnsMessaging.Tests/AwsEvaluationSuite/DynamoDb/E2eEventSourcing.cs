// Translated from behavioral/dynamodb/check_e2e_event_sourcing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.e2e-event-sourcing</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eEventSourcing
{
    private const string CheckId = "e2e-event-sourcing";

    private static readonly string[] EventTypes =
    [
        "order_created", "item_added", "item_added", "item_removed", "address_set",
        "item_added", "payment_submitted", "payment_confirmed", "shipped", "delivered",
    ];

    /// <summary>
    /// Event sourcing: write events for an aggregate, query chronologically, paginate,
    /// then delete the table and prove it is genuinely gone.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.e2e-event-sourcing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");

        async Task DeleteTableIfPresent()
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("aggregate_id", KeyType.HASH), new KeySchemaElement("event_id", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("aggregate_id", ScalarAttributeType.S), new AttributeDefinition("event_id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(DeleteTableIfPresent);
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        const string aggregate = "order-abc-123";
        for (var i = 0; i < EventTypes.Length; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("aggregate_id", S(aggregate)),
                ("event_id", S(string.Create(CultureInfo.InvariantCulture, $"evt-{i:D4}"))),
                ("event_type", S(EventTypes[i])),
                ("seq", N(i.ToString(CultureInfo.InvariantCulture)))));
        }

        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "aggregate_id = :agg",
            ExpressionAttributeValues = Item((":agg", S(aggregate))),
            ScanIndexForward = true,
            ConsistentRead = true,
        });
        Ensure(resp.Count == 10, $"Expected 10 events, got {resp.Count}");
        var eventIds = resp.Items.Select(item => item["event_id"].S).ToList();
        Ensure(eventIds.SequenceEqual(eventIds.Order(StringComparer.Ordinal)), $"Events not in chronological order: {Py.Repr(eventIds)}");
        var allEvents = new List<Dictionary<string, AttributeValue>>();
        var request = new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "aggregate_id = :agg",
            ExpressionAttributeValues = Item((":agg", S(aggregate))),
            ScanIndexForward = true,
            Limit = 3,
            ConsistentRead = true,
        };
        var pages = 0;
        while (true)
        {
            var pageResp = await ddb.QueryAsync(request);
            allEvents.AddRange(pageResp.Items);
            pages++;
            if (pageResp.LastEvaluatedKey is null)
            {
                break;
            }

            request.ExclusiveStartKey = pageResp.LastEvaluatedKey;
        }

        Ensure(allEvents.Count == 10, $"Pagination yielded {allEvents.Count} events, expected 10");
        Ensure(pages >= 3, $"Expected at least 3 pages with Limit=3, got {pages}");
        var pagedIds = allEvents.Select(item => item["event_id"].S).ToList();
        Ensure(pagedIds.SequenceEqual(pagedIds.Order(StringComparer.Ordinal)), "Paginated events lost ordering");
        var gotTypes = allEvents.Select(item => item["event_type"].S).ToList();
        Ensure(gotTypes.SequenceEqual(EventTypes), $"Reassembled event log mismatched the written sequence: expected {Py.Repr(EventTypes)}, got {Py.Repr(gotTypes)}");

        await ddb.DeleteTableAsync(table);
        await ddb.WaitForTableNotExists(table, delaySeconds: 2, maxAttempts: 250);
        var describeError = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(table));
        if (describeError is null)
        {
            throw Fail("DescribeTable succeeded on a deleted table");
        }

        var code = describeError.Code;
        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException on a deleted table, got {code}");
        var message = describeError.Message;
        var expectedMessage = $"Requested resource not found: Table: {table} not found";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        var putError = await FidelityContext.CaptureError(() => ddb.PutItemAsync(table, Item(("aggregate_id", S(aggregate)), ("event_id", S("evt-post-delete")))));
        if (putError is null)
        {
            throw Fail("PutItem succeeded against a deleted table");
        }

        code = putError.Code;
        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException writing to a deleted table, got {code}");
        message = putError.Message;
        expectedMessage = "Requested resource not found";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");
    }
}
