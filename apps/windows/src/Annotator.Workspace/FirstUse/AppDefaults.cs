using System.Globalization;
using System.Text.Json;

namespace Annotator.Workspace.FirstUse;

/// <summary>
/// The app's own settings outside the preference profile: the Mac keeps them in <c>UserDefaults</c>, Windows in a JSON
/// file in the app's data folder (spec 013, platform mapping). The introduction's suppression, the coffee reminder's
/// cadence and usage counts live here under the Mac's keys. Values stay in memory when a save fails;
/// <see cref="SaveFailed"/> says why. Use it on one thread.
/// </summary>
public sealed class AppDefaults
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly string? _path;
    private string? _lastFailure;

    private AppDefaults(string? path)
    {
        _path = path;
    }

    /// <summary>Settings kept only in memory, for tests and development runs that must not write files.</summary>
    public static AppDefaults InMemory() => new(null);

    /// <summary>
    /// Settings in <paramref name="path"/>. A missing file starts empty; an unreadable one starts empty too and says why in
    /// <see cref="LoadFailure"/>, as UserDefaults starts over from a damaged file.
    /// </summary>
    public static AppDefaults InFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var defaults = new AppDefaults(path);
        try
        {
            defaults.Read(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException
                                          or InvalidOperationException)
        {
            defaults._values.Clear();
            defaults.LoadFailure = $"Saved app settings could not be read, so the introduction and reminders start over: {error.Message}";
        }
        return defaults;
    }

    /// <summary>Why the saved settings could not be read, or null.</summary>
    public string? LoadFailure { get; private set; }

    /// <summary>Raised once for each new reason a save failed, with a message for the person.</summary>
    public event Action<string>? SaveFailed;

    public bool Contains(string key) => _values.ContainsKey(key);

    /// <summary>UserDefaults bool(forKey:): false when missing.</summary>
    public bool Bool(string key) => _values.TryGetValue(key, out object? value) && value is bool flag && flag;

    /// <summary>UserDefaults integer(forKey:): 0 when missing.</summary>
    public long Integer(string key) => _values.TryGetValue(key, out object? value) && value is long number ? number : 0;

    /// <summary>UserDefaults object(forKey:) as? Date. Dates are saved as round-trip strings and read when asked for.</summary>
    public DateTimeOffset? Date(string key) => _values.TryGetValue(key, out object? value) ? value switch
    {
        DateTimeOffset date => date,
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) => parsed,
        _ => null,
    } : null;

    /// <summary>UserDefaults string(forKey:): null when missing or not a string.</summary>
    public string? String(string key) => _values.TryGetValue(key, out object? value) && value is string text ? text : null;

    public void Set(string key, bool value) => Store(key, value);

    public void Set(string key, long value) => Store(key, value);

    public void Set(string key, DateTimeOffset value) => Store(key, value.ToUniversalTime());

    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Store(key, value);
    }

    public void Remove(string key)
    {
        if (_values.Remove(key))
        {
            Save();
        }
    }

    private void Store(string key, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_values.TryGetValue(key, out object? old) && old.Equals(value))
        {
            return;
        }
        _values[key] = value;
        Save();
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            string? folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            string temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, Write());
            File.Move(temporary, _path, overwrite: true);
            _lastFailure = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string message = $"App settings could not be saved: {error.Message}";
            if (message != _lastFailure)
            {
                _lastFailure = message;
                SaveFailed?.Invoke(message);
            }
        }
    }

    private void Read(byte[] data)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The settings are not a JSON object.");
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            switch (reader.TokenType)
            {
                case JsonTokenType.True or JsonTokenType.False:
                    _values[key] = reader.GetBoolean();
                    break;
                case JsonTokenType.Number:
                    _values[key] = reader.GetInt64();
                    break;
                case JsonTokenType.String:
                    _values[key] = reader.GetString()!;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    private byte[] Write()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach ((string key, object value) in _values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                switch (value)
                {
                    case bool flag:
                        writer.WriteBoolean(key, flag);
                        break;
                    case long number:
                        writer.WriteNumber(key, number);
                        break;
                    case DateTimeOffset date:
                        writer.WriteString(key, date.ToString("O", CultureInfo.InvariantCulture));
                        break;
                    case string text:
                        writer.WriteString(key, text);
                        break;
                }
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
