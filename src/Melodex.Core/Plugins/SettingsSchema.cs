using System.Text.Json;
using System.Text.Json.Nodes;

namespace Melodex.Core.Plugins;

/// <summary>
/// The parts of JSON Schema plugin settings use: a flat object of strings, numbers, integers and booleans, with
/// defaults, enums, ranges and required fields. Secrets are marked <c>writeOnly</c>, never returned, and kept when an
/// update leaves them out.
/// </summary>
public static class SettingsSchema
{
    /// <summary>The settings a new instance starts with: every property's default.</summary>
    public static JsonObject Defaults(JsonElement? schema)
    {
        var result = new JsonObject();
        foreach (var (name, property) in Properties(schema))
            if (property.TryGetProperty("default", out var value))
                result[name] = JsonNode.Parse(value.GetRawText());
        return result;
    }

    /// <summary>What's wrong with these settings, by property; empty when they're fine.</summary>
    public static Dictionary<string, string[]> Validate(JsonElement? schema, JsonObject settings)
    {
        var errors = new Dictionary<string, string[]>();
        if (schema is not { ValueKind: JsonValueKind.Object } s)
            return errors;
        if (s.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                if (settings[name] is null || settings[name] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length == 0)
                    errors[name] = ["Required."];

        foreach (var (name, property) in Properties(schema))
        {
            if (settings[name] is not { } node || errors.ContainsKey(name))
                continue;
            var problem = Check(property, node);
            if (problem is not null)
                errors[name] = [problem];
        }
        return errors;
    }

    /// <summary>Settings to show: secrets removed.</summary>
    public static JsonObject Redact(JsonElement? schema, JsonObject settings)
    {
        var copy = (JsonObject)settings.DeepClone();
        foreach (var (name, property) in Properties(schema))
            if (IsSecret(property))
                copy.Remove(name);
        return copy;
    }

    /// <summary>An update applied over the current settings: secrets it leaves out or blanks keep their value.</summary>
    public static JsonObject Merge(JsonElement? schema, JsonObject current, JsonObject update)
    {
        var merged = (JsonObject)update.DeepClone();
        foreach (var (name, property) in Properties(schema))
            if (IsSecret(property) && (merged[name] is null || merged[name] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length == 0)
                && current[name] is { } kept)
                merged[name] = kept.DeepClone();
        return merged;
    }

    private static bool IsSecret(JsonElement property) =>
        property.TryGetProperty("writeOnly", out var w) && w.ValueKind == JsonValueKind.True;

    private static string? Check(JsonElement property, JsonNode node)
    {
        var type = property.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (node is not JsonValue value)
            return "Must be a single value.";
        switch (type)
        {
            case "string" when !value.TryGetValue<string>(out _):
                return "Must be text.";
            case "boolean" when !value.TryGetValue<bool>(out _):
                return "Must be true or false.";
            case "integer" when !(value.TryGetValue<long>(out _) || value.TryGetValue<double>(out var d) && d == Math.Floor(d)):
                return "Must be a whole number.";
            case "number" when !value.TryGetValue<double>(out _):
                return "Must be a number.";
        }
        if (property.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array
            && !options.EnumerateArray().Any(o => JsonNode.DeepEquals(JsonNode.Parse(o.GetRawText()), node)))
            return $"Must be one of {string.Join(", ", options.EnumerateArray().Select(o => o.ToString()))}.";
        if (value.TryGetValue<string>(out var text))
        {
            if (property.TryGetProperty("maxLength", out var most) && text.Length > most.GetInt32())
                return $"Must be at most {most} characters.";
            if (property.TryGetProperty("pattern", out var pattern) && !System.Text.RegularExpressions.Regex.IsMatch(text, pattern.GetString()!))
                return property.TryGetProperty("patternMessage", out var message) ? message.GetString() : "Isn't in the right form.";
        }
        if (value.TryGetValue<double>(out var number))
        {
            if (property.TryGetProperty("minimum", out var min) && number < min.GetDouble())
                return $"Must be at least {min}.";
            if (property.TryGetProperty("maximum", out var max) && number > max.GetDouble())
                return $"Must be at most {max}.";
        }
        return null;
    }

    private static IEnumerable<(string Name, JsonElement Property)> Properties(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => (p.Name, p.Value))
            : [];
}
