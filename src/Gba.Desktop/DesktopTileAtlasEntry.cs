namespace Gba.Desktop;

internal sealed record DesktopTileAtlasEntry(
    string MapId,
    string Label,
    int Dx,
    int Dy,
    int? X,
    int? Y,
    int Width,
    int Height,
    string Type,
    string Notes,
    int? StandX,
    int? StandY,
    string Action)
{
    public static IReadOnlyList<DesktopTileAtlasEntry> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var resolved = Path.GetFullPath(path, Environment.CurrentDirectory);
        if (!File.Exists(resolved))
        {
            return [];
        }

        var lines = File.ReadLines(resolved)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        var headers = ParseCsvLine(lines[0]);
        var columns = headers
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name.Trim(), item => item.index, StringComparer.OrdinalIgnoreCase);
        if (!columns.ContainsKey("dx") || !columns.ContainsKey("dy"))
        {
            return [];
        }

        var entries = new List<DesktopTileAtlasEntry>();
        foreach (var line in lines.Skip(1))
        {
            var values = ParseCsvLine(line);
            if (!TryGetInt(values, columns, "dx", out var dx) || !TryGetInt(values, columns, "dy", out var dy))
            {
                continue;
            }

            var width = TryGetInt(values, columns, "width", out var parsedWidth) ? Math.Max(1, parsedWidth) : 1;
            var height = TryGetInt(values, columns, "height", out var parsedHeight) ? Math.Max(1, parsedHeight) : 1;
            entries.Add(new DesktopTileAtlasEntry(
                GetValue(values, columns, "mapId"),
                GetValue(values, columns, "label"),
                dx,
                dy,
                TryGetInt(values, columns, "x", out var x) ? x : null,
                TryGetInt(values, columns, "y", out var y) ? y : null,
                width,
                height,
                GetValue(values, columns, "type"),
                GetValue(values, columns, "notes"),
                TryGetInt(values, columns, "standX", out var standX) ? standX : null,
                TryGetInt(values, columns, "standY", out var standY) ? standY : null,
                GetValue(values, columns, "action")));
        }

        return entries;
    }

    private static string GetValue(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> columns, string name)
        => columns.TryGetValue(name, out var index) && index >= 0 && index < values.Count ? values[index] : string.Empty;

    private static bool TryGetInt(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> columns, string name, out int result)
        => int.TryParse(GetValue(values, columns, name), out result);

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        values.Add(current.ToString());
        return values;
    }
}
