// Translated from behavioral/dynamodb/check_concurrent_tag_resource_no_lost_tags.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-tag-resource-no-lost-tags</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentTagResourceNoLostTags
{
    /// <summary>
    /// N concurrent TagResource calls, each adding a DISTINCT tag key to the SAME table ARN, must
    /// all durably apply -- a read-modify-write race on the resource's tag set must not silently drop
    /// a concurrently-added sibling key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-tag-resource-no-lost-tags")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var arn = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table.TableArn;
        const int n = 10;
        var keys = Enumerable.Range(0, n).Select(i => $"concurrent-tag-{i}").ToList();

        Func<Task<TagResourceResponse>> AddTag(string k) => () => ddb.TagResourceAsync(new TagResourceRequest
        {
            ResourceArn = arn,
            Tags = [new Tag { Key = k, Value = "v" }],
        });

        object[] results;
        using (ctx.Concurrently())
        {
            results = await RunConcurrent([.. keys.Select(AddTag)]);
        }

        var successes = ConcurrentSuccesses(results);
        Ensure(successes == n, $"expected all {n} TagResource calls to succeed, got {successes}: {Py.Repr(results.Select(r => r is Exception ex ? ex.Message : "ok"))}");

        var finalTags = await PollUntil(
            async () => (await ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = arn })).Tags.Select(tag => tag.Key).ToHashSet(StringComparer.Ordinal),
            present => present.IsSupersetOf(keys),
            timeoutS: 30,
            intervalS: 2.0,
            description: "all concurrently-added tag keys present");
        var missing = keys.Except(finalTags, StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0, $"lost tag keys under concurrent TagResource: {Py.Repr(missing)}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 30, double intervalS = 2.0, string description = "condition")
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

        throw Fail($"{description} not met within {timeoutS.ToString(CultureInfo.InvariantCulture)}s. Last result: {Py.Repr(last)}");
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

    private static int ConcurrentSuccesses(object[] results) => results.Count(r => r is not Exception);

    private static List<string?> ConcurrentErrorCodes(object[] results) =>
    [
        .. results.OfType<Exception>().Select(r => r is Amazon.Runtime.AmazonServiceException ex ? AwsError.From(ex).Code : r.GetType().Name),
    ];
}
