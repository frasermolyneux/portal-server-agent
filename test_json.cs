using System;
using System.Text.Json;

// Test: What happens when JSON contains null values?
var json = "{\"hostname\":null,\"username\":\"valid\"}";
var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

foreach (var kvp in dict)
{
    Console.WriteLine($"Key: {kvp.Key}");
    Console.WriteLine($"  ValueKind: {kvp.Value.ValueKind}");
    
    if (kvp.Value.ValueKind == JsonValueKind.String)
    {
        Console.WriteLine($"  Value: '{kvp.Value.GetString()}'");
    }
    else if (kvp.Value.ValueKind == JsonValueKind.Null)
    {
        Console.WriteLine($"  Value is null");
    }
}
