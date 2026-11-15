// Translated from behavioral/dynamodb/check_list_tables_pages_names_in_byte_order_across_punctuation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-tables-pages-names-in-byte-order-across-punctuation</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListTablesPagesNamesInByteOrderAcrossPunctuation
{
    // Name suffixes in UTF-8 byte order ('-' < '.' < 'Z' < '_' < 'b'), unlike locale collation.
    private static readonly string[] Suffixes = ["-a-z", "-a.z", "-aZ", "-a_z", "-ab"];

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>Table names after start that begin with prefix, following LastEvaluatedTableName.</summary>
    private static async Task<List<string>> Walk(IAmazonDynamoDB ddb, string start, string prefix, int limit)
    {
        var names = new List<string>();
        var pages = 0;
        var request = new ListTablesRequest { ExclusiveStartTableName = start, Limit = limit };
        while (true)
        {
            var resp = await ddb.ListTablesAsync(request);
            pages++;
            var page = resp.TableNames;
            Ensure(page.Count <= limit, $"ListTables Limit={limit} returned {page.Count} names: {Py.Repr(page)}");
            Ensure(page.SequenceEqual(page.Order(StringComparer.Ordinal)), $"ListTables page is not in byte order: {Py.Repr(page)}");
            names.AddRange(page.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)));
            var cursor = resp.LastEvaluatedTableName;
            if (cursor is null || (page.Count > 0 && !page[^1].StartsWith(prefix, StringComparison.Ordinal)))
            {
                return names;
            }

            Ensure(pages <= 50, $"ListTables walk from {Py.Repr(start)} did not end after 50 pages: {Py.Repr(names)}");
            request.ExclusiveStartTableName = cursor;
        }
    }

    /// <summary>
    /// ListTables orders names by UTF-8 bytes where punctuation and case disagree with collation
    /// (-a-z &lt; -a.z &lt; -aZ &lt; -a_z &lt; -ab); a Limit=1 cursor walk returns each of them once, in that order.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_ListTables.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-tables-pages-names-in-byte-order-across-punctuation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var prefix = FidelityContext.UniqueName("sim2real-behav-ddb-lt-");
        var names = Suffixes.Select(s => prefix + s).ToList();
        foreach (var name in Enumerable.Reverse(names))
        {
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = name,
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            });
            ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        }

        foreach (var name in names)
        {
            await WaitActive(ddb, ctx, name);
        }

        var walk = await Walk(ddb, prefix, prefix, 1);
        Ensure(walk.SequenceEqual(names), $"ListTables Limit=1 walk listed {Py.Repr(walk)}, expected byte order {Py.Repr(names)}");
        var page = await Walk(ddb, prefix, prefix, 100);
        Ensure(page.SequenceEqual(names), $"ListTables Limit=100 from {Py.Repr(prefix)} listed {Py.Repr(page)}, expected {Py.Repr(names)}");
    }
}
