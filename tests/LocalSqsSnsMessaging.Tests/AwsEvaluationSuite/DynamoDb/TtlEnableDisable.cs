// Translated from behavioral/dynamodb/check_ttl_enable_disable.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ttl-enable-disable</summary>
[Category("AwsEvaluationSuite")]
public sealed class TtlEnableDisable
{
    private const string Check = "ttl-enable-disable";

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T?, string> repr, double timeoutS = 180, double intervalS = 5.0, string description = "condition")
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

    /// <summary>
    /// TTL can be enabled; DescribeTimeToLive converges to ENABLED with the configured attribute.
    ///
    /// Note: real AWS requires ~1 hour between enable and disable, so we only
    /// test enabling and verifying the describe response.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ttl-enable-disable")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "expires_at" },
        });

        async Task<TimeToLiveDescription> Probe() =>
            (await ddb.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = table })).TimeToLiveDescription;

        var ttl = await PollUntil(
            Probe,
            t => t.TimeToLiveStatus == TimeToLiveStatus.ENABLED,
            TtlRepr,
            description: $"TTL on {table} reaching ENABLED");
        Ensure(ttl.TimeToLiveStatus == TimeToLiveStatus.ENABLED, TtlRepr(ttl));
        Ensure(ttl.AttributeName == "expires_at", TtlRepr(ttl));
    }
}
