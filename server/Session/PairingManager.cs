using System.Security.Cryptography;
using System.Text.Json;

namespace DeskStreamer.Server.Session;

/// <summary>
/// TOFU pairing store (PROTOCOL.md §2.2). Persists clientId -> token in
/// paired_clients.json next to the executable. Thread-safe.
/// </summary>
public sealed class PairingManager
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _tokens = new();

    public PairingManager(string? directory = null)
    {
        directory ??= AppContext.BaseDirectory;
        _path = Path.Combine(directory, "paired_clients.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                          ?? new Dictionary<string, string>();
            }
        }
        catch
        {
            _tokens = new Dictionary<string, string>();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_tokens,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pairing] failed to persist tokens: {ex.Message}");
        }
    }

    /// <summary>Returns true if the (clientId, token) pair matches a stored pairing.</summary>
    public bool Verify(string clientId, string token)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(token))
            return false;

        lock (_gate)
        {
            return _tokens.TryGetValue(clientId, out var stored) &&
                   CryptographicOperations.FixedTimeEquals(
                       System.Text.Encoding.ASCII.GetBytes(stored),
                       System.Text.Encoding.ASCII.GetBytes(token));
        }
    }

    /// <summary>Creates a new 32-byte hex token for the client, persists it, and returns it.</summary>
    public string Pair(string clientId)
    {
        var token = RandomToken();
        lock (_gate)
        {
            _tokens[clientId] = token;
            Save();
        }
        return token;
    }

    /// <summary>Random 6-digit PIN as a zero-padded string.</summary>
    public static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>32 random bytes rendered as 64 lowercase hex chars.</summary>
    private static string RandomToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
