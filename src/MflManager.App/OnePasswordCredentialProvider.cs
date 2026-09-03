using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace MflManager.App;

public sealed record ExternalLogin(string Username, string Password);

/// <summary>Reads only the requested fields from the user's local 1Password CLI session.</summary>
public sealed class OnePasswordCredentialProvider
{
    public async Task<ExternalLogin?> GetAsync(string itemName, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = "op", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "item", "get", itemName, "--fields", "username,password", "--format", "json" }) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;
            using var document = JsonDocument.Parse(output);
            var username = Find(document.RootElement, "username");
            var password = Find(document.RootElement, "password");
            return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) ? null : new(username, password);
        }
        catch (Win32Exception) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? Find(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array) return null;
        foreach (var field in fields.EnumerateArray())
        {
            var label = field.TryGetProperty("label", out var labelValue) ? labelValue.GetString() : null;
            if (label?.Equals(name, StringComparison.OrdinalIgnoreCase) == true && field.TryGetProperty("value", out var value)) return value.GetString();
        }
        return null;
    }
}
