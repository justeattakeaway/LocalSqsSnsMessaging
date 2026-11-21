// Translated from behavioral/dynamodb/check_list_tables_limit_two_walk_matches_the_default_page_walk.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-tables-limit-two-walk-matches-the-default-page-walk</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListTablesLimitTwoWalkMatchesTheDefaultPageWalk
{
    private const int Tables = 5;
    private const int Page = 2;
    private const int PageCap = 5000; // a walk that never drops LastEvaluatedTableName fails instead of spinning

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>DescribeTable polled until the table is ACTIVE.</summary>
    private static async Task WaitActive(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 240.0);
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(name)).Table.TableStatus?.Value;
            if (status == "ACTIVE")
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= deadline)
            {
                if (BudgetSpent(ctx))
                {
                    throw new TimeoutException($"the run budget ended before table {name} became ACTIVE");
                }

                throw Fail($"table {name} is still {Py.Repr(status)}, not ACTIVE");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A cleanup deleting the table; a table still being created is retried until it can be deleted.</summary>
    private static Func<Task> DeleteTable(IAmazonDynamoDB ddb, string name) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await ddb.DeleteTableAsync(name);
                return;
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            catch (ResourceInUseException) when (clock.Elapsed.TotalSeconds < 240.0)
            {
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Every ListTables page in order, following LastEvaluatedTableName until a page carries none.</summary>
    private static async Task<List<List<string>>> Walk(IAmazonDynamoDB ddb, int? limit)
    {
        var pages = new List<List<string>>();
        var request = new ListTablesRequest();
        if (limit is { } l)
        {
            request.Limit = l;
        }

        while (true)
        {
            var resp = await ddb.ListTablesAsync(request);
            pages.Add([.. resp.TableNames ?? []]);
            var cursor = resp.LastEvaluatedTableName;
            if (string.IsNullOrEmpty(cursor))
            {
                return pages;
            }

            Ensure(pages.Count < PageCap, $"ListTables still returned a cursor after {pages.Count} pages");
            request.ExclusiveStartTableName = cursor;
        }
    }

    private static List<string> Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

    /// <summary>
    /// ListTables walked at Limit=2 to its last page lists the check's tables once each, exactly as
    /// the walk at the default page size lists them.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-tables-limit-two-walk-matches-the-default-page-walk")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var stem = FidelityContext.UniqueName("sim2real-ltpw-");
        var own = Sorted(Enumerable.Range(0, Tables).Select(i => $"{stem}-{i}"));
        foreach (var name in own)
        {
            await ddb.CreateTableAsync(new CreateTableRequest
            {
                TableName = name,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            });
            ctx.OnCleanup(DeleteTable(ddb, name));
        }

        foreach (var name in own)
        {
            await WaitActive(ctx, ddb, name);
        }

        var clock = FidelityTime.StartNew();
        var deadline = BudgetS(ctx, 60.0);
        List<string> whole;
        List<List<string>> small;
        List<string> paged;
        while (true)
        {
            whole = [.. (await Walk(ddb, null)).SelectMany(p => p).Where(own.Contains)];
            small = await Walk(ddb, Page);
            paged = [.. small.SelectMany(p => p).Where(own.Contains)];
            if (Sorted(whole).SequenceEqual(own) && Sorted(paged).SequenceEqual(own))
            {
                break;
            }

            if (clock.Elapsed.TotalSeconds >= deadline)
            {
                if (BudgetSpent(ctx))
                {
                    throw new TimeoutException("the run budget ended before both ListTables walks listed the tables");
                }

                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(Sorted(whole).SequenceEqual(own), $"the default-page walk listed {Py.Repr(Sorted(whole))}, expected {Py.Repr(own)}");
        var sizes = small.Select(p => p.Count).ToList();
        Ensure(sizes.All(size => size <= Page), $"a Limit={Page} page held more: page sizes {Py.Repr(sizes)}");
        Ensure(paged.Count == paged.Distinct().Count(), $"the Limit={Page} walk listed a table twice: {Py.Repr(paged)}");
        Ensure(
            Sorted(paged).SequenceEqual(Sorted(whole)),
            $"the Limit={Page} walk listed {Py.Repr(Sorted(paged))}, the default-page walk {Py.Repr(Sorted(whole))}");
        Ensure(small.Count >= 3, $"{Tables} tables at Limit={Page} gave only {small.Count} page(s)");
    }
}
