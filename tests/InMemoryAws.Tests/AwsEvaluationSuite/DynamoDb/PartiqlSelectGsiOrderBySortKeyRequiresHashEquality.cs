// Translated from behavioral/dynamodb/check_partiql_select_gsi_order_by_sort_key_requires_hash_equality.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-select-gsi-order-by-sort-key-requires-hash-equality</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlSelectGsiOrderBySortKeyRequiresHashEquality
{
    private const string CheckId = "partiql-select-gsi-order-by-sort-key-requires-hash-equality";

    private static readonly (string Code, string Message) NoHashKey = (
        "ValidationException",
        "Must have at least one non-optional hash key condition in WHERE clause when using ORDER BY clause.");

    private static readonly (string Code, string Message) NoWhere = (
        "ValidationException",
        "Must have WHERE clause in the statement when using ORDER BY clause.");

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}: {Py.Repr(error.Message)}");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, double budgetS, double pollS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed.TotalSeconds >= budgetS)
            {
                throw Fail($"condition not met within {budgetS}s; last value {value}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(pollS));
        }
    }

    private static int Gsk(Dictionary<string, AttributeValue> item) => int.Parse(item["gsk"].N, CultureInfo.InvariantCulture);

    /// <summary>
    /// A SELECT through a GSI qualifier with hash-key equality and ORDER BY on the index sort key
    /// returns items in that sort-key order; without a hash-key equality condition, or without a
    /// WHERE clause at all, the same ORDER BY is refused with its exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-select-gsi-order-by-sort-key-requires-hash-equality")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqgob-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("gsk", ScalarAttributeType.N),
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await Poll(
            async () => (await ddb.DescribeTableAsync(table)).Table,
            t => t.TableStatus == TableStatus.ACTIVE && t.GlobalSecondaryIndexes.All(g => g.IndexStatus == IndexStatus.ACTIVE),
            budgetS: BudgetS(ctx, 600.0),
            pollS: 5.0);

        // Partition g1 holds three items whose gsk values are not in insertion order, so a correct
        // ascending/descending answer cannot come from echoing insertion order.
        List<Dictionary<string, AttributeValue>> mainItems =
        [
            Item(("pk", S("p1")), ("sk", S("a")), ("gpk", S("g1")), ("gsk", N("30")), ("tag", S("t"))),
            Item(("pk", S("p2")), ("sk", S("b")), ("gpk", S("g1")), ("gsk", N("10")), ("tag", S("t"))),
            Item(("pk", S("p3")), ("sk", S("c")), ("gpk", S("g1")), ("gsk", N("20")), ("tag", S("t"))),
        ];

        // A sibling partition the WHERE equality must exclude.
        var sibling = Item(("pk", S("p4")), ("sk", S("d")), ("gpk", S("g2")), ("gsk", N("15")), ("tag", S("t")));
        foreach (var item in mainItems.Append(sibling))
        {
            await ddb.PutItemAsync(table, item);
        }

        // The GSI fans out asynchronously: poll until every written item, sibling included, is in it.
        var written = mainItems.Append(sibling).OrderBy(i => i["gsk"].N, StringComparer.Ordinal).ToList();
        await Poll(
            async () => (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"gsi1\"" })).Items
                .OrderBy(i => i["gsk"].N, StringComparer.Ordinal).ToList(),
            items => Py.Equal(items, written),
            budgetS: BudgetS(ctx, 60.0),
            pollS: 2.0);

        var ascending = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"gsi1\" WHERE gpk = 'g1' ORDER BY gsk ASC",
        })).Items;
        var wantAsc = mainItems.OrderBy(Gsk).ToList();
        Ensure(
            Py.Equal(ascending, wantAsc),
            $"ORDER BY gsk ASC through the GSI must return gsk 10, 20, 30 in that order.\nwant: {Py.Repr(wantAsc)}\ngot:  {Py.Repr(ascending)}");

        var descending = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"SELECT * FROM \"{table}\".\"gsi1\" WHERE gpk = 'g1' ORDER BY gsk DESC",
        })).Items;
        var wantDesc = mainItems.OrderByDescending(Gsk).ToList();
        Ensure(
            Py.Equal(descending, wantDesc),
            $"ORDER BY gsk DESC through the GSI must return gsk 30, 20, 10 in that order.\nwant: {Py.Repr(wantDesc)}\ngot:  {Py.Repr(descending)}");

        // Positive control: the same non-key filter, without ORDER BY, succeeds through the GSI and
        // returns every tagged item regardless of partition - so the refusal below is about ORDER BY,
        // not about this filter shape.
        var filtered = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"gsi1\" WHERE tag = 't'" })).Items;
        Ensure(
            Py.Equal(filtered.OrderBy(i => i["gsk"].N, StringComparer.Ordinal).ToList(), written),
            $"a non-key filter without ORDER BY must return every matching item, got {Py.Repr(filtered)}");

        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"gsi1\" WHERE tag = 't' ORDER BY gsk ASC" }),
            NoHashKey);
        await ExpectError(
            () => ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\".\"gsi1\" ORDER BY gsk ASC" }),
            NoWhere);
    }
}
