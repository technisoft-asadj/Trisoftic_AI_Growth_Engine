using System.Text;

namespace TriSoftic.GrowthEngine.BuildingBlocks.Parsing;

/// <summary>Minimal RFC 4180 reader: quoted fields, escaped quotes, embedded commas/newlines, CRLF or LF, UTF-8 BOM.</summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var f = new StringBuilder(); var q = false;
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; } else q = false; }
                else f.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(f.ToString()); f.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add([.. row]);
                row.Clear();
            }
            else f.Append(c);
        }
        if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); if (row.Count > 1 || row[0].Length > 0) rows.Add([.. row]); }
        return rows;
    }
}
