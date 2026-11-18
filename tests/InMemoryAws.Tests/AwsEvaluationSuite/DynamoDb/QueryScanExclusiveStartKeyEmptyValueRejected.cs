// Translated from behavioral/dynamodb/check_query_scan_exclusive_start_key_empty_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-scan-exclusive-start-key-empty-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryScanExclusiveStartKeyEmptyValueRejected
{
    private const string ErrValidation = "ValidationException";

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll fn() (an eventually consistent Limit=1 page) until it carries a LastEvaluatedKey.</summary>
    private static async Task<Dictionary<string, AttributeValue>> PollForLek(
        FidelityContext ctx, Func<Task<Dictionary<string, AttributeValue>?>> fn, double pollS = 1.0)
    {
        while (true)
        {
            var lek = await fn();
            if (lek is not null)
            {
                return lek;
            }

            var left = ctx.TimeLeftSeconds;
            if (left <= pollS)
            {
                throw Fail("no second item became visible in time; last page had no LastEvaluatedKey");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollS));
        }
    }

    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> key, string name, AttributeValue value) =>
        new(key, StringComparer.Ordinal) { [name] = value };

    /// <summary>
    /// Query/Scan reject an ExclusiveStartKey whose hash or range key is an empty string or
    /// binary value, with ValidationException. Query always names the key alone; Scan always
    /// names a secondary index on the table instead, for any key attribute, once one exists.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-scan-exclusive-start-key-empty-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.B),
                new AttributeDefinition("gpk", ScalarAttributeType.B),
                new AttributeDefinition("gsk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH), new KeySchemaElement("gsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        var item1 = Item(("pk", S("x")), ("sk", B("1")), ("gpk", B("g")), ("gsk", S("h")));
        var item2 = Item(("pk", S("x")), ("sk", B("2")), ("gpk", B("g")), ("gsk", S("i")));
        await ddb.PutItemAsync(table, item1);
        await ddb.PutItemAsync(table, item2);

        // Positive controls: a real, freshly minted ExclusiveStartKey continues each path.
        var page = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("x"))),
            Limit = 1,
            ConsistentRead = true,
        });
        var tableEsk = page.LastEvaluatedKey;
        var cont = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("x"))),
            ExclusiveStartKey = tableEsk,
            ConsistentRead = true,
        });
        Ensure(cont.Items is { Count: > 0 }, "positive control: query continuation with a real ExclusiveStartKey returned nothing");
        var scanPage = await ddb.ScanAsync(new ScanRequest { TableName = table, Limit = 1, ConsistentRead = true });
        var scanEsk = scanPage.LastEvaluatedKey;
        var scanCont = await ddb.ScanAsync(new ScanRequest { TableName = table, ExclusiveStartKey = scanEsk, ConsistentRead = true });
        Ensure(scanCont.Items is not null, $"positive control: scan continuation returned {Py.Repr(scanCont.Items)}");

        // The GSI is eventually consistent: poll Limit=1 until the second item is visible, so
        // pagination here reflects two items rather than racing the index's own propagation.
        var gsiQueryEsk = await PollForLek(ctx, async () => (await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gpk = :g",
            ExpressionAttributeValues = Item((":g", B("g"))),
            Limit = 1,
        })).LastEvaluatedKey);
        cont = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gpk = :g",
            ExpressionAttributeValues = Item((":g", B("g"))),
            ExclusiveStartKey = gsiQueryEsk,
        });
        Ensure(cont.Items is { Count: > 0 }, "positive control: GSI query continuation returned nothing");
        var gsiScanEsk = await PollForLek(ctx, async () => (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = "gsi1", Limit = 1 })).LastEvaluatedKey);
        scanCont = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = "gsi1", ExclusiveStartKey = gsiScanEsk });
        Ensure(scanCont.Items is not null, $"positive control: GSI scan continuation returned {Py.Repr(scanCont.Items)}");

        // Query never names an index: every empty key attribute reports "Key: <name>" alone,
        // whether it is the table's hash key, its range key, the GSI's hash key or its range key.
        foreach (var (label, badKey, attr) in new[]
        {
            ("table hash (pk, S)", With(tableEsk, "pk", S("")), "pk"),
            ("table range (sk, B)", With(tableEsk, "sk", B([])), "sk"),
            ("GSI hash (gpk, B)", With(gsiQueryEsk, "gpk", B([])), "gpk"),
            ("GSI range (gsk, S)", With(gsiQueryEsk, "gsk", S("")), "gsk"),
        })
        {
            var emptyKind = badKey[attr].S == "" ? "string" : "binary";
            (string? Code, string? Message) result;
            if (attr is "gpk" or "gsk")
            {
                result = await Error(() => ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    IndexName = "gsi1",
                    KeyConditionExpression = "gpk = :g",
                    ExpressionAttributeValues = Item((":g", B("g"))),
                    ExclusiveStartKey = badKey,
                }));
            }
            else
            {
                result = await Error(() => ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    KeyConditionExpression = "pk = :p",
                    ExpressionAttributeValues = Item((":p", S("x"))),
                    ExclusiveStartKey = badKey,
                }));
            }

            var (code, message) = result;
            Ensure(code == ErrValidation, $"Query/{label}: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
            var expected =
                "One or more parameter values are not valid. The AttributeValue for a key attribute " +
                $"cannot contain an empty {emptyKind} value. Key: {attr}";
            Ensure(message == expected, $"Query/{label}: expected {Py.Repr(expected)}, got {Py.Repr(message)}");
        }

        // Scan always names a secondary index -- gsi1, the only one on this table -- for every
        // empty key attribute, even one (the base table's own key) that index does not contain.
        foreach (var (label, badKey, attr, useIndex) in new[]
        {
            ("table hash (pk, S)", With(scanEsk, "pk", S("")), "pk", false),
            ("table range (sk, B)", With(scanEsk, "sk", B([])), "sk", false),
            ("GSI hash (gpk, B)", With(gsiScanEsk, "gpk", B([])), "gpk", true),
            ("GSI range (gsk, S)", With(gsiScanEsk, "gsk", S("")), "gsk", true),
        })
        {
            var emptyKind = badKey[attr].S == "" ? "string" : "binary";
            var (code, message) = useIndex
                ? await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = "gsi1", ExclusiveStartKey = badKey }))
                : await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, ExclusiveStartKey = badKey }));
            Ensure(code == ErrValidation, $"Scan/{label}: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
            var expected =
                "One or more parameter values are not valid. A value specified for a secondary " +
                "index key is not supported. The AttributeValue for a key attribute cannot contain " +
                $"an empty {emptyKind} value. IndexName: gsi1, IndexKey: {attr}";
            Ensure(message == expected, $"Scan/{label}: expected {Py.Repr(expected)}, got {Py.Repr(message)}");
        }

        // Nothing any of the eight rejected reads could write: the table still holds only the seed.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        var sortedFinal = final.OrderBy(i => Convert.ToHexString(Bytes(i["sk"].B)), StringComparer.Ordinal).ToList();
        Ensure(Py.Equal(sortedFinal, new[] { item1, item2 }), $"expected only the two seed items to remain, got {Py.Repr(final)}");
    }
}
