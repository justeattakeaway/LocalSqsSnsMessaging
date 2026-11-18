// Translated from behavioral/dynamodb/check_concurrent_unconditional_list_append_loses_no_element.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-unconditional-list-append-loses-no-element</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentUnconditionalListAppendLosesNoElement
{
    private const string Check = "concurrent-unconditional-list-append-loses-no-element";
    private const int Workers = 24;
    private const double TableActiveBudgetS = 120.0;
    private const double TeardownWaitS = 120.0;

    /// <summary>
    /// 24 concurrent unconditional list_append updates on one item lose no element, and each
    /// UPDATED_NEW post-image is a distinct-length prefix of the final list ending in its own token,
    /// proving DynamoDB serialises writes to one item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-unconditional-list-append-loses-no-element")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        // No SDK retries: list_append is not idempotent, so a retry of a landed request appends twice.
        var racer = ctx.DynamoClient(maxAttempts: 1);
        var table = await CreateTable(ctx, ddb);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("race")));
        await ddb.PutItemAsync(table, Item(("pk", S("race")), ("l", L())));

        var tokens = Enumerable.Range(0, Workers).Select(i => $"w{i.ToString("00", CultureInfo.InvariantCulture)}").ToList();

        async Task<(string Token, List<string>? Post, string? Code)> Append(string token)
        {
            var (resp, error) = await FidelityContext.Capture(() => racer.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET l = list_append(l, :me)",
                ExpressionAttributeValues = Item((":me", L(S(token)))),
                ReturnValues = ReturnValue.UPDATED_NEW,
            }));
            if (error is not null)
            {
                return (token, null, error.Code);
            }

            return (token, [.. resp!.Attributes["l"].L.Select(e => e.S)], null);
        }

        (string Token, List<string>? Post, string? Code)[] results;
        using (ctx.Concurrently())
        {
            results = await Task.WhenAll(tokens.Select(t => Task.Run(() => Append(t), cancellationToken)));
        }

        var errors = results.Where(r => r.Code is not null).Select(r => (r.Token, r.Code)).ToList();
        Ensure(
            errors.Count == 0,
            $"{errors.Count} of {Workers} unconditional appends were refused: {Py.Repr(errors)}; an " +
            "unconditional UpdateItem on one item has nothing to conflict on");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item["l"].L.Select(e => e.S).ToList();
        Ensure(
            final.Order(StringComparer.Ordinal).SequenceEqual(tokens.Order(StringComparer.Ordinal)),
            $"the stored list holds {final.Count} element(s) {Py.Repr(final)}; expected each of the " +
            $"{Workers} tokens exactly once -- a lost or duplicated append");

        var lengths = results.Select(r => r.Post!.Count).Order().ToList();
        Ensure(
            lengths.SequenceEqual(Enumerable.Range(1, Workers)),
            $"post-image lengths were {Py.Repr(lengths)}; expected each of 1..{Workers} exactly once. Two " +
            "equal lengths mean two appends read the same list");
        foreach (var (token, post, _) in results)
        {
            Ensure(post![^1] == token, $"worker {Py.Repr(token)}'s post-image {Py.Repr(post)} does not end with its own token");
            Ensure(
                final.Take(post.Count).SequenceEqual(post),
                $"worker {Py.Repr(token)}'s post-image {Py.Repr(post)} is not a prefix of the final list {Py.Repr(final)}; " +
                "the writes were not serialised");
        }
    }

    private static async Task DeleteTableWhenPossible(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null)
            {
                return;
            }

            var code = error.Code;
            if (code == "ResourceNotFoundException")
            {
                return;
            }

            if (code == "ResourceInUseException" && clock.Elapsed.TotalSeconds < TeardownWaitS)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
                continue;
            }

            throw new InvalidOperationException($"dynamodb table {Py.Repr(tableName)} could not be deleted: {code}", error.Exception);
        }
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableWhenPossible(ddb, name));
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed.TotalSeconds < TableActiveBudgetS)
        {
            status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus.Value;
            if (status == "ACTIVE")
            {
                return name;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {Py.Repr(name)} not ACTIVE within {TableActiveBudgetS.ToString("0.0", CultureInfo.InvariantCulture)}s: {Py.Repr(status)}");
    }
}
