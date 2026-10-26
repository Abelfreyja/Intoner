using Intoner.Services.Input;
using Intoner.Services.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Intoner.Services.Configuration;

internal static class IntonerConfigurationReader
{
    public static JsonObject Parse(string json, JsonSerializerOptions options)
    {
        JsonObject root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
            JsonDocumentUtility.DocumentOptions) as JsonObject
            ?? throw new JsonException("configuration must be an object");

        try
        {
            options.MakeReadOnly(populateMissingResolver: true);
            root = ReadObject(root, options.GetTypeInfo(typeof(IntonerConfiguration)));
        }
        catch (ArgumentException ex)
        {
            throw new JsonException("configuration contains duplicate property names", ex);
        }

        return root;
    }

    private static JsonObject ReadObject(JsonObject source, JsonTypeInfo contract)
    {
        JsonObject result = new(new JsonNodeOptions { PropertyNameCaseInsensitive = false });
        foreach (KeyValuePair<string, JsonNode?> entry in source)
        {
            JsonPropertyInfo? property = contract.Properties.FirstOrDefault(property =>
                string.Equals(property.Name, entry.Key, StringComparison.OrdinalIgnoreCase));
            JsonTypeInfo? valueContract = property is not null ? contract.Options.GetTypeInfo(property.PropertyType) : null;
            result.Add(property?.Name ?? entry.Key, entry.Value is JsonObject section && valueContract?.Kind == JsonTypeInfoKind.Object
                ? ReadObject(section, valueContract)
                : entry.Value?.DeepClone());
        }

        return result;
    }

    public static IntonerConfiguration Read(JsonObject root, JsonSerializerOptions options, out bool repaired)
    {
        JsonObject defaults = JsonSerializer.SerializeToNode(IntonerConfiguration.CreateDefault(), options)!.AsObject();
        repaired = RepairObject(root, defaults, options.GetTypeInfo(typeof(IntonerConfiguration)));
        IntonerConfiguration configuration = root.Deserialize<IntonerConfiguration>(options)!;
        JsonElement beforeNormalization = JsonSerializer.SerializeToElement(configuration, options);
        IntonerConfigurationNormalizer.Normalize(configuration);
        repaired |= !JsonElement.DeepEquals(beforeNormalization, JsonSerializer.SerializeToElement(configuration, options));
        return configuration;
    }

    public static JsonObject CreateDocument(IntonerConfiguration configuration, JsonObject? source, JsonSerializerOptions options)
    {
        JsonObject known = JsonNode.Parse(JsonSerializer.Serialize(configuration, options))!.AsObject();
        JsonObject result = source?.DeepClone().AsObject() ?? new JsonObject();
        MergeKnownProperties(result, known, options.GetTypeInfo(typeof(IntonerConfiguration)));
        return result;
    }

    private static void MergeKnownProperties(JsonObject target, JsonObject known, JsonTypeInfo contract)
    {
        foreach (JsonPropertyInfo property in contract.Properties)
        {
            JsonNode? value = known[property.Name];
            JsonTypeInfo propertyContract = contract.Options.GetTypeInfo(property.PropertyType);
            if (value is JsonObject section && target[property.Name] is JsonObject original
                && propertyContract.Kind == JsonTypeInfoKind.Object)
            {
                MergeKnownProperties(original, section, propertyContract);
            }
            else
            {
                if (property.PropertyType == typeof(Dictionary<string, KeyboardGesture?>)
                    && value is JsonObject shortcuts && target[property.Name] is JsonObject previous)
                {
                    foreach (string id in shortcuts.Select(static entry => entry.Key).ToArray())
                    {
                        if (shortcuts[id] is JsonObject gesture && previous[id] is JsonObject originalGesture)
                        {
                            JsonObject merged = ReadObject(originalGesture, contract.Options.GetTypeInfo(typeof(KeyboardGesture)));
                            MergeKnownProperties(merged, gesture, contract.Options.GetTypeInfo(typeof(KeyboardGesture)));
                            shortcuts[id] = merged;
                        }
                    }
                }

                target[property.Name] = value?.DeepClone();
            }
        }
    }

    private static bool RepairObject(JsonObject source, JsonObject defaults, JsonTypeInfo contract)
    {
        bool repaired = false;
        foreach (JsonPropertyInfo property in contract.Properties)
        {
            if (!defaults.TryGetPropertyValue(property.Name, out JsonNode? fallback))
            {
                continue;
            }

            if (!source.TryGetPropertyValue(property.Name, out JsonNode? value) || value is null && fallback is not null)
            {
                source[property.Name] = fallback?.DeepClone();
                repaired = true;
                continue;
            }

            JsonTypeInfo propertyContract = contract.Options.GetTypeInfo(property.PropertyType);
            if (propertyContract.Kind == JsonTypeInfoKind.Object && fallback is JsonObject sectionDefaults)
            {
                if (value is JsonObject section)
                {
                    repaired |= RepairObject(section, sectionDefaults, propertyContract);
                }
                else
                {
                    source[property.Name] = sectionDefaults.DeepClone();
                    repaired = true;
                }

                continue;
            }

            if (property.PropertyType == typeof(List<Guid>) && value is JsonArray ids)
            {
                for (int index = ids.Count - 1; index >= 0; --index)
                {
                    if (!CanRead(ids[index], contract.Options.GetTypeInfo(typeof(Guid))))
                    {
                        ids.RemoveAt(index);
                        repaired = true;
                    }
                }

                continue;
            }

            if (property.PropertyType == typeof(Dictionary<string, KeyboardGesture?>) && value is JsonObject overrides)
            {
                JsonTypeInfo gestureContract = contract.Options.GetTypeInfo(typeof(KeyboardGesture?));
                string[] shortcutIds = overrides.Select(static entry => entry.Key).ToArray();
                for (int index = 0; index < shortcutIds.Length; ++index)
                {
                    if (!CanRead(overrides[shortcutIds[index]], gestureContract))
                    {
                        overrides[shortcutIds[index]] = null;
                        repaired = true;
                    }
                }

                continue;
            }

            if (!CanRead(value, propertyContract))
            {
                source[property.Name] = fallback?.DeepClone();
                repaired = true;
            }
        }

        return repaired;
    }

    private static bool CanRead(JsonNode? value, JsonTypeInfo contract)
    {
        try
        {
            return value.Deserialize(contract) switch
            {
                float number => float.IsFinite(number),
                double number => double.IsFinite(number),
                _ => true,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
