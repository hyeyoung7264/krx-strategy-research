using System.Text.Json;

namespace Investment.Core;

internal static class DartSecrets
{
    public static bool Reflected(string json, string key)
    {
        if (json.Contains(key, StringComparison.Ordinal)) return true;
        using var document = JsonDocument.Parse(json);
        bool Contains(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!.Contains(key, StringComparison.Ordinal),
            JsonValueKind.Object => element.EnumerateObject().Any(p => p.Name.Contains(key, StringComparison.Ordinal) || Contains(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(Contains),
            _ => false
        };
        return Contains(document.RootElement);
    }
}
