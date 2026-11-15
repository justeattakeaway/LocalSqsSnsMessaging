// Translated from behavioral/dynamodb/check_batch_execute_statement_throttling_enforced.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-execute-statement-throttling-enforced</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchExecuteStatementThrottlingEnforced
{
    private static readonly HashSet<string> ThrottleCodes = new(StringComparer.Ordinal)
    {
        "ThrottlingException",
        "ProvisionedThroughputExceeded",
        "ProvisionedThroughputExceededException",
        "RequestLimitExceeded",
    };

    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = "sim2real:managed", Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
    ];

    /// <summary>
    /// Concurrent PartiQL INSERT batches against a 1-WCU table draw a throttling-class error, after
    /// a lone 2-statement control batch succeeds: BatchExecuteStatement enforces provisioned capacity.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbThroughput, "provisioned throughput is never enforced, so concurrent PartiQL batches against a 1-WCU table are never throttled")]
    [DisplayName("dynamodb.batch-execute-statement-throttling-enforced")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await SimpleTable(
            ddb,
            ctx,
            billingMode: BillingMode.PROVISIONED,
            provisionedThroughput: new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 });

        var control = await ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest
        {
            Statements =
            [
                new BatchStatementRequest
                {
                    Statement = $"INSERT INTO \"{name}\" VALUE {{'pk': ?, 'v': ?}}",
                    Parameters = [S("control-0"), S("x")],
                },
                new BatchStatementRequest
                {
                    Statement = $"INSERT INTO \"{name}\" VALUE {{'pk': ?, 'v': ?}}",
                    Parameters = [S("control-1"), S("x")],
                },
            ],
        });
        var controlErrors = (control.Responses ?? []).Where(item => item.Error is not null).Select(item => item.Error.Message).ToList();
        Ensure(
            controlErrors.Count == 0,
            $"a lone unhurried 2-statement batch was rejected: {Py.Repr(controlErrors)} -- the target " +
            "refuses BatchExecuteStatement outright, independent of load");

        Task<BatchExecuteStatementResponse> Burst(int wave, int batchIdx)
        {
            var statements = Enumerable.Range(0, 25).Select(i => new BatchStatementRequest
            {
                Statement = $"INSERT INTO \"{name}\" VALUE {{'pk': ?, 'v': ?}}",
                Parameters = [S($"throttle-{wave}-{batchIdx}-{i}"), S(new string('x', 2000))],
            }).ToList();
            return ddb.BatchExecuteStatementAsync(new BatchExecuteStatementRequest { Statements = statements });
        }

        async Task<HashSet<string?>> Wave(int wave)
        {
            object[] results;
            using (ctx.Concurrently())
            {
                results = await RunConcurrent(Enumerable.Range(0, 16).Select(i => (Func<Task<BatchExecuteStatementResponse>>)(() => Burst(wave, i))).ToList());
            }

            var observed = new HashSet<string?>(StringComparer.Ordinal);
            foreach (var r in results)
            {
                switch (r)
                {
                    case Amazon.Runtime.AmazonServiceException ex:
                        observed.Add(AwsError.From(ex).Code);
                        break;
                    case Exception ex:
                        observed.Add(ex.GetType().Name);
                        break;
                    case BatchExecuteStatementResponse resp:
                        foreach (var item in resp.Responses ?? [])
                        {
                            if (item.Error is { } err)
                            {
                                observed.Add(err.Code?.Value);
                            }
                        }

                        break;
                }
            }

            return observed;
        }

        var observed = await Wave(0);
        // A second wave, because a fresh table's burst credit can absorb the first one.
        if (!observed.Overlaps(ThrottleCodes))
        {
            observed.UnionWith(await Wave(1));
        }

        Ensure(
            observed.Overlaps(ThrottleCodes),
            "expected server-side throttling under load against a 1-WCU table, " +
            $"observed error codes: {(observed.Count == 0 ? "set()" : "{" + string.Join(", ", observed.Select(Py.Repr)) + "}")}");
    }

    /// <summary>Poll DescribeTable until the table AND every GSI on it report ACTIVE.</summary>
    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutS = 90.0, double intervalS = 1.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table;
            if (last.TableStatus == TableStatus.ACTIVE && (last.GlobalSecondaryIndexes ?? []).All(idx => (idx.IndexStatus?.Value ?? "ACTIVE") == "ACTIVE"))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutS.ToString("0.0", CultureInfo.InvariantCulture)}s: {Py.Repr(last?.TableStatus)}");
    }

    /// <summary>Create a single-"pk" (S) hash-key table, register teardown, wait ACTIVE, return the name.</summary>
    private static async Task<string> SimpleTable(
        IAmazonDynamoDB ddb,
        FidelityContext ctx,
        string prefix = "sim2real-behav-ddb-tbl-",
        BillingMode? billingMode = null,
        ProvisionedThroughput? provisionedThroughput = null)
    {
        billingMode ??= BillingMode.PAY_PER_REQUEST;
        var name = FidelityContext.UniqueName(prefix);
        var request = new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = billingMode,
            Tags = TagsKv(),
        };
        if (billingMode == BillingMode.PROVISIONED)
        {
            request.ProvisionedThroughput = provisionedThroughput ?? new ProvisionedThroughput { ReadCapacityUnits = 5, WriteCapacityUnits = 5 };
        }

        await ddb.CreateTableAsync(request);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }

    /// <summary>Runs every fn concurrently; each result is its response or the exception it raised.</summary>
    private static async Task<object[]> RunConcurrent<T>(IReadOnlyList<Func<Task<T>>> fns)
        where T : class =>
        await Task.WhenAll(fns.Select(fn => Task.Run<object>(async () =>
        {
            try
            {
                return await fn();
            }
#pragma warning disable CA1031 // as the Python check, every failure is recorded as a result
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        })));
}
