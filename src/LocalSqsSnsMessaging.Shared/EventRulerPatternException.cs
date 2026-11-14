#pragma warning disable CA1032 // Implement standard exception constructors - internal type, only needs the message constructor
#pragma warning disable CA1064 // Exceptions should be public - internal implementation exception

namespace LocalSqsSnsMessaging;

/// <summary>
/// A pattern refused by <see cref="EventRulerJsonReader"/> or <see cref="EventRulerGrammar"/>. Its
/// message is the reason (and location, when the refusal has one) as AWS words it; each service
/// wraps it in its own error, so it never reaches the wire itself.
/// </summary>
internal sealed class EventRulerPatternException : Exception
{
    public EventRulerPatternException(string message, bool isOrRelationship = false) : base(message)
    {
        IsOrRelationship = isOrRelationship;
    }

    /// <summary>
    /// Whether the pattern was refused because a <c>$or</c> is not a valid or-relationship (not an
    /// array of at least two objects).
    /// </summary>
    public bool IsOrRelationship { get; }
}
