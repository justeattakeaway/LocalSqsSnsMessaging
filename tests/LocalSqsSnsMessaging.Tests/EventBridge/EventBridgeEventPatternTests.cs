using System.Text.Json.Nodes;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.EventBridge;

/// <summary>
/// Unit tests for the EventBridge content-based event pattern matcher.
/// </summary>
public sealed class EventBridgeEventPatternTests
{
    private static bool Match(string pattern, string @event) =>
        EventBridgeEventPattern.Matches(pattern, JsonNode.Parse(@event));

    [Test]
    public async Task ExactMatch_TopLevelFields()
    {
        Match("""{"source":["my.app"]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"source":["my.app"]}""", """{"source":"other"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MultipleFields_AreAnded()
    {
        var pattern = """{"source":["my.app"],"detail-type":["Order"]}""";
        Match(pattern, """{"source":"my.app","detail-type":"Order"}""").ShouldBeTrue();
        Match(pattern, """{"source":"my.app","detail-type":"Other"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MultipleCandidates_AreOred()
    {
        var pattern = """{"source":["a","b"]}""";
        Match(pattern, """{"source":"b"}""").ShouldBeTrue();
        Match(pattern, """{"source":"c"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NestedDetail_Matches()
    {
        var pattern = """{"detail":{"state":["running"]}}""";
        Match(pattern, """{"detail":{"state":"running"}}""").ShouldBeTrue();
        Match(pattern, """{"detail":{"state":"stopped"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Prefix_And_Suffix()
    {
        Match("""{"source":[{"prefix":"my."}]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"source":[{"prefix":"my."}]}""", """{"source":"your.app"}""").ShouldBeFalse();
        Match("""{"detail":{"file":[{"suffix":".png"}]}}""", """{"detail":{"file":"a.png"}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task AnythingBut()
    {
        Match("""{"source":[{"anything-but":"blocked"}]}""", """{"source":"allowed"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":"blocked"}]}""", """{"source":"blocked"}""").ShouldBeFalse();
        Match("""{"source":[{"anything-but":["a","b"]}]}""", """{"source":"c"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":["a","b"]}]}""", """{"source":"a"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Numeric()
    {
        Match("""{"detail":{"amount":[{"numeric":[">",10,"<",100]}]}}""", """{"detail":{"amount":50}}""").ShouldBeTrue();
        Match("""{"detail":{"amount":[{"numeric":[">",10]}]}}""", """{"detail":{"amount":5}}""").ShouldBeFalse();
        Match("""{"detail":{"amount":[{"numeric":["=",42]}]}}""", """{"detail":{"amount":42}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Exists()
    {
        Match("""{"detail":{"key":[{"exists":true}]}}""", """{"detail":{"key":"v"}}""").ShouldBeTrue();
        Match("""{"detail":{"key":[{"exists":true}]}}""", """{"detail":{"other":"v"}}""").ShouldBeFalse();
        Match("""{"detail":{"key":[{"exists":false}]}}""", """{"detail":{"other":"v"}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Wildcard()
    {
        Match("""{"source":[{"wildcard":"my.*.service"}]}""", """{"source":"my.orders.service"}""").ShouldBeTrue();
        Match("""{"source":[{"wildcard":"my.*.service"}]}""", """{"source":"my.orders.api"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Cidr()
    {
        Match("""{"detail":{"ip":[{"cidr":"10.0.0.0/24"}]}}""", """{"detail":{"ip":"10.0.0.55"}}""").ShouldBeTrue();
        Match("""{"detail":{"ip":[{"cidr":"10.0.0.0/24"}]}}""", """{"detail":{"ip":"10.0.1.55"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task EqualsIgnoreCase()
    {
        Match("""{"source":[{"equals-ignore-case":"MyApp"}]}""", """{"source":"myapp"}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task OrOperator()
    {
        var pattern = """{"$or":[{"source":["a"]},{"detail-type":["X"]}]}""";
        Match(pattern, """{"source":"a","detail-type":"Y"}""").ShouldBeTrue();
        Match(pattern, """{"source":"b","detail-type":"X"}""").ShouldBeTrue();
        Match(pattern, """{"source":"b","detail-type":"Y"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ArrayValuedEventField_MatchesAnyElement()
    {
        Match("""{"resources":["arn:2"]}""", """{"resources":["arn:1","arn:2"]}""").ShouldBeTrue();
        Match("""{"resources":["arn:9"]}""", """{"resources":["arn:1","arn:2"]}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task InvalidPattern_IsDetected()
    {
        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateTestPattern("not json"));
        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateTestPattern("""["array"]"""));
        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateTestPattern(""));
        Should.NotThrow(() => EventBridgeEventPattern.ValidateTestPattern("""{"source":["a"]}"""));
        await Task.CompletedTask;
    }

    [Test]
    public async Task InvalidPattern_ReportsTheReasonAndPositionInTheTextAsSent()
    {
        const string pattern = "{\n  \"source\": \"a\"\n}";
        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateTestPattern(pattern))
            .Message.ShouldBe($"Event pattern is not valid. Reason: \"source\" must be an object or an array\n at [Source: (String)\"{pattern}\"; line: 2, column: 14]");

        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateTestPattern("{not json"))
            .Message.ShouldBe("Event pattern is not valid. Reason: Unexpected character ('n' (code 110)): was expecting double-quote to start field name\n at [Source: (String)\"{not json\"; line: 1, column: 2]");
        await Task.CompletedTask;
    }

    [Test]
    public async Task RulePattern_LongerThanTheLimit_IsRefused()
    {
        var pattern = $$"""{"source":["{{new string('y', EventBridgeEventPattern.MaxRulePatternLength - 15)}}"]}""";
        Should.NotThrow(() => EventBridgeEventPattern.ValidateRulePattern("r", pattern));
        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateRulePattern("r", pattern + " "))
            .Message.ShouldBe("Parameter EventPattern for rule r exceeds limit of 2048.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task OneBranchOr_IsReadAsAFieldNameByPutRuleOnly()
    {
        const string pattern = """{"$or":[{"detail":{"k":["zz"]}}]}""";
        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateRulePattern("r", pattern))
            .Message.ShouldStartWith("Event pattern is not valid. Reason: Unrecognized match type detail");
        Should.Throw<InternalInvalidEventPatternException>(() => EventBridgeEventPattern.ValidateTestPattern(pattern))
            .Message.ShouldStartWith("Event pattern is not valid. Reason: There must have at least 2 Objects in $or relationship.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NestedPattern_MatchesTheFlattenedPath()
    {
        const string pattern = """{"detail":{"o":{"p":["v"]}}}""";
        Match(pattern, """{"detail":{"o":{"p":"v"}}}""").ShouldBeTrue();
        Match(pattern, """{"detail":{"o.p":"v"}}""").ShouldBeTrue();
        Match(pattern, """{"detail":{"o":[{"p":"v"}]}}""").ShouldBeTrue();
        Match(pattern, """{"detail":{"p":"v"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ArrayField_MatchesWhenAnyElementMatches_NestedArraysIncluded()
    {
        Match("""{"detail":{"xs":["b"]}}""", """{"detail":{"xs":[["b"]]}}""").ShouldBeTrue();
        Match("""{"detail":{"xs":["b"]}}""", """{"detail":{"xs":[]}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Exists_TreatsObjectsAndEmptyArraysAsAbsent()
    {
        const string exists = """{"detail":{"k":[{"exists":true}]}}""";
        const string absent = """{"detail":{"k":[{"exists":false}]}}""";
        Match(exists, """{"detail":{"k":{"a":1}}}""").ShouldBeFalse();
        Match(absent, """{"detail":{"k":{}}}""").ShouldBeTrue();
        Match(absent, """{"detail":{"k":[]}}""").ShouldBeTrue();
        Match(exists, """{"detail":{"k":null}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExistsFalse_MatchesWhenAnIntermediateParentIsMissing()
    {
        Match("""{"detail":{"order":{"customer":[{"exists":false}]}}}""", """{"detail":{}}""").ShouldBeTrue();
        Match("""{"detail":{"order":{"customer":[{"exists":true}]}}}""", """{"detail":{}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task AnythingBut_OverAnArray_MatchesWhenAnyElementEscapes()
    {
        const string pattern = """{"detail":{"tags":[{"anything-but":["blocked","banned"]}]}}""";
        Match(pattern, """{"detail":{"tags":["a","blocked"]}}""").ShouldBeTrue();
        Match(pattern, """{"detail":{"tags":["blocked","banned"]}}""").ShouldBeFalse();
        Match(pattern, """{"detail":{"tags":[]}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task KeysWithinOneArrayOfObjects_MustMatchTheSameElement()
    {
        const string pattern = """{"detail":{"people":{"first":["Anna"],"last":["Jones"]}}}""";
        Match(pattern, """{"detail":{"people":[{"first":"Anna","last":"Smith"},{"first":"John","last":"Jones"}]}}""").ShouldBeFalse();
        Match(pattern, """{"detail":{"people":[{"first":"John","last":"Smith"},{"first":"Anna","last":"Jones"}]}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Wildcard_EscapedStar_IsLiteral()
    {
        Match("""{"source":[{"wildcard":"a\\*b"}]}""", """{"source":"a*b"}""").ShouldBeTrue();
        Match("""{"source":[{"wildcard":"a\\*b"}]}""", """{"source":"axb"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Cidr_ShorthandAddress_DoesNotMatch()
    {
        Match("""{"detail":{"ip":[{"cidr":"10.0.0.0/24"}]}}""", """{"detail":{"ip":"10.0.0"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NumericOperators_AllVariants()
    {
        Match("""{"detail":{"n":[{"numeric":["!=",5]}]}}""", """{"detail":{"n":4}}""").ShouldBeTrue();
        Match("""{"detail":{"n":[{"numeric":["!=",5]}]}}""", """{"detail":{"n":5}}""").ShouldBeFalse();
        Match("""{"detail":{"n":[{"numeric":["<=",10]}]}}""", """{"detail":{"n":10}}""").ShouldBeTrue();
        Match("""{"detail":{"n":[{"numeric":[">=",10]}]}}""", """{"detail":{"n":10}}""").ShouldBeTrue();
        Match("""{"detail":{"n":[{"numeric":[">=",10]}]}}""", """{"detail":{"n":9}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task AnythingBut_ObjectForms()
    {
        Match("""{"source":[{"anything-but":{"prefix":"aws."}}]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":{"prefix":"aws."}}]}""", """{"source":"aws.ec2"}""").ShouldBeFalse();
        Match("""{"source":[{"anything-but":{"suffix":".tmp"}}]}""", """{"source":"file.txt"}""").ShouldBeTrue();
        Match("""{"source":[{"anything-but":{"wildcard":"a*z"}}]}""", """{"source":"abz"}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Prefix_EqualsIgnoreCase_Form()
    {
        Match("""{"source":[{"prefix":{"equals-ignore-case":"MY."}}]}""", """{"source":"my.app"}""").ShouldBeTrue();
        Match("""{"detail":{"f":[{"suffix":{"equals-ignore-case":".PNG"}}]}}""", """{"detail":{"f":"a.png"}}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Exists_False_WhenPresent_DoesNotMatch()
    {
        Match("""{"detail":{"key":[{"exists":false}]}}""", """{"detail":{"key":"v"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Wildcard_LeadingAndTrailing()
    {
        Match("""{"source":[{"wildcard":"*.service"}]}""", """{"source":"my.service"}""").ShouldBeTrue();
        Match("""{"source":[{"wildcard":"my.*"}]}""", """{"source":"my.anything"}""").ShouldBeTrue();
        Match("""{"source":[{"wildcard":"*"}]}""", """{"source":"anything"}""").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Cidr_InvalidInputs_ReturnFalse()
    {
        Match("""{"detail":{"ip":[{"cidr":"not-a-cidr"}]}}""", """{"detail":{"ip":"10.0.0.1"}}""").ShouldBeFalse();
        Match("""{"detail":{"ip":[{"cidr":"10.0.0.0/24"}]}}""", """{"detail":{"ip":"not-an-ip"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NumberAndStringAreDistinct()
    {
        // A string candidate must not match a numeric field (and vice versa).
        Match("""{"detail":{"n":["5"]}}""", """{"detail":{"n":5}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MissingField_WithLiteralCandidate_DoesNotMatch()
    {
        Match("""{"detail":{"absent":["x"]}}""", """{"detail":{"present":"y"}}""").ShouldBeFalse();
        await Task.CompletedTask;
    }
    [Test]
    public async Task ArrayConsistency_KeysInUnrelatedArrays_DoNotMultiplyTheSearch()
    {
        // a, b and c each match every element of their own array; x and y then fail because they lie
        // in different elements of items. Trying every (a, b, c) combination first took n^3 steps.
        const int n = 1000;
        var values = $"[{string.Join(",", Enumerable.Range(0, n))}]";
        var pattern = """{"a":[{"exists":true}],"b":[{"exists":true}],"c":[{"exists":true}],"items":{"x":[1],"y":[2]}}""";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Match(pattern, $$"""{"a":{{values}},"b":{{values}},"c":{{values}},"items":[{"x":1},{"y":2}]}""").ShouldBeFalse();
        Match(pattern, $$"""{"a":{{values}},"b":{{values}},"c":{{values}},"items":[{"x":1,"y":2}]}""").ShouldBeTrue();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ArrayConsistency_StillRequiresTheSameElementOfASharedOuterArray()
    {
        // Values in their own inner arrays are interchangeable only within one element of items.
        var pattern = """{"items":{"tags":["t"],"sku":["abc"]}}""";

        Match(pattern, """{"items":[{"tags":["t","u"],"sku":"xyz"},{"tags":["v"],"sku":"abc"}]}""").ShouldBeFalse();
        Match(pattern, """{"items":[{"tags":["v"],"sku":"xyz"},{"tags":["u","t"],"sku":"abc"}]}""").ShouldBeTrue();
        await Task.CompletedTask;
    }
}
