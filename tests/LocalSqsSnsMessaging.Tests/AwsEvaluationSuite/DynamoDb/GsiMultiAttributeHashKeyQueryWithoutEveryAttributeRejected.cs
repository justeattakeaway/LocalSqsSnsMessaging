// Translated from behavioral/dynamodb/check_gsi_multi_attribute_hash_key_query_without_every_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-multi-attribute-hash-key-query-without-every-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiMultiAttributeHashKeyQueryWithoutEveryAttributeRejected
{
    private const string Index = "by-tenant-region";
    private static readonly string[] Attrs5 = ["tenant", "region", "ts", "a4", "a5"];
    private static readonly string[] TableAttrs = ["pk", "tenant", "region", "ts"];

    /// <summary>
    /// A GSI keyed on two HASH attributes answers a Query only with equality on both (either alone raises
    /// ValidationException naming the other), returns exactly the matching items and skips an item missing one;
    /// CreateTable with five HASH attributes raises ValidationException and creates nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-multi-attribute-hash-key-query-without-every-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-mak-");

        // --- negative at create time: more HASH attributes than allowed, nothing is created ---
        var tooMany = $"{table}-five";
        var (created, createError) = await FidelityContext.Capture(() => ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tooMany,
            AttributeDefinitions = [.. new[] { "pk" }.Concat(Attrs5).Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "five-hash",
                    KeySchema = [.. Attrs5.Select(n => new KeySchemaElement(n, KeyType.HASH))],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        }));
        if (createError is null)
        {
            ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(tooMany), "ResourceNotFoundException"));
            throw Fail($"a GSI with five HASH attributes was accepted: {Py.Repr(created!.TableDescription.TableStatus)}");
        }

        var (code, msg) = (createError.Code, createError.Message);
        Ensure(code == "ValidationException", $"five HASH attributes gave {Py.Repr(code)}");
        Ensure(
            msg == "One or more parameter values were invalid: The KeySchema exceeds the maximum allowed number of HASH key attributes",
            $"five HASH attributes: message {Py.Repr(msg)}");
        (code, msg) = await Error(() => ddb.DescribeTableAsync(tooMany));
        Ensure(
            (code, msg) == ("ResourceNotFoundException", $"Requested resource not found: Table: {tooMany} not found"),
            $"the refused table describes as {Py.Repr(code)}: {Py.Repr(msg)}, expected it absent");

        // --- positive context: a two-HASH-attribute GSI with a RANGE attribute ---
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [.. TableAttrs.Select(n => new AttributeDefinition(n, ScalarAttributeType.S))],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema =
                    [
                        new KeySchemaElement("tenant", KeyType.HASH),
                        new KeySchemaElement("region", KeyType.HASH),
                        new KeySchemaElement("ts", KeyType.RANGE),
                    ],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        var desc = await Poll(
            async () => (await ddb.DescribeTableAsync(table)).Table,
            t => t.TableStatus == TableStatus.ACTIVE && t.GlobalSecondaryIndexes.All(g => g.IndexStatus == IndexStatus.ACTIVE),
            budgetSeconds: 600.0,
            pollSeconds: 5.0);
        var keySchema = desc.GlobalSecondaryIndexes[0].KeySchema.Select(k => (k.AttributeName, k.KeyType?.Value)).ToList();
        Ensure(
            keySchema.SequenceEqual([("tenant", "HASH"), ("region", "HASH"), ("ts", "RANGE")]),
            $"the GSI describes its key as {Py.Repr(keySchema)}");

        (string Pk, string Tenant, string Region, string Ts)[] rows = [("1", "acme", "eu", "01"), ("2", "acme", "us", "02"), ("3", "acme", "eu", "03"), ("4", "zeta", "eu", "04")];
        foreach (var (pk, tenant, region, ts) in rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("tenant", S(tenant)), ("region", S(region)), ("ts", S(ts))));
        }

        // Missing one HASH attribute: the item is not indexed at all.
        await ddb.PutItemAsync(table, Item(("pk", S("5")), ("tenant", S("acme")), ("ts", S("05"))));

        QueryRequest Full() => new()
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "tenant = :t AND #r = :r",
            ExpressionAttributeNames = Names(("#r", "region")),
            ExpressionAttributeValues = Item((":t", S("acme")), (":r", S("eu"))),
        };
        var got = await Poll(() => Pks(ddb, Full()), v => v.SequenceEqual(["1", "3"]), budgetSeconds: 60.0, pollSeconds: 2.0);
        Ensure(got.SequenceEqual(["1", "3"]), $"the full index key selected {Py.Repr(got)}");
        var ranged = Full();
        ranged.KeyConditionExpression = "tenant = :t AND #r = :r AND ts > :x";
        ranged.ExpressionAttributeValues = Item((":t", S("acme")), (":r", S("eu")), (":x", S("01")));
        got = await Pks(ddb, ranged);
        Ensure(got.SequenceEqual(["3"]), $"the full index key with ts > '01' selected {Py.Repr(got)}, expected only '3'");

        var indexed = await Poll(
            async () =>
            {
                var pks = new List<string>();
                var request = new ScanRequest { TableName = table, IndexName = Index };
                while (true)
                {
                    var page = await ddb.ScanAsync(request);
                    pks.AddRange(page.Items.Select(i => i["pk"].S));
                    if (page.LastEvaluatedKey is not { Count: > 0 })
                    {
                        break;
                    }

                    request.ExclusiveStartKey = page.LastEvaluatedKey;
                }

                return pks.Order(StringComparer.Ordinal).ToList();
            },
            v => v.SequenceEqual(["1", "2", "3", "4"]),
            budgetSeconds: 60.0,
            pollSeconds: 2.0);
        Ensure(!indexed.Contains("5"), $"an item without the 'region' HASH attribute was indexed: {Py.Repr(indexed)}");

        // --- negatives: one HASH attribute alone is refused, naming the missing one ---
        (code, msg) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "tenant = :t",
            ExpressionAttributeValues = Item((":t", S("acme"))),
        }));
        Ensure(code == "ValidationException", $"querying with only 'tenant' gave {Py.Repr(code)}");
        Ensure(
            msg == "HASH key attributes region must have equality conditions specified in the query",
            $"querying with only 'tenant': message {Py.Repr(msg)}");
        (code, msg) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "#r = :r",
            ExpressionAttributeNames = Names(("#r", "region")),
            ExpressionAttributeValues = Item((":r", S("eu"))),
        }));
        Ensure(code == "ValidationException", $"querying with only 'region' gave {Py.Repr(code)}");
        Ensure(
            msg == "HASH key attributes tenant must have equality conditions specified in the query",
            $"querying with only 'region': message {Py.Repr(msg)}");
    }

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises; only <c>notFound</c> means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>The (code, message) <c>fn</c> is refused with. An unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    /// <summary><c>read()</c> once <c>done(value)</c>; raises naming the last value when the budget runs out.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, double budgetSeconds, double pollSeconds)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(budgetSeconds))
            {
                throw Fail($"condition not met within {budgetSeconds}s; last value {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollSeconds));
        }
    }

    private static async Task<List<string>> Pks(IAmazonDynamoDB ddb, QueryRequest request)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        while (true)
        {
            var page = await ddb.QueryAsync(request);
            items.AddRange(page.Items);
            if (page.LastEvaluatedKey is not { Count: > 0 })
            {
                break;
            }

            request.ExclusiveStartKey = page.LastEvaluatedKey;
        }

        return [.. items.Select(i => i["pk"].S).Order(StringComparer.Ordinal)];
    }
}
