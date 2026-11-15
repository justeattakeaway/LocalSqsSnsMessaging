// Translated from behavioral/dynamodb/check_projection_list_index_out_of_range_null_element_and_nested_list.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-list-index-out-of-range-null-element-and-nested-list</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionListIndexOutOfRangeNullElementAndNestedList
{
    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0)
    {
        return async () =>
        {
            var clock = FidelityTime.StartNew();
            while (true)
            {
                var error = await FidelityContext.CaptureError(undo);
                if (error is null)
                {
                    return;
                }

                if (notFound.Contains(error.Code))
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
    }

    private static readonly string[] ResourceNotFound = ["ResourceNotFoundException"];

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription?> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var limit = Math.Max(0.0, ctx.TimeLeftSeconds - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < limit)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    private static AttributeValue Str(string v) => S(v);

    private static AttributeValue NullValue => Null();

    /// <summary>
    /// GetItem projection drops an out-of-range list index, keeps a NULL list element in place or
    /// by index, and compacts a list nested under a list element's map to the selected element.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-list-index-out-of-range-null-element-and-nested-list")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-projlist-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ResourceNotFound));
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", Str("doc")));
        var item = Item(
            ("pk", Str("doc")),
            ("mylist", L(Str("zero"), Str("one"))),
            ("listWithNull", L(Str("keep0"), NullValue, Str("keep2"))),
            ("nestedList", L(
                M(("inner", L(Str("i0"), Str("i1"), Str("i2"))), ("other", Str("o"))),
                M(("inner", L(Str("j0")))))));
        await ddb.PutItemAsync(table, item);

        async Task<Dictionary<string, AttributeValue>?> Project(string expr, Dictionary<string, string>? names)
        {
            var request = new GetItemRequest { TableName = table, Key = key, ConsistentRead = true, ProjectionExpression = expr };
            if (names is not null)
            {
                request.ExpressionAttributeNames = names;
            }

            return (await ddb.GetItemAsync(request)).Item;
        }

        var cases = new (string Expr, Dictionary<string, string>? Names, Dictionary<string, AttributeValue> Want)[]
        {
            ("mylist[99]", null, Item()),
            ("pk, mylist[99], mylist[1]", null, Item(("pk", Str("doc")), ("mylist", L(Str("one"))))),
            ("listWithNull", null, Item(("listWithNull", item["listWithNull"]))),
            ("listWithNull[1]", null, Item(("listWithNull", L(NullValue)))),
            ("listWithNull[0], listWithNull[2]", null, Item(("listWithNull", L(Str("keep0"), Str("keep2"))))),
            ("nestedList[0].#i[1]", Names(("#i", "inner")), Item(("nestedList", L(M(("inner", L(Str("i1")))))))),
        };
        foreach (var (expr, names, want) in cases)
        {
            var got = await Project(expr, names);
            Ensure(Py.Equal(got, want), $"ProjectionExpression {Py.Repr(expr)} returned {Py.Repr(got)}, expected {Py.Repr(want)}");
        }

        var whole = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(whole, item), $"the unprojected item read back as {Py.Repr(whole)}, expected {Py.Repr(item)}");
    }
}
