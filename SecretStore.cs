using System.Text.Json;

public sealed class SecretStore
{
    private readonly Dictionary<string, string> _secrets;
    private readonly string _filePath;
    private readonly object _sync = new();

    public SecretStore(IHostEnvironment environment)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "secrets.json");
        _secrets = LoadSecrets(_filePath);
    }

    public void Set(string name, string value)
    {
        lock (_sync)
        {
            _secrets[name] = value;
            SaveSecrets();
        }
    }

    public void SetMany(IEnumerable<KeyValuePair<string, string>> secrets)
    {
        lock (_sync)
        {
            foreach (var secret in secrets)
            {
                _secrets[secret.Key] = secret.Value;
            }

            SaveSecrets();
        }
    }

    public bool TryGet(string name, out string? value)
    {
        lock (_sync)
        {
            return _secrets.TryGetValue(name, out value);
        }
    }

    private static Dictionary<string, string> LoadSecrets(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var json = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return secrets is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(secrets, StringComparer.Ordinal);
    }

    private void SaveSecrets()
    {
        var json = JsonSerializer.Serialize(_secrets, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_filePath, json);
    }
}
