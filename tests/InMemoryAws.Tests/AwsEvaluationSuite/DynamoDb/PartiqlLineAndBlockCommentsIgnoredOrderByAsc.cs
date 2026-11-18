// Translated from behavioral/dynamodb/check_partiql_line_and_block_comments_ignored_order_by_asc.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-line-and-block-comments-ignored-order-by-asc</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlLineAndBlockCommentsIgnoredOrderByAsc
{
    private const string CheckId = "partiql-line-and-block-comments-ignored-order-by-asc";
    private static readonly string[] Ascending = ["1", "2", "3"];

    /// <summary>Poll DescribeTable until ACTIVE; CreateTable is eventually consistent.</summary>
    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                status = "PENDING";
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status ?? "None"})");
    }

    /// <summary>
    /// ExecuteStatement ignores '--' line comments and '/* */' block comments anywhere in the
    /// statement, and ORDER BY sk ASC returns ascending sort-key order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-line-and-block-comments-ignored-order-by-asc")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-pqlcmt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitForTableActive(ddb, table);

        for (var sk = 1; sk <= 3; sk++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("A")), ("sk", N(sk.ToString(CultureInfo.InvariantCulture)))));
        }

        var keyedItem = Item(("pk", S("A")), ("sk", N("2")));

        var forms = new (string Name, string Statement)[]
        {
            ("leading --", $"-- a leading comment\nSELECT * FROM \"{table}\" WHERE pk='A' AND sk=2"),
            ("trailing --", $"SELECT * FROM \"{table}\" WHERE pk='A' AND sk=2 -- a trailing comment"),
            ("-- between FROM and WHERE", $"SELECT * FROM \"{table}\" -- between FROM and WHERE\nWHERE pk='A' AND sk=2"),
            ("/* */ block comment", $"SELECT * FROM \"{table}\" /* a block comment */ WHERE pk='A' AND sk=2"),
        };
        foreach (var (name, statement) in forms)
        {
            var result = await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = statement, ConsistentRead = true });
            var items = result.Items;
            Ensure(Py.Equal(items, new[] { keyedItem }), $"{name}: expected exactly the one keyed item {Py.Repr(keyedItem)}, got {Py.Repr(items)}");
        }

        var ascending = (await ddb.ExecuteStatementAsync(new ExecuteStatementRequest { Statement = $"SELECT * FROM \"{table}\" WHERE pk='A' ORDER BY sk ASC", ConsistentRead = true })).Items;
        var ascendingKeys = ascending.Select(i => i["sk"].N).ToList();
        Ensure(ascendingKeys.SequenceEqual(Ascending), $"ORDER BY sk ASC must return sort keys in ascending order (1, 2, 3), got {Py.Repr(ascendingKeys)}");
    }
}
