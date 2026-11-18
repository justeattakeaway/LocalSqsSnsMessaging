// Translated from behavioral/dynamodb/check_projection_shared_list_index_and_nested_map_merge_across_read_operations.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-shared-list-index-and-nested-map-merge-across-read-operations</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionSharedListIndexAndNestedMapMergeAcrossReadOperations
{
    private static readonly (string Alias, string Name)[] AllNames =
        [("#l", "l"), ("#m", "m"), ("#a", "a"), ("#b", "b"), ("#n", "n"), ("#x", "x"), ("#y", "y")];

    private static readonly string[] QueryScanBatch = ["Query", "Scan", "BatchGetItem"];

    /// <summary>Only the aliases <paramref name="expr"/> references; DynamoDB rejects unused ExpressionAttributeNames.</summary>
    private static Dictionary<string, string> Used(string expr) =>
        Names([.. AllNames.Where(n => Regex.IsMatch(expr, $"(?<![A-Za-z0-9_]){Regex.Escape(n.Alias)}(?![A-Za-z0-9_])"))]);

    /// <summary>
    /// A ProjectionExpression sharing a list index across a leaf and a nested map path merges
    /// into one element on Query, Scan and BatchGetItem, and Query rebuilds a map leaf alongside a
    /// renumbered list element from two unrelated top-level paths.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-shared-list-index-and-nested-map-merge-across-read-operations")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("row")), ("sk", S("only")));

        await ddb.PutItemAsync(
            table,
            Item(
                ("pk", S("row")),
                ("sk", S("only")),
                ("l", L(
                    M(("a", S("a0")), ("b", S("b0")), ("m", M(("x", S("mx0")), ("y", S("my0"))))),
                    M(("a", S("a1")), ("b", S("b1"))))),
                ("m", M(("n", S("n0")), ("z", S("z0"))))));

        async Task<Dictionary<string, AttributeValue>> Read(string op, string expr, Dictionary<string, string>? extraNames = null)
        {
            var aliases = Used(expr);
            foreach (var (k, v) in extraNames ?? [])
            {
                aliases[k] = v;
            }

            List<Dictionary<string, AttributeValue>> items;
            var (result, error) = await FidelityContext.Capture<List<Dictionary<string, AttributeValue>>>(async () =>
            {
                if (op == "GetItem")
                {
                    return [(await ddb.GetItemAsync(new GetItemRequest
                    {
                        TableName = table, Key = key, ConsistentRead = true,
                        ProjectionExpression = expr, ExpressionAttributeNames = aliases,
                    })).Item];
                }

                if (op == "Query")
                {
                    var queryNames = new Dictionary<string, string>(aliases, StringComparer.Ordinal) { ["#pk"] = "pk" };
                    return (await ddb.QueryAsync(new QueryRequest
                    {
                        TableName = table,
                        KeyConditionExpression = "#pk = :pk",
                        ExpressionAttributeNames = queryNames,
                        ExpressionAttributeValues = Item((":pk", S("row"))),
                        ProjectionExpression = expr,
                        ConsistentRead = true,
                    })).Items;
                }

                if (op == "Scan")
                {
                    return (await ddb.ScanAsync(new ScanRequest
                    {
                        TableName = table, ProjectionExpression = expr,
                        ExpressionAttributeNames = aliases, ConsistentRead = true,
                    })).Items;
                }

                return (await ddb.BatchGetItemAsync(new BatchGetItemRequest
                {
                    RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
                    {
                        [table] = new KeysAndAttributes
                        {
                            Keys = [key], ProjectionExpression = expr,
                            ExpressionAttributeNames = aliases, ConsistentRead = true,
                        },
                    },
                })).Responses[table];
            });
            if (error is not null)
            {
                throw Fail(
                    $"{op} ProjectionExpression {Py.Repr(expr)} is legal and must be SERVED, but the target refused it with {error.Code}: {error.Message}");
            }

            items = result!;
            Ensure(items.Count == 1, $"{op} expected exactly one item, got {items.Count}: {Py.Repr(items)}");
            return items[0];
        }

        // --- same list index shared by a leaf and Query/Scan/BatchGetItem: merge into one element ---
        const string exprAb = "#l[0].#a, #l[0].#b";
        var wantAb = Item(("l", L(M(("a", S("a0")), ("b", S("b0"))))));
        foreach (var op in QueryScanBatch)
        {
            var got = await Read(op, exprAb);
            Ensure(
                Py.Equal(got, wantAb),
                $"{op} ProjectionExpression {Py.Repr(exprAb)} must merge the shared index into one element carrying both leaves; got {Py.Repr(got)}");
        }

        // --- GetItem: a leaf and a nested-map path through the SAME list index merge into one element ---
        const string exprLeafAndMap = "#l[0].#a, #l[0].#m.#x";
        var gotLeafAndMap = await Read("GetItem", exprLeafAndMap);
        Ensure(
            Py.Equal(gotLeafAndMap, Item(("l", L(M(("a", S("a0")), ("m", M(("x", S("mx0"))))))))),
            $"GetItem {Py.Repr(exprLeafAndMap)} must merge the list-index leaf and the nested-map path into one element; got {Py.Repr(gotLeafAndMap)}");

        // --- GetItem: two nested-map leaves through the same list index merge into one map ---
        const string exprMapPair = "#l[0].#m.#x, #l[0].#m.#y";
        var gotMapPair = await Read("GetItem", exprMapPair);
        Ensure(
            Py.Equal(gotMapPair, Item(("l", L(M(("m", M(("x", S("mx0")), ("y", S("my0"))))))))),
            $"GetItem {Py.Repr(exprMapPair)} must merge both nested-map leaves into one map under the shared list element; got {Py.Repr(gotMapPair)}");

        // --- Query: a map leaf and a different list index rebuild as two unrelated top-level paths ---
        const string exprRebuild = "#m.#n, #l[1]";
        var gotRebuild = await Read("Query", exprRebuild);
        Ensure(
            Py.Equal(gotRebuild, Item(("m", M(("n", S("n0")))), ("l", L(M(("a", S("a1")), ("b", S("b1"))))))),
            $"Query {Py.Repr(exprRebuild)} must rebuild the map leaf and the renumbered list element as two top-level paths; got {Py.Repr(gotRebuild)}");

        // --- control: the stored item is untouched by any projected read above ---
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(
                stored["l"],
                L(
                    M(("a", S("a0")), ("b", S("b0")), ("m", M(("x", S("mx0")), ("y", S("my0"))))),
                    M(("a", S("a1")), ("b", S("b1"))))),
            $"a projected read must not mutate the stored list; got {Py.Repr(stored["l"])}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-projmerge2-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {name} never became ACTIVE");
    }
}
