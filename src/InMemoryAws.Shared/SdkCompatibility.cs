namespace InMemoryAws;

/// <summary>
/// Helpers the generated serializers use to read the request models' optional values.
/// </summary>
internal static class SdkCompatibility
{
    public static int GetValueOrZero(int? value) => value.GetValueOrDefault();
    public static long GetValueOrZero(long? value) => value.GetValueOrDefault();
    public static bool GetValueOrFalse(bool? value) => value.GetValueOrDefault();
    public static int GetValueOrDefault(int? value, int defaultValue) => value ?? defaultValue;
    public static long GetValueOrDefault(long? value, long defaultValue) => value ?? defaultValue;

    public static bool HasNonDefaultValue(int? value) => value.HasValue;
    public static bool HasNonDefaultValue(long? value) => value.HasValue;
    public static bool HasNonDefaultValue(bool? value) => value.HasValue;

    public static int GetValue(int? value) => value!.Value;
    public static long GetValue(long? value) => value!.Value;
    public static bool GetValue(bool? value) => value!.Value;

    public static double GetValueOrZero(double? value) => value.GetValueOrDefault();
    public static bool HasNonDefaultValue(double? value) => value.HasValue;
    public static double GetValue(double? value) => value!.Value;

    public static bool HasNonDefaultValue(DateTime? value) => value.HasValue;
    public static DateTime GetValue(DateTime? value) => value!.Value;
}
