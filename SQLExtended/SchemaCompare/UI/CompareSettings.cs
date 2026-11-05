using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SQLExtended.SchemaCompare.Engine;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>
/// Options and recent paths, in <c>%APPDATA%\SQLExtended\SchemaCompare\settings.json</c>, shared by SSMS and the
/// standalone shell. The shell's own entries (its theme, its saved server sign-ins) live in the same file: a host that
/// doesn't know them keeps them in <see cref="Extra"/> and writes them back, so a save from SSMS can't erase them.
/// </summary>
public class CompareSettings
{
    private const int MaxHistory = 12;

    public static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLExtended", "SchemaCompare", "settings.json");

    public CompareOptions Options { get; set; } = new();
    public List<string> LeftHistory { get; set; } = new();
    public List<string> RightHistory { get; set; } = new();
    public bool ShowIdentical { get; set; }
    public bool ShowOriginal { get; set; }
    public bool OptionsPanelOpen { get; set; } = true;

    // The change script window's choices, and where the last script was saved.
    public bool ScriptLeftToRight { get; set; } = true;
    public bool ScriptIncludeDrops { get; set; }
    public bool ScriptUseTransaction { get; set; } = true;
    public string ScriptFolder { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();

    private static readonly JsonSerializerSettings Json = new() { ObjectCreationHandling = ObjectCreationHandling.Replace, Formatting = Newtonsoft.Json.Formatting.Indented };

    public static T Load<T>() where T : CompareSettings, new()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<T>(File.ReadAllText(FilePath), Json) ?? new T();
        }
        catch
        {
            // A damaged settings file costs the user their options, not the compare.
        }

        return new T();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, GetType(), Json));
        }
        catch
        {
            // Not being able to remember options is no reason to interrupt a compare.
        }
    }

    public static void Remember(List<string> history, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        history.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, path);
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
    }
}
