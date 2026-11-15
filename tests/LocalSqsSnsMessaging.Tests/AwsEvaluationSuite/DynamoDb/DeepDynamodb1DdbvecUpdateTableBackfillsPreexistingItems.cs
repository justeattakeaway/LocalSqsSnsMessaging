// Translated from behavioral/dynamodb/check_deep_dynamodb_1_ddbvec_update_table_backfills_preexisting_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deep-dynamodb-1-ddbvec-update-table-backfills-preexisting-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepDynamodb1DdbvecUpdateTableBackfillsPreexistingItems
{
    private const string CheckId = "deep-dynamodb-1-ddbvec-update-table-backfills-preexisting-items";
    private const int NPreexisting = 30;
    private static readonly double[] Query = [0.0, 0.0, 0.0, 0.0];
    private static readonly double[] MidVector = [0.5, 0.0, 0.0, 0.0];

    // On AWS, Backfilling turns true about 30 s after UpdateTable and the index reaches ACTIVE about
    // 510 s after it, for 30 items. The ceiling is generous on purpose.
    private const double BackfillBudgetSeconds = 1500;

    /// <summary>Index additions tried before the check gives up on observing the CREATING and Backfilling windows.</summary>
    private const int Attempts = 3;

    /// <summary>Seconds after UpdateTable during which the state is read at a short interval.</summary>
    private const double FastPollSeconds = 30;

    // Distinct euclidean distances from the origin, so the nearest-first order is a total order.
    private static double[] VecFor(int i) => [1.0 + i, 0.5 * (i % 3), 0.25, 0.0];

    private static double Euclid(double[] u, double[] v) => Math.Sqrt(u.Zip(v).Sum(p => Math.Pow(p.First - p.Second, 2)));

    /// <summary>
    /// A vector index added by UpdateTable refuses searches while CREATING, passes CREATING with Backfilling true to
    /// ACTIVE, and then returns all 30 pre-existing items plus one written mid-backfill, nearest-first.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbVectorSearch, "UpdateTable doesn't take VectorIndexUpdates: it answers that no supported update was given")]
    [DisplayName("dynamodb.deep-dynamodb-1-ddbvec-update-table-backfills-preexisting-items")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbvec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });

        // DeleteTable is refused (ResourceInUseException) while a vector index is CREATING, so a
        // plain delete registered here would leak the table whenever the check fails mid-backfill.
        ctx.OnCleanup(() => DeleteTableWhenSettled(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        Ensure(
            (await ddb.DescribeTableAsync(table)).Table.VectorIndexes is not { Count: > 0 },
            "a table created without VectorIndexes already reports one");
        var vectors = Enumerable.Range(0, NPreexisting).ToDictionary(i => $"item-{i:00}", VecFor, StringComparer.Ordinal);
        foreach (var (pk, vec) in vectors)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("emb", new AttributeValue { L = Vec(vec) }), ("payload", S($"p-{pk}"))));
        }

        var scanned = (await ddb.ScanAsync(new ScanRequest { TableName = table, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(scanned == NPreexisting, $"expected {NPreexisting} items before the index, got {scanned}");

        // Each attempt adds the index and watches it to ACTIVE. An attempt that misses the CREATING
        // refusal or the Backfilling window drops the index and adds it again, up to Attempts times,
        // while the budget left holds a full attempt (the longest add-to-ACTIVE seen so far).
        var refused = false;
        var tried = new List<string>();
        var took = new List<double>();
        var states = new List<(string? Status, bool Backfilling)>();
        var midWritten = false;
        VectorIndexDescription? final = null;
        var budgetSeconds = Bound(ctx, BackfillBudgetSeconds);
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(budgetSeconds);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0)
            {
                await DropIndex(ddb, table, clock, deadline);
                var left = (deadline - clock.Elapsed).TotalSeconds;
                Ensure(
                    left >= took.Max(),
                    $"the CREATING refusal and the Backfilling window were never both observed in "
                    + $"{attempt} attempts; {left.ToString("F0", CultureInfo.InvariantCulture)}s left cannot hold another ({took.Max().ToString("F0", CultureInfo.InvariantCulture)}s to "
                    + $"ACTIVE): {Py.Repr(tried)}");
            }

            await AddIndex(ddb, table);
            var armed = clock.Elapsed;
            if (!refused)
            {
                // The model: "The index must be in the ACTIVE state." A CREATING index is not searchable.
                var err = await Outcome(() => ddb.SearchVectorsAsync(new SearchVectorsRequest
                {
                    TableName = table,
                    IndexName = "late-euc",
                    TopK = 5,
                    SearchVector = Vec(Query),
                }));
                if (err == ("ValidationException", "The table does not have the specified index: late-euc"))
                {
                    refused = true;
                }
                else
                {
                    var now = (await Index(ddb, table))?.IndexStatus?.ToString();
                    Ensure(
                        now != "CREATING",
                        $"searching a vector index that stayed CREATING answered {Py.Repr(err)}; expected "
                        + "ValidationException 'The table does not have the specified index: late-euc'");
                    tried.Add($"search answered {Py.Repr(err)} with the index already {Py.Repr(now)}");
                }
            }

            // Poll to completion, recording every distinct (IndexStatus, Backfilling) state seen.
            states = [];
            midWritten = false;
            final = null;
            while (clock.Elapsed < deadline)
            {
                var tbl = (await ddb.DescribeTableAsync(table)).Table;
                var cur = (tbl.VectorIndexes ?? []).Where(v => v.IndexName == "late-euc").ToList();
                Ensure(cur.Count > 0, $"the vector index vanished while being created: {Py.Repr(tbl.VectorIndexes?.Select(IndexDict).ToList())}");
                final = cur[0];
                var state = (final.IndexStatus?.ToString(), final.Backfilling ?? false);
                if (states.Count == 0 || states[^1] != state)
                {
                    states.Add(state);
                }

                if (state == ("CREATING", true) && !midWritten)
                {
                    await ddb.PutItemAsync(table, Item(
                        ("pk", S("mid-backfill")),
                        ("emb", new AttributeValue { L = Vec(MidVector) }),
                        ("payload", S("p-mid-backfill"))));
                    midWritten = true;
                }

                if (state.Item1 == "ACTIVE" && !state.Item2 && tbl.TableStatus == TableStatus.ACTIVE)
                {
                    break;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds((clock.Elapsed - armed).TotalSeconds < FastPollSeconds ? 0.2 : 1));
            }

            Ensure(
                final?.IndexStatus?.ToString() == "ACTIVE",
                $"the vector index never became ACTIVE within {budgetSeconds.ToString("F0", CultureInfo.InvariantCulture)}s; states seen {Py.Repr(states)}");
            if (refused && midWritten)
            {
                break;
            }

            tried.Add($"attempt {attempt + 1} states {Py.Repr(states)}");
            took.Add((clock.Elapsed - armed).TotalSeconds);
        }

        Ensure(refused, $"SearchVectors was never refused while the index was CREATING: {Py.Repr(tried)}");
        Ensure(
            states.Contains(("CREATING", true)),
            $"a vector index created over {NPreexisting} existing items never reported "
            + $"Backfilling=true in {Attempts} attempts: {Py.Repr(tried)}");
        Ensure(states[^1] == ("ACTIVE", false), $"backfill did not end ACTIVE/not-backfilling: {Py.Repr(states)}");
        Ensure(
            states.IndexOf(("CREATING", true)) < states.IndexOf(("ACTIVE", false)),
            $"Backfilling=true must precede ACTIVE: {Py.Repr(states)}");
        Ensure(midWritten, $"no write landed during the backfill window; states {Py.Repr(states)}");
        foreach (var (field, val) in new[] { ("ItemCount", final?.ItemCount), ("IndexSizeBytes", final?.IndexSizeBytes) })
        {
            Ensure(val is >= 0, $"{field} on the ACTIVE index was {Py.Repr(val)}");
        }

        var expected = vectors.Keys.Append("mid-backfill")
            .OrderBy(pk => Euclid(Query, vectors.GetValueOrDefault(pk, MidVector)))
            .ToList();
        List<SearchResultItem> res = [];
        var searchClock = FidelityTime.StartNew();
        while (searchClock.Elapsed < TimeSpan.FromSeconds(120))
        {
            res = (await ddb.SearchVectorsAsync(new SearchVectorsRequest
            {
                TableName = table,
                IndexName = "late-euc",
                TopK = 100,
                SearchVector = Vec(Query),
            })).SearchResults ?? [];
            if (res.Count >= expected.Count)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var got = res.Select(r => r.Item["pk"].S).ToList();
        var missing = expected.Except(got, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(
            missing.Count == 0,
            $"{missing.Count} items were never backfilled into the new index: {Py.Repr(missing)} "
            + $"(returned {got.Count})");
        Ensure(got.SequenceEqual(expected), $"backfilled index order {Py.Repr(got)} is not nearest-first {Py.Repr(expected)}");
        foreach (var r in res)
        {
            var pk = r.Item["pk"].S;
            Ensure(
                Py.Equal(r.Item.GetValueOrDefault("payload"), S($"p-{pk}")),
                $"backfilled item {Py.Repr(pk)} lost its projected payload: {Py.Repr(r.Item)}");
        }
    }

    private static async Task AddIndex(IAmazonDynamoDB ddb, string table)
    {
        var resp = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            VectorIndexUpdates =
            [
                new VectorIndexUpdate
                {
                    Create = new CreateVectorIndexAction
                    {
                        IndexName = "late-euc",
                        VectorAttribute = new VectorAttributeDefinition { AttributeName = "emb" },
                        Dimensions = 4,
                        DistanceFunction = "EUCLIDEAN",
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                },
            ],
        });
        var vis = resp.TableDescription.VectorIndexes ?? [];
        Ensure(vis.Select(v => v.IndexName).SequenceEqual(["late-euc"]), $"UpdateTable described {Py.Repr(vis.Select(IndexDict).ToList())}");
        Ensure(
            vis[0].IndexStatus?.ToString() == "CREATING",
            $"a just-requested vector index must be CREATING, was {Py.Repr(vis[0].IndexStatus?.ToString())}");
        Ensure(
            vis[0].Dimensions == 4 && vis[0].DistanceFunction?.ToString() == "EUCLIDEAN",
            $"UpdateTable echoed {Py.Repr(IndexDict(vis[0]))}");
    }

    private static async Task<VectorIndexDescription?> Index(IAmazonDynamoDB ddb, string table)
    {
        var tbl = (await ddb.DescribeTableAsync(table)).Table;
        return (tbl.VectorIndexes ?? []).FirstOrDefault(v => v.IndexName == "late-euc");
    }

    /// <summary>Delete the vector index and wait until the table no longer lists it and is ACTIVE.</summary>
    private static async Task DropIndex(IAmazonDynamoDB ddb, string table, FidelityStopwatch clock, TimeSpan deadline)
    {
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            VectorIndexUpdates = [new VectorIndexUpdate { Delete = new DeleteVectorIndexAction { IndexName = "late-euc" } }],
        });
        while (true)
        {
            var tbl = (await ddb.DescribeTableAsync(table)).Table;
            var names = (tbl.VectorIndexes ?? []).Select(v => v.IndexName).ToList();
            if (!names.Contains("late-euc") && tbl.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            if (clock.Elapsed >= deadline)
            {
                throw Fail($"the dropped vector index is still listed: {Py.Repr(tbl.VectorIndexes?.Select(IndexDict).ToList())}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// (code, message) of the service error <paramref name="call"/> raises, or ('&lt;accepted&gt;', repr of the answer)
    /// when the target accepts the call.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Outcome<T>(Func<Task<T>> call)
    {
        T result;
        try
        {
            result = await call();
        }
        catch (AmazonServiceException ex)
        {
            var error = AwsError.From(ex);
            return (error.Code, error.Message);
        }
        catch (Exception ex)
        {
            throw new FidelityAssertionException($"raised a non-service error {ex}", ex);
        }

        return ("<accepted>", Convert.ToString(result, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// capSeconds, bounded by the runner's remaining time less a reserve (a quarter, at most 150 s) for
    /// the search and teardown.
    /// </summary>
    private static double Bound(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - Math.Min(150.0, left / 4)));
    }

    private static Dictionary<string, object?> IndexDict(VectorIndexDescription v) => new(StringComparer.Ordinal)
    {
        ["IndexName"] = v.IndexName,
        ["IndexStatus"] = v.IndexStatus?.ToString(),
        ["Dimensions"] = v.Dimensions,
        ["DistanceFunction"] = v.DistanceFunction?.ToString(),
        ["Backfilling"] = v.Backfilling,
    };
    private static async Task DeleteTableWhenSettled(IAmazonDynamoDB ddb, string table, int timeoutSeconds = 1500)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary><c>[{'N': repr(float(x))} for x in values]</c>.</summary>
    private static List<AttributeValue> Vec(IEnumerable<double> values) => values.Select(x => N(PyFloat(x))).ToList();

    private static string PyFloat(double x)
    {
        var text = x.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }
}
