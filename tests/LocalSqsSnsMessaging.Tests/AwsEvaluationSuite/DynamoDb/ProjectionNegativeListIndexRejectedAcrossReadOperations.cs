// Translated from behavioral/dynamodb/check_projection_negative_list_index_rejected_across_read_operations.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-negative-list-index-rejected-across-read-operations</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionNegativeListIndexRejectedAcrossReadOperations
{
    private static readonly (string? Code, string? Message) Invalid = ("ValidationException", "Invalid ProjectionExpression: Syntax error; token: \"-\", near: \"[-1\"");
    private static readonly string[] Ops = ["GetItem", "Query", "Scan", "BatchGetItem"];

    /// <summary>The (code, message) <c>fn</c> raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string? Code, string? Message) expected)
    {
        var got = await Error(fn);
        Ensure(got == expected, $"got {Py.Repr(got)}, expected {Py.Repr(expected)}");
    }

    /// <summary>
    /// A ProjectionExpression list index written as a negative integer is refused with the same
    /// syntax-error message on GetItem, Query, Scan and BatchGetItem, and GetItem with a non-negative
    /// index returns that element.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbDetail, "A negative list index in a ProjectionExpression is refused with token \"[\" near \"#l[-1]\", not token \"-\" near \"[-1\"")]
    [DisplayName("dynamodb.projection-negative-list-index-rejected-across-read-operations")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb);
        var key = Item(("pk", S("row")), ("sk", S("only")));

        await ddb.PutItemAsync(table, Item(("pk", S("row")), ("sk", S("only")), ("l", L(S("zero"), S("one")))));

        async Task<object> Call(string op, string expr)
        {
            if (op == "GetItem")
            {
                return await ddb.GetItemAsync(new GetItemRequest
                {
                    TableName = table,
                    Key = key,
                    ConsistentRead = true,
                    ProjectionExpression = expr,
                    ExpressionAttributeNames = Names(("#l", "l")),
                });
            }

            if (op == "Query")
            {
                return await ddb.QueryAsync(new QueryRequest
                {
                    TableName = table,
                    KeyConditionExpression = "#pk = :pk",
                    ExpressionAttributeNames = Names(("#pk", "pk"), ("#l", "l")),
                    ExpressionAttributeValues = Item((":pk", S("row"))),
                    ProjectionExpression = expr,
                    ConsistentRead = true,
                });
            }

            if (op == "Scan")
            {
                return await ddb.ScanAsync(new ScanRequest
                {
                    TableName = table,
                    ProjectionExpression = expr,
                    ExpressionAttributeNames = Names(("#l", "l")),
                    ConsistentRead = true,
                });
            }

            return await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new Dictionary<string, KeysAndAttributes>
                {
                    [table] = new KeysAndAttributes
                    {
                        Keys = [key],
                        ProjectionExpression = expr,
                        ExpressionAttributeNames = Names(("#l", "l")),
                        ConsistentRead = true,
                    },
                },
            });
        }

        // --- every read op refuses a negative list index with the identical syntax-error message ---
        foreach (var op in Ops)
        {
            await ExpectError(() => Call(op, "#l[-1]"), Invalid);
        }

        // --- positive control: GetItem with a non-negative index returns that element ---
        var got = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true,
            ProjectionExpression = "#l[1]",
            ExpressionAttributeNames = Names(("#l", "l")),
        })).Item;
        Ensure(
            Py.Equal(got, Item(("l", L(S("one"))))),
            $"GetItem '#l[1]' must succeed and return the element at index 1; got {Py.Repr(got)}");
    }

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-projnegidx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags =
            [
                new Tag { Key = "sim2real:managed", Value = "true" },
                new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await WaitTableActive(ddb, name);
        return name;
    }

    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string name, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"table {name} never became ACTIVE");
    }
}
