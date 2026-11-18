// Translated from behavioral/dynamodb/check_describe_endpoints.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-endpoints</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeEndpoints
{
    private const string Check = "describe-endpoints";

    private static async Task WaitTableDeleted(IAmazonDynamoDB ddb, string name, double timeoutSeconds = 90)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name }));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"table {name} not deleted within {timeoutSeconds}s");
    }

    private static async Task<List<Endpoint>> GetEndpoints(IAmazonDynamoDB ddb)
    {
        var resp = await ddb.DescribeEndpointsAsync(new DescribeEndpointsRequest());
        var endpoints = resp.Endpoints ?? [];
        Ensure(endpoints.Count > 0, "{'Endpoints': []}");
        foreach (var e in endpoints)
        {
            Ensure(!string.IsNullOrEmpty(e.Address), $"{{'Address': {Py.Repr(e.Address)}, 'CachePeriodInMinutes': {Py.Repr(e.CachePeriodInMinutes)}}}");
            Ensure(e.CachePeriodInMinutes is > 0, $"{{'Address': {Py.Repr(e.Address)}, 'CachePeriodInMinutes': {Py.Repr(e.CachePeriodInMinutes)}}}");
        }

        return endpoints;
    }

    /// <summary>
    /// DescribeEndpoints returns a non-empty list of endpoints, each with an address and a positive
    /// CachePeriodInMinutes, and the same addresses before, during and after a table's life.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-endpoints")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var before = await GetEndpoints(ddb);

        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var during = await GetEndpoints(ddb);

        await ddb.DeleteTableAsync(t);
        await WaitTableDeleted(ddb, t);
        var after = await GetEndpoints(ddb);

        // The endpoint list is a SERVICE-level constant, not a resource's state: the
        // exact address set must be identical whether no table, one table, or a
        // just-deleted table exists in the account.
        var beforeAddrs = before.Select(e => e.Address).Order(StringComparer.Ordinal).ToList();
        var duringAddrs = during.Select(e => e.Address).Order(StringComparer.Ordinal).ToList();
        var afterAddrs = after.Select(e => e.Address).Order(StringComparer.Ordinal).ToList();
        Ensure(
            beforeAddrs.SequenceEqual(duringAddrs) && duringAddrs.SequenceEqual(afterAddrs),
            $"endpoint addresses changed across table lifecycle: before={Py.Repr(beforeAddrs)} during={Py.Repr(duringAddrs)} after={Py.Repr(afterAddrs)}");
    }
}
