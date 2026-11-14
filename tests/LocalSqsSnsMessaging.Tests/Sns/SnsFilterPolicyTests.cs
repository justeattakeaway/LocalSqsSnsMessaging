using Shouldly;
using MessageAttributeValue = LocalSqsSnsMessaging.Sns.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.Sns;

public class SnsFilterPolicyTests
{
    private static SnsSubscription Subscription(string policy, string scope = SnsFilterPolicy.MessageAttributesScope) => new()
    {
        SubscriptionArn = "sub",
        TopicArn = "arn:aws:sns:us-east-1:000000000000:topic",
        EndPoint = "arn:aws:sqs:us-east-1:000000000000:queue",
        Protocol = "sqs",
        Raw = true,
        FilterPolicyScope = scope,
        FilterPolicy = policy
    };

    private static Dictionary<string, MessageAttributeValue> Attributes(params (string Name, string Type, string Value)[] attributes) =>
        attributes.ToDictionary(a => a.Name, a => new MessageAttributeValue { DataType = a.Type, StringValue = a.Value });

    [Test]
    [Arguments("""{"colour": ["red", "blue"]}""", "colour", "String", "red", true)]
    [Arguments("""{"colour": ["red", "blue"]}""", "colour", "String", "green", false)]
    [Arguments("""{"colour": [{"anything-but": ["red"]}]}""", "colour", "String", "green", true)]
    [Arguments("""{"colour": [{"anything-but": ["red"]}]}""", "colour", "String", "red", false)]
    [Arguments("""{"colour": [{"prefix": "bl"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"colour": [{"suffix": "ue"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"colour": [{"equals-ignore-case": "BLUE"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"colour": [{"wildcard": "b*e"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"colour": [{"wildcard": "b*e"}]}""", "colour", "String", "red", false)]
    [Arguments("""{"colour": [{"anything-but": {"wildcard": "*ue"}}]}""", "colour", "String", "blue", false)]
    [Arguments("""{"source_ip": [{"cidr": "10.0.0.0/24"}]}""", "source_ip", "String", "10.0.0.42", true)]
    [Arguments("""{"source_ip": [{"cidr": "10.0.0.0/24"}]}""", "source_ip", "String", "10.1.0.42", false)]
    [Arguments("""{"price": [{"numeric": [">=", 100, "<", 200]}]}""", "price", "Number", "150", true)]
    [Arguments("""{"price": [{"numeric": [">=", 100, "<", 200]}]}""", "price", "Number", "200", false)]
    [Arguments("""{"price": [100]}""", "price", "Number", "100", true)]
    [Arguments("""{"price": [100]}""", "price", "String", "100", false)]
    [Arguments("""{"colour": [{"exists": true}]}""", "colour", "String", "red", true)]
    [Arguments("""{"colour": [{"exists": false}]}""", "size", "String", "large", true)]
    [Arguments("""{"colour": [{"exists": true}]}""", "size", "String", "large", false)]
    [Arguments("""{"tags": ["sale"]}""", "tags", "String.Array", """["new", "sale"]""", true)]
    [Arguments("""{"tags": ["sale"]}""", "tags", "String.Array", """["new"]""", false)]
    [Arguments("""{"tags": ["sale"]}""", "tags", "Binary", "c2FsZQ==", false)]
    public void AttributeScope_MatchesPerPolicy(string policy, string attributeName, string type, string value, bool expected)
    {
        var subscription = Subscription(policy);

        SnsFilterPolicy.Matches(subscription, "body", Attributes((attributeName, type, value))).ShouldBe(expected);
    }

    [Test]
    public void AttributeScope_OrBranches_MatchWhenAnyBranchMatches()
    {
        var subscription = Subscription("""{"$or": [{"colour": ["red"]}, {"size": ["large"]}]}""");

        SnsFilterPolicy.Matches(subscription, "body", Attributes(("size", "String", "large"))).ShouldBeTrue();
        SnsFilterPolicy.Matches(subscription, "body", Attributes(("size", "String", "small"))).ShouldBeFalse();
    }

    [Test]
    public void EmptyPolicy_MatchesEverything()
    {
        SnsFilterPolicy.Matches(Subscription(""), "body", null).ShouldBeTrue();
        SnsFilterPolicy.Matches(Subscription("{}"), "body", null).ShouldBeTrue();
    }

    [Test]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """{"order": {"status": "shipped"}}""", true)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """{"order": {"status": "placed"}}""", false)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """{"order": "shipped"}""", false)]
    [Arguments("""{"items": {"sku": ["abc"]}}""", """{"items": [{"sku": "xyz"}, {"sku": "abc"}]}""", true)]
    [Arguments("""{"total": [{"numeric": [">", 10]}]}""", """{"total": 12.5}""", true)]
    [Arguments("""{"active": [true]}""", """{"active": true}""", true)]
    [Arguments("""{"active": [true]}""", """{"active": "true"}""", false)]
    [Arguments("""{"ref": [null]}""", """{"ref": null}""", true)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", "not json", false)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """["shipped"]""", false)]
    public void BodyScope_MatchesAgainstJsonBody(string policy, string body, bool expected)
    {
        var subscription = Subscription(policy, SnsFilterPolicy.MessageBodyScope);

        SnsFilterPolicy.Matches(subscription, body, Attributes(("order", "String", "shipped"))).ShouldBe(expected);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""["array"]""")]
    [Arguments("""{"colour": "red"}""")]
    [Arguments("""{"colour": []}""")]
    [Arguments("""{"colour": [["red"]]}""")]
    [Arguments("""{"colour": [{"prefix": "a", "suffix": "b"}]}""")]
    [Arguments("""{"colour": [{"regex": "a.*"}]}""")]
    [Arguments("""{"$or": {"colour": ["red"]}}""")]
    // Malformed operands: the operator is known but its argument isn't the right shape.
    [Arguments("""{"price": [{"numeric": [">", 100, "<"]}]}""")]
    [Arguments("""{"price": [{"numeric": ["~", 100]}]}""")]
    [Arguments("""{"price": [{"numeric": [">", "100"]}]}""")]
    [Arguments("""{"price": [{"numeric": ["<", 100, ">", 200]}]}""")]
    [Arguments("""{"price": [{"numeric": 100}]}""")]
    [Arguments("""{"colour": [{"exists": "yes"}]}""")]
    [Arguments("""{"colour": [{"prefix": 5}]}""")]
    [Arguments("""{"colour": [{"suffix": ["a", "b"]}]}""")]
    [Arguments("""{"colour": [{"equals-ignore-case": true}]}""")]
    [Arguments("""{"colour": [{"wildcard": ["a*"]}]}""")]
    [Arguments("""{"colour": [{"anything-but": []}]}""")]
    [Arguments("""{"colour": [{"anything-but": {"regex": "x"}}]}""")]
    [Arguments("""{"colour": [{"anything-but": {"prefix": 1}}]}""")]
    [Arguments("""{"colour": [{"anything-but": [["red"]]}]}""")]
    [Arguments("""{"colour": [{"anything-but": ["red", 2]}]}""")]
    [Arguments("""{"colour": [{"anything-but": {"cidr": "10.0.0.0/8"}}]}""")]
    [Arguments("""{"price": [{"numeric": ["!=", 5]}]}""")]
    [Arguments("""{"price": [{"numeric": [">", 10, "<", 5]}]}""")]
    [Arguments("""{"source_ip": [{"cidr": "10.0.0.0"}]}""")]
    [Arguments("""{"$or": [{"colour": ["red"]}]}""")]
    [Arguments("""{"colour": {}}""")]
    public void Validate_RejectsMalformedPolicies(string policy)
    {
        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(policy, SnsFilterPolicy.MessageAttributesScope));
    }

    [Test]
    [Arguments("""{"colour": ["red", 1, true, null]}""")]
    [Arguments("""{"colour": [{"prefix": "a"}, {"suffix": "b"}, {"equals-ignore-case": "c"}, {"wildcard": "a*b"}]}""")]
    [Arguments("""{"colour": [{"prefix": {"equals-ignore-case": "a"}}]}""")]
    [Arguments("""{"source_ip": [{"cidr": "10.0.0.0/8"}]}""")]
    [Arguments("""{"price": [{"numeric": ["=", 100]}, {"numeric": [">=", 1, "<", 10]}]}""")]
    [Arguments("""{"colour": [{"exists": false}]}""")]
    [Arguments("""{"colour": [{"anything-but": "red"}, {"anything-but": 3}, {"anything-but": ["red", "blue"]}, {"anything-but": [1, 2]}]}""")]
    [Arguments("""{"colour": [{"anything-but": {"equals-ignore-case": ["Red"]}}, {"suffix": {"equals-ignore-case": "ED"}}]}""")]
    [Arguments("""{}""")]
    [Arguments("""{"colour": [{"anything-but": {"prefix": "a"}}, {"anything-but": {"suffix": ["b", "c"]}}, {"anything-but": {"wildcard": "*x"}}]}""")]
    [Arguments("""{"$or": [{"colour": ["red"]}, {"size": ["large"]}], "shape": ["round"]}""")]
    public void Validate_AcceptsWellFormedPolicies(string policy)
    {
        Should.NotThrow(() => SnsFilterPolicy.Validate(policy, SnsFilterPolicy.MessageAttributesScope));
    }

    [Test]
    public void Validate_RejectsNestingForAttributeScope_ButAllowsItForBodyScope()
    {
        const string nested = """{"order": {"status": ["shipped"]}}""";

        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(nested, SnsFilterPolicy.MessageAttributesScope));
        Should.NotThrow(() => SnsFilterPolicy.Validate(nested, SnsFilterPolicy.MessageBodyScope));
    }

    [Test]
    public void Validate_RejectsMoreThanFiveLevelsOfNesting()
    {
        const string sixLevels = """{"a": {"b": {"c": {"d": {"e": {"f": ["x"]}}}}}}""";
        const string fiveLevels = """{"a": {"b": {"c": {"d": {"e": ["x"]}}}}}""";

        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(sixLevels, SnsFilterPolicy.MessageBodyScope));
        Should.NotThrow(() => SnsFilterPolicy.Validate(fiveLevels, SnsFilterPolicy.MessageBodyScope));
    }

    [Test]
    [Arguments("this-is-not-json",
        "Invalid parameter: FilterPolicy: failed to parse JSON. Unrecognized token 'this': was expecting (JSON String, Number, Array, Object or token 'null', 'true' or 'false')\n at [Source: REDACTED (`StreamReadFeature.INCLUDE_SOURCE_IN_LOCATION` disabled); line: 1, column: 1]")]
    [Arguments("""{"event": "must-be-a-list"}""",
        "Invalid parameter: FilterPolicy: \"event\" must be an object or an array\n at [Source: (String)\"{\"event\":\"must-be-a-list\"}\"; line: 1, column: 11]")]
    [Arguments("""{"price": [{"greater-than": 5}]}""",
        "Invalid parameter: FilterPolicy: Unrecognized match type greater-than\n at [Source: (String)\"{\"price\":[{\"greater-than\":5}]}\"; line: 1, column: 28]")]
    [Arguments("""{"price": [{"numeric": ["!=", 5]}]}""",
        "Invalid parameter: FilterPolicy: Unrecognized numeric range operator: !=\n at [Source: (String)\"{\"price\":[{\"numeric\":[\"!=\",5]}]}\"; line: 1, column: 29]")]
    [Arguments("""{"ip": [{"anything-but": {"cidr": "10.0.0.0/8"}}]}""",
        "Invalid parameter: FilterPolicy: Unsupported anything-but pattern: cidr\n at [Source: (String)\"{\"ip\":[{\"anything-but\":{\"cidr\":\"10.0.0.0/8\"}}]}\"; line: 1, column: 33]")]
    [Arguments("""{"a": ["1"], "b": ["2"], "c": ["3"], "d": ["4"], "e": ["5"], "f": ["6"]}""",
        "Invalid parameter: FilterPolicy: Filter policy can not have more than 5 keys")]
    public void Validate_ReportsTheReasonAwsGives(string policy, string expectedMessage)
    {
        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(policy, SnsFilterPolicy.MessageAttributesScope))
            .Message.ShouldBe(expectedMessage);
    }

    [Test]
    public void Validate_CountsValueCombinations()
    {
        static string Policy(params int[] sizes) =>
            "{" + string.Join(",", sizes.Select((n, i) => $"\"k{i}\": [{string.Join(",", Enumerable.Range(0, n).Select(j => $"\"v{j}\""))}]")) + "}";

        Should.NotThrow(() => SnsFilterPolicy.Validate(Policy(5, 5, 6), SnsFilterPolicy.MessageAttributesScope));
        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(Policy(5, 5, 7), SnsFilterPolicy.MessageAttributesScope))
            .Message.ShouldBe("Invalid parameter: FilterPolicy: Filter policy is too complex");
    }

    [Test]
    public void Validate_CountsNestedLeafKeysForBodyScope()
    {
        const string fiveLeaves = """{"order": {"customer": {"tier": ["gold"], "region": ["eu"]}, "total": [{"numeric": [">", 0]}]}, "source": ["web"], "kind": ["created"]}""";
        const string sixLeaves = """{"order": {"customer": {"tier": ["gold"], "region": ["eu"], "vip": ["yes"]}, "total": [{"numeric": [">", 0]}]}, "source": ["web"], "kind": ["created"]}""";

        Should.NotThrow(() => SnsFilterPolicy.Validate(fiveLeaves, SnsFilterPolicy.MessageBodyScope));
        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(sixLeaves, SnsFilterPolicy.MessageBodyScope))
            .Message.ShouldBe("Invalid parameter: FilterPolicy: Filter policy can not have more than 5 keys");
        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(fiveLeaves, SnsFilterPolicy.MessageAttributesScope))
            .Message.ShouldBe("Invalid parameter: Filter policy scope MessageAttributes does not support nested filter policy");
    }
}
