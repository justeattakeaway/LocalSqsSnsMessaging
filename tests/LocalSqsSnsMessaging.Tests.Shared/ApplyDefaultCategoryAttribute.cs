using TUnit.Core.Interfaces;

namespace LocalSqsSnsMessaging.Tests;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ApplyDefaultCategoryAttribute : Attribute, ITestDiscoveryEventReceiver
{
    public const string TimeBasedCategoryName = "TimeBased";
    private const string ImmediateCategoryName = "Immediate";

    public ValueTask OnTestDiscovered(DiscoveredTestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TestDetails.Categories.Contains(TimeBasedCategoryName, StringComparer.OrdinalIgnoreCase))
        {
            return ValueTask.CompletedTask;
        }
        context.TestDetails.Categories.Add(ImmediateCategoryName);
        return ValueTask.CompletedTask;
    }
}
