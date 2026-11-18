// Translated from behavioral/dynamodb/check_condition_non_orderable_path_and_size_of_unsupported_type_evaluate_false.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.condition-non-orderable-path-and-size-of-unsupported-type-evaluate-false</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConditionNonOrderablePathAndSizeOfUnsupportedTypeEvaluateFalse
{
    private const string CcfMsg = "The conditional request failed";

    private static Dictionary<string, AttributeValue> Base() => Item(
        ("pk", S("p1")),
        ("n", N("5")),
        ("num", N("9")),
        ("flag", Bool(true)),
        ("s", S("x")),
        ("nu", Null()),
        ("sset", SS("a", "b")));

    // (condition, values) that are well-formed but evaluate to false on _BASE.
    private static (string Condition, Dictionary<string, AttributeValue>? Values)[] FalseCases() =>
    [
        ("flag > :zero", Item((":zero", N("0")))),
        ("nu > :zero", Item((":zero", N("0")))),
        ("nope > :zero", Item((":zero", N("0")))),
        ("flag > n", null),
        ("sset > n", null),
        ("n = :bool", Item((":bool", Bool(true)))),
        ("size(flag) > :zero", Item((":zero", N("0")))),
        ("size(nu) > :zero", Item((":zero", N("0")))),
        ("size(num) > :zero", Item((":zero", N("0")))),
    ];

    // (condition, values) that evaluate to true on _BASE.
    private static (string Condition, Dictionary<string, AttributeValue>? Values)[] TrueCases() =>
    [
        ("n <> :bool", Item((":bool", Bool(true)))),
        ("size(num) <> :zero", Item((":zero", N("0")))),
        ("size(s) > :zero", Item((":zero", N("0")))),
        ("size(sset) > :zero", Item((":zero", N("0")))),
        ("size(sset) = :two", Item((":two", N("2")))),
        ("n > :zero", Item((":zero", N("0")))),
    ];

    /// <summary>
    /// Ordering a path that holds BOOL/NULL/a set or nothing, '=' with a BOOL value, and size() of
    /// an N/BOOL/NULL in a PutItem condition evaluate false (ConditionalCheckFailedException);
    /// '&lt;&gt;' is true.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.condition-non-orderable-path-and-size-of-unsupported-type-evaluate-false")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-cond-eval-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("p1")));
        await ddb.PutItemAsync(table, Base());

        Task<PutItemResponse> Put(Dictionary<string, AttributeValue> item, string condition, Dictionary<string, AttributeValue>? values) =>
            ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = item,
                ConditionExpression = condition,
                ExpressionAttributeValues = values,
            });

        foreach (var (condition, values) in FalseCases())
        {
            var changed = Base();
            changed["mark"] = S(condition);
            var (code, msg) = await Error(() => Put(changed, condition, values));
            Ensure(
                (code, msg) == ("ConditionalCheckFailedException", CcfMsg),
                $"PutItem under {Py.Repr(condition)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            Ensure(Py.Equal(item, Base()), $"PutItem under false condition {Py.Repr(condition)} changed the item to {Py.Repr(item)}");
        }

        var current = Base();
        foreach (var (condition, values) in TrueCases())
        {
            var changed = Base();
            changed["mark"] = S(condition);
            await Put(changed, condition, values);
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(item, changed),
                $"PutItem under true condition {Py.Repr(condition)} left {Py.Repr(item)} (before it: {Py.Repr(current)})");
            current = item;
        }
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        const double timeoutS = 300.0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is not null ? (error.Code, error.Message) : throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
