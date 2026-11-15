// Translated from behavioral/dynamodb/check_describe_endpoints_returns_server_endpoints.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-endpoints-returns-server-endpoints</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeEndpointsReturnsServerEndpoints
{
    /// <summary>
    /// DescribeEndpoints is a server-side, no-input operation that returns the
    /// set of regional service endpoints DynamoDB manages; the response includes
    /// an address for the target region and is stable across repeated calls.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-endpoints-returns-server-endpoints")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var resp1 = await ddb.DescribeEndpointsAsync(new DescribeEndpointsRequest());
        var endpoints1 = resp1.Endpoints ?? [];
        Ensure(endpoints1.Count >= 1, "DescribeEndpoints returned no endpoints");
        foreach (var ep in endpoints1)
        {
            Ensure(!string.IsNullOrEmpty(ep.Address), "endpoint Address must not be empty");
            Ensure(ep.CachePeriodInMinutes is > 0, $"CachePeriodInMinutes must be a positive int, got {Py.Repr(ep.CachePeriodInMinutes)}");
        }

        var matchingRegion = endpoints1
            .Where(ep => ep.Address.Contains(ctx.Region, StringComparison.Ordinal) && ep.Address.Contains("dynamodb", StringComparison.Ordinal))
            .ToList();
        Ensure(matchingRegion.Count > 0, $"no endpoint address referenced region {ctx.Region}: {Py.Repr(endpoints1.Select(e => e.Address))}");

        var resp2 = await ddb.DescribeEndpointsAsync(new DescribeEndpointsRequest());
        var addrs1 = endpoints1.Select(ep => ep.Address).Order(StringComparer.Ordinal).ToList();
        var addrs2 = (resp2.Endpoints ?? []).Select(ep => ep.Address).Order(StringComparer.Ordinal).ToList();
        Ensure(addrs1.SequenceEqual(addrs2), "DescribeEndpoints returned a different endpoint set on an immediate repeat call");
    }
}
