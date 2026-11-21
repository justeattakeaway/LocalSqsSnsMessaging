// Translated from behavioral/dynamodb/check_concurrent_add_returns_exact_permutation_of_counter_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-add-returns-exact-permutation-of-counter-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentAddReturnsExactPermutationOfCounterValues
{
    private const string Check = "concurrent-add-returns-exact-permutation-of-counter-values";
    private const int Racers = 20;

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("counter")));

    /// <summary>
    /// 20 concurrent ADD increments on one item each succeed and their UPDATED_NEW values
    /// are exactly the permutation {1..20}; the item is serialised, never a lost or repeated step.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-add-returns-exact-permutation-of-counter-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-addperm-");
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
        await ddb.PutItemAsync(table, Item(("pk", S("counter")), ("cnt", N("0"))));

        async Task<int> Inc()
        {
            var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "ADD cnt :one",
                ExpressionAttributeValues = Item((":one", N("1"))),
                ReturnValues = ReturnValue.UPDATED_NEW,
            });
            return int.Parse(resp.Attributes["cnt"].N, CultureInfo.InvariantCulture);
        }

        List<int> values;
        using (ctx.Concurrently())
        {
            values = [.. (await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(Inc)))).Order()];
        }

        Ensure(
            values.SequenceEqual(Enumerable.Range(1, Racers)),
            $"the {Racers} UPDATED_NEW values returned were {Py.Repr(values)}, expected exactly the "
            + $"permutation 1..{Racers} with no value lost or repeated");

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final["cnt"], N(Racers.ToString(CultureInfo.InvariantCulture))),
            $"final consistent read gave cnt={Py.Repr(final["cnt"])}, expected {{'N': '{Racers}'}}");
    }
}
