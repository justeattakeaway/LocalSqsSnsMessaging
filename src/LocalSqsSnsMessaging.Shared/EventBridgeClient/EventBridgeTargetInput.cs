using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// What a target is sent for an event. EventBridge requires it to be JSON: when an input
/// transformer renders something that is not (a plain-text template splicing in a quote, an object
/// spliced into a quoted string), the target is not sent it and EventBridge dead-letters
/// <see cref="Body"/> with <c>ERROR_CODE</c> <c>INVALID_JSON</c> and <c>ERROR_MESSAGE</c>
/// "Invalid input for target.".
/// </summary>
/// <param name="Body">The rendered input.</param>
/// <param name="IsValidJson">Whether <paramref name="Body"/> is JSON and so may be sent to the target.</param>
internal readonly record struct EventBridgeTargetPayload(string Body, bool IsValidJson);

/// <summary>
/// A target's Input, InputPath or InputTransformer: validated by PutTargets and rendered for each
/// matched event. See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-transform-target-input.html
/// </summary>
/// <remarks>
/// Paths are the JSONPath subset EventBridge accepts: <c>$</c> followed by <c>.member</c>,
/// <c>[index]</c> (not negative) and the <c>[*]</c> / <c>.*</c> wildcards; a bracketed quoted
/// member such as <c>['a.b']</c> is refused. A path with a wildcard selects an array of matches.
/// <para>
/// An InputTemplate is either JSON or, when the whole template is one JSON string, plain text. A
/// placeholder outside a string is replaced by its value as JSON (an unresolved path by <c>""</c>);
/// inside a JSON template's string, by a string's escaped text or any other value's JSON; inside a
/// plain-text template, by its text unescaped, objects and arrays written without quotes
/// (<c>{id:o1,v:1}</c>). An unresolved path is empty text in either kind of string.
/// </para>
/// </remarks>
internal static class EventBridgeTargetInput
{
    private const string RuleNameVariable = "aws.events.rule-name";
    private const string RuleArnVariable = "aws.events.rule-arn";
    private const string EventVariable = "aws.events.event";
    private const string EventJsonVariable = "aws.events.event.json";
    private const string IngestionTimeVariable = "aws.events.event.ingestion-time";

    /// <summary>
    /// Refuses a PutTargets request whole, naming the first target whose input is invalid: more
    /// than one of Input, InputPath and InputTransformer, an Input that is not JSON, an unsupported
    /// path, a template placeholder that is neither declared nor reserved, or a template that is
    /// not JSON once every placeholder reads as <c>null</c>.
    /// </summary>
    public static void Validate(List<Target>? targets)
    {
        foreach (var target in targets ?? [])
        {
            Validate(target);
        }
    }

    /// <summary>What <paramref name="target"/> of <paramref name="rule"/> is sent for <paramref name="event"/>: its Input, InputPath or InputTransformer, else the whole envelope.</summary>
    public static EventBridgeTargetPayload Render(Target target, RuleResource rule, EventBridgeEvent @event)
    {
        if (target.Input is not null)
        {
            return new EventBridgeTargetPayload(target.Input, IsValidJson: true);
        }
        if (target.InputPath is not null)
        {
            // A path that selects nothing sends JSON null.
            var selected = TryParsePath(target.InputPath, out var steps) && TrySelect(@event.Envelope, steps, out var value)
                ? EventBridgeJson.Serialize(value)
                : "null";
            return new EventBridgeTargetPayload(selected, IsValidJson: true);
        }
        if (target.InputTransformer is { } transformer)
        {
            var body = Transform(transformer, rule, @event);
            return new EventBridgeTargetPayload(body, EventBridgeJson.IsValid(body, out _, out _, out _));
        }
        return new EventBridgeTargetPayload(EventBridgeJson.Serialize(@event.Envelope), IsValidJson: true);
    }

    /// <summary>
    /// The FIFO message group for an SQS target's delivery: <c>SqsParameters.MessageGroupId</c>, or,
    /// when that is a JSONPath such as <c>$.detail.group</c>, the value it selects from the event.
    /// <see langword="null"/> when no group is set or the path selects nothing.
    /// </summary>
    public static string? ResolveMessageGroupId(Target target, JsonObject envelope)
    {
        var groupId = target.SqsParameters?.MessageGroupId;
        if (groupId is null || !groupId.StartsWith("$.", StringComparison.Ordinal) || !TryParsePath(groupId, out var steps))
        {
            return groupId;
        }
        if (!TrySelect(envelope, steps, out var value))
        {
            return null;
        }
        return value is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : EventBridgeJson.Serialize(value);
    }

    private static void Validate(Target target)
    {
        var inputs = (target.Input is null ? 0 : 1) + (target.InputPath is null ? 0 : 1) + (target.InputTransformer is null ? 0 : 1);
        if (inputs > 1)
        {
            throw ValidationError($"Only one of Input, InputPath, or InputTransformer must be provided for target {target.Id}.");
        }

        if (target.Input is { } input && !EventBridgeJson.IsValid(input, out _, out var line, out var column))
        {
            throw ValidationError($"JSON syntax error in input for target {target.Id}: [Source: (String)\"{input}\"; line: {line}, column: {column}]");
        }

        if (target.InputPath is { } inputPath && !TryParsePath(inputPath, out _))
        {
            throw InvalidInputPath(target);
        }

        if (target.InputTransformer is { } transformer)
        {
            Validate(target, transformer);
        }
    }

    private static void Validate(Target target, InputTransformer transformer)
    {
        foreach (var path in (transformer.InputPathsMap ?? []).Values)
        {
            if (!TryParsePath(path, out _))
            {
                throw InvalidInputPath(target);
            }
        }

        var template = transformer.InputTemplate ?? string.Empty;
        var placeholders = FindPlaceholders(template);
        foreach (var placeholder in placeholders)
        {
            if (!IsReservedVariable(placeholder.Name) && transformer.InputPathsMap?.ContainsKey(placeholder.Name) != true)
            {
                throw ValidationError($"InputTemplate for target {target.Id} contains invalid placeholder {placeholder.Name}.");
            }
        }

        var parsedAs = WithNullPlaceholders(template, placeholders);
        if (!EventBridgeJson.IsValid(parsedAs, out _, out var line, out var column))
        {
            throw ValidationError($"Invalid InputTemplate for target {target.Id} : [Source: (String)\"{parsedAs}\"; line: {line}, column: {column}].");
        }
    }

    private static AwsServiceException InvalidInputPath(Target target) =>
        ValidationError($"InputPath for target {target.Id} is invalid.");

    private static AwsServiceException ValidationError(string message) =>
        new(message) { ErrorCode = "ValidationException" };

    // ---- templates ----

    /// <summary>A <c>&lt;name&gt;</c> in a template, at [<see cref="Start"/>, <see cref="End"/>), and whether it sits inside a JSON string.</summary>
    private readonly record struct Placeholder(int Start, int End, string Name, bool InString);

    /// <summary>How a placeholder's value is written, by where it sits in the template.</summary>
    private enum Splice
    {
        /// <summary>Outside any string: the value as JSON.</summary>
        Json,

        /// <summary>Inside a string of a JSON template: a string escaped, anything else as JSON.</summary>
        JsonString,

        /// <summary>Inside a plain-text template: the value's text, unescaped and unquoted.</summary>
        Text
    }

    private static string Transform(InputTransformer transformer, RuleResource rule, EventBridgeEvent @event)
    {
        var template = transformer.InputTemplate ?? string.Empty;
        var placeholders = FindPlaceholders(template);
        var isPlainText = EventBridgeJson.IsValid(WithNullPlaceholders(template, placeholders), out var kind, out _, out _)
                          && kind == JsonValueKind.String;

        var output = new StringBuilder(template.Length);
        var position = 0;
        foreach (var placeholder in placeholders)
        {
            output.Append(template, position, placeholder.Start - position);
            var splice = !placeholder.InString ? Splice.Json : isPlainText ? Splice.Text : Splice.JsonString;
            var found = TryResolveVariable(placeholder.Name, transformer, rule, @event, out var value);
            AppendValue(output, found, value, splice);
            position = placeholder.End;
        }
        output.Append(template, position, template.Length - position);
        return output.ToString();
    }

    private static void AppendValue(StringBuilder output, bool found, JsonNode? value, Splice splice)
    {
        if (!found)
        {
            // An unresolved path is the empty string: "" as JSON, nothing inside a string.
            if (splice == Splice.Json)
            {
                output.Append("\"\"");
            }
            return;
        }

        switch (splice)
        {
            case Splice.Json:
                output.Append(EventBridgeJson.Serialize(value));
                break;
            case Splice.JsonString when value is JsonValue s && s.GetValueKind() == JsonValueKind.String:
                EventBridgeJson.AppendEscaped(output, s.GetValue<string>());
                break;
            case Splice.JsonString:
                // Spliced verbatim, quotes and all: an object inside a string renders invalid JSON.
                output.Append(EventBridgeJson.Serialize(value));
                break;
            default:
                AppendText(output, value);
                break;
        }
    }

    /// <summary>A value as plain text: strings unescaped and unquoted, at any depth.</summary>
    private static void AppendText(StringBuilder output, JsonNode? value)
    {
        switch (value)
        {
            case JsonObject obj:
                output.Append('{');
                var first = true;
                foreach (var (name, member) in obj)
                {
                    output.Append(first ? string.Empty : ",").Append(name).Append(':');
                    AppendText(output, member);
                    first = false;
                }
                output.Append('}');
                break;
            case JsonArray array:
                output.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    output.Append(i == 0 ? string.Empty : ",");
                    AppendText(output, array[i]);
                }
                output.Append(']');
                break;
            case JsonValue s when s.GetValueKind() == JsonValueKind.String:
                output.Append(s.GetValue<string>());
                break;
            default:
                output.Append(EventBridgeJson.Serialize(value));
                break;
        }
    }

    private static bool TryResolveVariable(string name, InputTransformer transformer, RuleResource rule, EventBridgeEvent @event, out JsonNode? value)
    {
        if (transformer.InputPathsMap?.TryGetValue(name, out var path) == true)
        {
            value = null;
            return TryParsePath(path, out var steps) && TrySelect(@event.Envelope, steps, out value);
        }

        value = name switch
        {
            RuleNameVariable => JsonValue.Create(rule.Name),
            RuleArnVariable => JsonValue.Create(rule.Arn),
            EventVariable => EnvelopeWithoutDetail(@event.Envelope),
            EventJsonVariable => @event.Envelope,
            IngestionTimeVariable => JsonValue.Create(@event.IngestionTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
            _ => null
        };
        return value is not null;
    }

    private static bool IsReservedVariable(string name) =>
        name is RuleNameVariable or RuleArnVariable or EventVariable or EventJsonVariable or IngestionTimeVariable;

    private static JsonObject EnvelopeWithoutDetail(JsonObject envelope)
    {
        var copy = new JsonObject();
        foreach (var (name, value) in envelope)
        {
            if (!string.Equals(name, "detail", StringComparison.Ordinal))
            {
                copy[name] = value?.DeepClone();
            }
        }
        return copy;
    }

    /// <summary>The template as PutTargets parses it, every placeholder read as <c>null</c>.</summary>
    private static string WithNullPlaceholders(string template, List<Placeholder> placeholders)
    {
        var output = new StringBuilder(template.Length);
        var position = 0;
        foreach (var placeholder in placeholders)
        {
            output.Append(template, position, placeholder.Start - position).Append("null");
            position = placeholder.End;
        }
        return output.Append(template, position, template.Length - position).ToString();
    }

    /// <summary>
    /// Every <c>&lt;name&gt;</c> in a template (names are letters, digits, <c>_</c>, <c>-</c> and,
    /// for the reserved <c>aws.events.*</c> variables, <c>.</c>), noting which sit inside a JSON string.
    /// </summary>
    private static List<Placeholder> FindPlaceholders(string template)
    {
        var placeholders = new List<Placeholder>();
        var inString = false;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '<')
            {
                var end = i + 1;
                while (end < template.Length && IsPlaceholderNameChar(template[end]))
                {
                    end++;
                }
                if (end > i + 1 && end < template.Length && template[end] == '>')
                {
                    placeholders.Add(new Placeholder(i, end + 1, template.Substring(i + 1, end - i - 1), inString));
                    i = end;
                }
            }
            else if (c == '\\' && inString)
            {
                i++;
            }
            else if (c == '"')
            {
                inString = !inString;
            }
        }
        return placeholders;
    }

    private static bool IsPlaceholderNameChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.';

    // ---- paths ----

    private enum PathStepKind
    {
        Member,
        Index,
        Wildcard
    }

    private readonly record struct PathStep(PathStepKind Kind, string Member = "", int Index = 0);

    private static bool TryParsePath(string? path, out List<PathStep> steps)
    {
        steps = [];
        if (string.IsNullOrEmpty(path) || path![0] != '$')
        {
            return false;
        }

        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var start = ++i;
                while (i < path.Length && path[i] is not ('.' or '[' or ']'))
                {
                    i++;
                }
                if (i == start)
                {
                    return false;
                }
                var member = path.Substring(start, i - start);
                steps.Add(member == "*" ? new PathStep(PathStepKind.Wildcard) : new PathStep(PathStepKind.Member, member));
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i);
                if (end < 0)
                {
                    return false;
                }
                var inner = path.Substring(i + 1, end - i - 1);
                if (inner == "*")
                {
                    steps.Add(new PathStep(PathStepKind.Wildcard));
                }
                else if (inner.Length > 0 && inner.All(c => c is >= '0' and <= '9')
                         && int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    steps.Add(new PathStep(PathStepKind.Index, Index: index));
                }
                else
                {
                    return false;
                }
                i = end + 1;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// What a parsed path selects: the one node it names (which may be JSON null), or for a path with
    /// a wildcard an array of every match. False when a path without a wildcard names nothing.
    /// </summary>
    private static bool TrySelect(JsonNode root, List<PathStep> steps, out JsonNode? value)
    {
        List<JsonNode?> current = [root];
        foreach (var step in steps)
        {
            var next = new List<JsonNode?>();
            foreach (var node in current)
            {
                switch (step.Kind)
                {
                    case PathStepKind.Member when node is JsonObject obj && obj.TryGetPropertyValue(step.Member, out var child):
                        next.Add(child);
                        break;
                    case PathStepKind.Index when node is JsonArray array && step.Index < array.Count:
                        next.Add(array[step.Index]);
                        break;
                    case PathStepKind.Wildcard when node is JsonArray array:
                        next.AddRange(array);
                        break;
                    case PathStepKind.Wildcard when node is JsonObject obj:
                        next.AddRange(obj.Select(p => p.Value));
                        break;
                }
            }
            current = next;
        }

        if (steps.Exists(s => s.Kind == PathStepKind.Wildcard))
        {
            value = new JsonArray(current.Select(n => n?.DeepClone()).ToArray());
            return true;
        }
        value = current.Count > 0 ? current[0] : null;
        return current.Count > 0;
    }
}
