using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Services;
using Path = System.IO.Path;

namespace SalcosArsenal;

internal static class SalcosCompat
{
    public static void Apply(DatabaseService databaseService, Assembly assembly)
    {
        var modRoot = Path.GetDirectoryName(assembly.Location) ?? "";
        if (string.IsNullOrWhiteSpace(modRoot))
            return;

        var weaponsDir = Path.Combine(modRoot, "Weapons");
        if (!Directory.Exists(weaponsDir))
            return;

        var compatById = LoadCompatFromWeapons(weaponsDir);
        if (compatById.Count == 0)
            return;

        var itemsDict = (IDictionary)databaseService.GetTables().Templates.Items;
        ApplyWeaponCompat(itemsDict, compatById);
    }

    private static Dictionary<string, SalcosCompatConfig> LoadCompatFromWeapons(string weaponsDir)
    {
        var result = new Dictionary<string, SalcosCompatConfig>(StringComparer.Ordinal);

        var files = Directory.GetFiles(weaponsDir, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var wrapper = JsonSerializer.Deserialize<SalcosCompatItemWrapper>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                var cfg = wrapper?.SalcosCompat;
                if (cfg == null)
                    continue;

                var id = cfg.Id;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                result[id] = cfg;
            }
            catch
            {
                // intentionally ignored – compat must never block startup
            }
        }

        return result;
    }

    private static void ApplyWeaponCompat(IDictionary itemsDict, Dictionary<string, SalcosCompatConfig> compatById)
    {
        if (itemsDict.Count == 0 || compatById.Count == 0)
            return;

        foreach (var kvp in compatById)
        {
            if (!itemsDict.Contains(kvp.Key))
                continue;

            var item = itemsDict[kvp.Key];
            if (item == null)
                continue;

            var overrides = kvp.Value.SlotOverrides;
            if (overrides is null || overrides.Count == 0)
                continue;

            ApplySlotOverrides(item, overrides);
        }
    }

    private static void ApplySlotOverrides(object templateItem, List<SalcosSlotOverride> overrides)
    {
        var props = GetMemberValue(templateItem, "_props") ?? GetMemberValue(templateItem, "Properties");
        if (props == null)
            return;

        var slotsObj = GetMemberValue(props, "Slots") ?? GetMemberValue(props, "slots");
        var slots = new List<object>();

        if (slotsObj is IEnumerable slotsEnum && slotsObj is not string)
        {
            foreach (var s in slotsEnum)
                if (s != null) slots.Add(s);
        }

        var chambersObj = GetMemberValue(props, "Chambers") ?? GetMemberValue(props, "chambers");
        if (chambersObj is IEnumerable chambersEnum && chambersObj is not string)
        {
            foreach (var c in chambersEnum)
                if (c != null) slots.Add(c);
        }

        if (slots.Count == 0)
            return;

        foreach (var ov in overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.SlotName))
                continue;

            var slot = FindSlotByName(slots, ov.SlotName);
            if (slot == null)
                continue;

            ApplySlotOverride(slot, ov);
        }
    }

    private static object? FindSlotByName(List<object> slots, string slotName)
    {
        foreach (var slot in slots)
        {
            var name = (GetMemberValue(slot, "_name") ?? GetMemberValue(slot, "Name"))?.ToString();
            if (name == null)
                continue;

            if (string.Equals(name, slotName, StringComparison.OrdinalIgnoreCase))
                return slot;
        }

        return null;
    }

    private static void ApplySlotOverride(object slot, SalcosSlotOverride ov)
    {
        var props = GetMemberValue(slot, "_props") ?? GetMemberValue(slot, "Properties");
        if (props == null)
            return;

        var filtersObj = GetMemberValue(props, "filters") ?? GetMemberValue(props, "Filters");
        if (filtersObj is not IList filters || filters.Count == 0)
            return;

        var firstFilter = filters[0];
        if (firstFilter == null)
            return;

        var filterTplsObj = GetMemberValue(firstFilter, "Filter") ?? GetMemberValue(firstFilter, "filter");
        var filterTpls = EnsureStringList(firstFilter, "Filter", filterTplsObj);
        if (filterTpls == null)
            return;

        if (ov.FilterTpls != null && ov.FilterTpls.Count > 0)
        {
            foreach (var tpl in ov.FilterTpls)
            {
                if (string.IsNullOrWhiteSpace(tpl))
                    continue;

                if (!ContainsString(filterTpls, tpl))
                    filterTpls.Add(tpl);
            }
        }

        if (ov.ClearExcludedFilter)
        {
            var excludedObj = GetMemberValue(firstFilter, "ExcludedFilter") ?? GetMemberValue(firstFilter, "excludedFilter");
            var excludedList = EnsureStringList(firstFilter, "ExcludedFilter", excludedObj);
            excludedList?.Clear();
        }
    }

    private static bool ContainsString(IList list, string value)
    {
        foreach (var x in list)
        {
            if (x == null)
                continue;

            if (string.Equals(x.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        try
        {
            var t = obj.GetType();

            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (prop != null)
                return prop.GetValue(obj);

            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null)
                return field.GetValue(obj);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static IList? EnsureStringList(object owner, string propName, object? current)
    {
        if (current is IList list)
            return list;

        var created = new List<string>();
        TrySetMemberValue(owner, propName, created);
        TrySetMemberValue(owner, propName.ToLowerInvariant(), created);
        return created;
    }

    private static void TrySetMemberValue(object obj, string name, object? value)
    {
        try
        {
            var t = obj.GetType();

            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(obj, value);
                return;
            }

            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null)
                field.SetValue(obj, value);
        }
        catch
        {
            // ignore
        }
    }
}

internal sealed class SalcosCompatItemWrapper
{
    [JsonPropertyName("salcosCompat")]
    public SalcosCompatConfig? SalcosCompat { get; set; }
}

internal sealed class SalcosCompatConfig
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("slotOverrides")]
    public List<SalcosSlotOverride>? SlotOverrides { get; set; }
}

internal sealed class SalcosSlotOverride
{
    [JsonPropertyName("slotName")]
    public string? SlotName { get; set; }

    [JsonPropertyName("filterTpls")]
    public List<string>? FilterTpls { get; set; }

    [JsonPropertyName("clearExcludedFilter")]
    public bool ClearExcludedFilter { get; set; } = true;
}
