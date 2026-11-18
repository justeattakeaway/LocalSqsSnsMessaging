// Translated from behavioral/dynamodb/check_ttl_configuration.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ttl-configuration</summary>
[Category("AwsEvaluationSuite")]
public sealed class TtlConfiguration
{
    private const string Check = "ttl-configuration";

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T?, string> repr, double timeoutS = 90, double intervalS = 3.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {repr(last)}");
    }

    private static string TtlRepr(TimeToLiveDescription? d)
    {
        if (d is null)
        {
            return "None";
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (d.TimeToLiveStatus is not null)
        {
            dict["TimeToLiveStatus"] = d.TimeToLiveStatus;
        }

        if (d.AttributeName is not null)
        {
            dict["AttributeName"] = d.AttributeName;
        }

        return Py.Repr(dict);
    }

    private static async Task DeleteTableIfPresent(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// Poll fn() (a read against the deleted table) until it raises
    /// ResourceNotFoundException. DeleteTable is async: the table lingers in
    /// DELETING and reads keep succeeding for a while, so an immediate read
    /// after DeleteTable is not evidence of anything.
    /// </summary>
    private static async Task PollNotFound(Func<Task> fn, string expectedMessage, string description)
    {
        async Task<bool> Probe()
        {
            var error = await FidelityContext.CaptureError(fn);
            if (error is null)
            {
                return false;
            }

            Ensure(error.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException for {description}, got {error.Code}");
            Ensure(error.Message == expectedMessage, $"expected {Py.Repr(expectedMessage)} for {description}, got {Py.Repr(error.Message)}");
            return true;
        }

        await PollUntil(Probe, gone => gone, gone => gone ? "True" : "False", timeoutS: 180, intervalS: 3.0, description: description);
    }

    /// <summary>
    /// A new table's TTL is DISABLED and UpdateTimeToLive converges to ENABLED on expires_at; after DeleteTable,
    /// DescribeTable and DescribeTimeToLive raise ResourceNotFoundException with the exact message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ttl-configuration")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableIfPresent(ddb, table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var baseline = (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = table })).TimeToLiveDescription;
        Ensure(baseline.TimeToLiveStatus == TimeToLiveStatus.DISABLED, $"fresh table must start DISABLED, got {TtlRepr(baseline)}");

        await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expires_at" },
        });

        var ttl = await PollUntil(
            async () => (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = table })).TimeToLiveDescription,
            d => d.TimeToLiveStatus == TimeToLiveStatus.ENABLED,
            TtlRepr,
            description: "TTL to converge to the terminal ENABLED status");
        Ensure(ttl.AttributeName == "expires_at", $"expected AttributeName='expires_at', got {TtlRepr(ttl)}");

        await ddb.DeleteTableAsync(table);

        var notFound = $"Requested resource not found: Table: {table} not found";
        await PollNotFound(
            () => ddb.DescribeTableAsync(table),
            expectedMessage: notFound,
            description: "describe_table to report the deleted table not-found");
        await PollNotFound(
            () => ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = table }),
            expectedMessage: notFound,
            description: "describe_time_to_live to report the deleted table not-found");
    }
}
