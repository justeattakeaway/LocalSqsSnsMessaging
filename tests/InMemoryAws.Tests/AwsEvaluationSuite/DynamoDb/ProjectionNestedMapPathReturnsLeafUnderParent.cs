// Translated from behavioral/dynamodb/check_projection_nested_map_path_returns_leaf_under_parent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-nested-map-path-returns-leaf-under-parent</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionNestedMapPathReturnsLeafUnderParent
{
    private static readonly string[] AllAttributes = ["pk", "profile", "unrelated"];

    /// <summary>
    /// A ProjectionExpression naming a path into a map returns the leaf still wrapped in its parent
    /// map, without its siblings, and omits the attribute entirely when the nested key is absent.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-nested-map-path-returns-leaf-under-parent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);

        async Task<GetItemResponse> Get(GetItemRequest request)
        {
            // Every GetItem below is legal, so a refusal is reported as an assertion failure.
            var (response, error) = await FidelityContext.Capture(() => ddb.GetItemAsync(request));
            if (error is not null)
            {
                throw Fail($"ProjectionExpression {Py.Repr(request.ProjectionExpression)} is legal and must be SERVED, but the target refused it with {error.Code}: {error.Message}");
            }

            return response!;
        }

        await ddb.PutItemAsync(table, Item(
            ("pk", S("nested-map")),
            ("profile", M(
                ("city", S("sea")),
                ("zip", S("98101")),
                ("unit", M(("floor", N("7")))))),
            ("unrelated", S("untouched"))));

        // --- one nested leaf: wrapped in its parent, no siblings, nothing else ---
        var item = (await Get(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("nested-map"))),
            ConsistentRead = true,
            ProjectionExpression = "#p.#c",
            ExpressionAttributeNames = Names(("#p", "profile"), ("#c", "city")),
        })).Item;
        Ensure(
            Py.Equal(item, Item(("profile", M(("city", S("sea")))))),
            $"a nested-map projection must return ONLY the selected leaf, still wrapped in its parent map, and nothing else (no pk, no sibling keys); got {Py.Repr(item)}");

        // --- two siblings under one parent: both kept, merged under the same parent ---
        var both = (await Get(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("nested-map"))),
            ConsistentRead = true,
            ProjectionExpression = "#p.#c, #p.#z",
            ExpressionAttributeNames = Names(("#p", "profile"), ("#c", "city"), ("#z", "zip")),
        })).Item;
        Ensure(
            Py.Equal(both, Item(("profile", M(("city", S("sea")), ("zip", S("98101")))))),
            $"two sibling paths under one parent must merge into a single parent map carrying both leaves; got {Py.Repr(both)}");

        // --- a two-level path reaches through an intermediate map ---
        var deep = (await Get(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("nested-map"))),
            ConsistentRead = true,
            ProjectionExpression = "#p.#u.#f",
            ExpressionAttributeNames = Names(("#p", "profile"), ("#u", "unit"), ("#f", "floor")),
        })).Item;
        Ensure(
            Py.Equal(deep, Item(("profile", M(("unit", M(("floor", N("7")))))))),
            $"a two-level nested path must rebuild BOTH enclosing maps around the leaf; got {Py.Repr(deep)}");

        // --- an absent nested key omits the attribute, rather than returning an empty map ---
        var missing = (await Get(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("nested-map"))),
            ConsistentRead = true,
            ProjectionExpression = "#p.#nope",
            ExpressionAttributeNames = Names(("#p", "profile"), ("#nope", "not_a_key")),
        })).Item;
        Ensure(
            Py.Equal(missing, Item()),
            $"a nested path naming a key the map does not have must omit the attribute entirely, not return an empty parent map; got {Py.Repr(missing)}");

        // --- control: projecting the parent itself returns the whole map unchanged ---
        var whole = (await Get(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("nested-map"))),
            ConsistentRead = true,
            ProjectionExpression = "#p",
            ExpressionAttributeNames = Names(("#p", "profile")),
        })).Item;
        Ensure(
            Py.Equal(whole, Item(("profile", M(
                ("city", S("sea")),
                ("zip", S("98101")),
                ("unit", M(("floor", N("7")))))))),
            $"projecting the parent attribute must return the whole map unchanged; got {Py.Repr(whole)}");

        // --- control: an unprojected read still carries everything ---
        var full = (await Get(new GetItemRequest { TableName = table, Key = Item(("pk", S("nested-map"))), ConsistentRead = true })).Item;
        Ensure(
            full.Keys.ToHashSet().SetEquals(AllAttributes),
            $"an unprojected read must carry every attribute; got {Py.Repr(full.Keys.Order(StringComparer.Ordinal).ToList())}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-projnest-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
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
