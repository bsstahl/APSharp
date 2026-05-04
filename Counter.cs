namespace Fedi;

public class Counter
{
    private readonly Dictionary<string, Dictionary<string, long>> _metrics = new();

    public void Set(string name, string? param = null, long value = 0)
    {
        if (!_metrics.TryGetValue(name, out var dict))
        {
            dict = new Dictionary<string, long>();
            _metrics[name] = dict;
        }
        if (param != null)
        {
            dict[param] = value;
        }
    }

    public long Add(string name, string param, long delta)
    {
        if (!_metrics.TryGetValue(name, out var dict))
        {
            dict = new Dictionary<string, long>();
            _metrics[name] = dict;
        }
        if (!dict.TryGetValue(param, out var current))
        {
            current = 0;
        }
        dict[param] = current + delta;
        return current + delta;
    }

    public long Increment(string name, string param) => Add(name, param, 1);

    public string ToHeader()
    {
        return string.Join(", ",
            _metrics.Select(kvp =>
            {
                var parts = new List<string> { kvp.Key };
                parts.AddRange(kvp.Value.Select(p => $"{p.Key}={p.Value}"));
                return string.Join(";", parts);
            }));
    }

    public long? Get(string name, string param)
    {
        if (_metrics.TryGetValue(name, out var dict) && dict.TryGetValue(param, out var value))
        {
            return value;
        }
        return null;
    }
}
