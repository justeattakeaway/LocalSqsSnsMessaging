// Translated from behavioral/dynamodb/check_query_scan_filterexpression_nested_path_into_unqueried_index_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-scan-filterexpression-nested-path-into-unqueried-index-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryScanFilterexpressionNestedPathIntoUnqueriedIndexKey
{
    private static readonly (string Code, string Message) IndexKeyInScopeRejected = (
        "ValidationException",
        "Filter Expression can only contain non-primary key attributes: Primary key attribute: gr");

    /// <summary>
    /// A FilterExpression nested path into a secondary index's key attribute is refused only
    /// when that index is the one being queried, even for the index's other key; the same
    /// attribute is an ordinary attribute on a Scan or on a Query of the base table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-scan-filterexpression-nested-path-into-unqueried-index-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-idxfilt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gh", ScalarAttributeType.S),
                new AttributeDefinition("gr", ScalarAttributeType.S),
            ],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gh", KeyType.HASH), new KeySchemaElement("gr", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        double Monotonic() => clock.Elapsed.TotalSeconds;
        var deadline = Monotonic() + 120;
        while (true)
        {
            var gsis = (await ddb.DescribeTableAsync(table)).Table.GlobalSecondaryIndexes ?? [];
            if (gsis.Count > 0 && gsis[0].IndexStatus == IndexStatus.ACTIVE)
            {
                break;
            }

            Ensure(Monotonic() < deadline, $"gsi1 did not reach ACTIVE, last saw {Py.Repr(gsis.Select(g => g.IndexStatus).ToList())}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("s1")), ("gh", S("g")), ("gr", S("r1")), ("color", S("red"))));

        // A Scan has no key condition at all: a nested path into a secondary index's key
        // attribute is an ordinary document-path lookup, not a protected key reference.
        var scanNested = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "gh.x = :v",
            ExpressionAttributeValues = Item((":v", S("z"))),
            ConsistentRead = true,
        });
        Ensure(
            scanNested.Count == 0,
            "Scan FilterExpression nested into the GSI hash key must not error; it matches by " +
            $"document path, which is absent, so Count should be 0, got {scanNested.Count}");
        Ensure(
            scanNested.ScannedCount == 1,
            "Scan must still have scanned the one seeded item, got ScannedCount " +
            $"{scanNested.ScannedCount}");

        var scanControl = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "color = :c",
            ExpressionAttributeValues = Item((":c", S("red"))),
            ConsistentRead = true,
        });
        Ensure(scanControl.Count == 1, $"Scan positive control matched {scanControl.Count}, expected 1");
        Ensure(Py.Equal(scanControl.Items[0]["pk"], S("p")), $"Scan positive control returned {Py.Repr(scanControl.Items[0])}, expected the seeded item");

        // A base-table Query names no index: the GSI's key attribute is likewise an ordinary
        // attribute there, even though the same attribute is protected while querying that index.
        var baseQueryNested = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "gh.x = :v",
            ExpressionAttributeValues = Item((":p", S("p")), (":v", S("z"))),
            ConsistentRead = true,
        });
        Ensure(
            baseQueryNested.Count == 0,
            "base-table Query FilterExpression nested into the GSI hash key must not error; " +
            $"got Count {baseQueryNested.Count}");

        var baseQueryControl = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "color = :c",
            ExpressionAttributeValues = Item((":p", S("p")), (":c", S("red"))),
            ConsistentRead = true,
        });
        Ensure(baseQueryControl.Count == 1, $"base-table Query positive control matched {baseQueryControl.Count}, expected 1");
        Ensure(
            Py.Equal(baseQueryControl.Items[0]["sk"], S("s1")),
            $"base-table Query positive control returned {Py.Repr(baseQueryControl.Items[0])}, " +
            "expected the seeded item");

        // Querying gsi1 itself protects BOTH of its key attributes, not only the one named in
        // KeyConditionExpression: a nested path into the index's other key is still refused.
        deadline = Monotonic() + 60;
        QueryResponse indexControl;
        while (true)
        {
            indexControl = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "gsi1",
                KeyConditionExpression = "gh = :g",
                FilterExpression = "color = :c",
                ExpressionAttributeValues = Item((":g", S("g")), (":c", S("red"))),
            });
            if (indexControl.Count == 1)
            {
                break;
            }

            Ensure(Monotonic() < deadline, $"item never became visible on gsi1, last saw Count {indexControl.Count}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(
            Py.Equal(indexControl.Items[0]["gr"], S("r1")),
            $"index Query positive control returned {Py.Repr(indexControl.Items[0])}, " +
            "expected the seeded item");

        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "gsi1",
                KeyConditionExpression = "gh = :g",
                FilterExpression = "gr.x = :v",
                ExpressionAttributeValues = Item((":g", S("g")), (":v", S("z"))),
            }),
            IndexKeyInScopeRejected.Code,
            IndexKeyInScopeRejected.Message);
    }

    /// <summary>Assert <paramref name="fn"/> raises exactly (code, message); an unexpected success raises too.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected {code} but the call succeeded: {Py.Repr(result)}");
        }

        var got = (error.Code, error.Message);
        Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
    }
}
