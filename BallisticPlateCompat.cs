using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace SalcosArsenal;

internal static class BallisticPlateCompat
{
    private const string ConfigRelativePath = "Config/BallisticPlateCompat.jsonc";

    public static void Apply(DatabaseService databaseService)
    {
        try
        {
            var mappings = LoadMappings();
            if (mappings.Count == 0)
                return;

            var items = databaseService.GetTables().Templates.Items;

            foreach (var item in items.Values)
            {
                var props = Get(item, "_props") ?? Get(item, "Properties");
                if (props == null)
                    continue;

                var slotsObj = Get(props, "Slots") ?? Get(props, "slots");
                if (slotsObj is not IEnumerable slotsEnum || slotsObj is string)
                    continue;

                foreach (var slot in slotsEnum.Cast<object>())
                {
                    PatchSlotIfMatchesMapping(slot, mappings);
                }
            }
        }
        catch
        {
            // IMPORTANT: Never crash server because of optional compatibility patches.
        }
    }

    private static void PatchSlotIfMatchesMapping(object slot, List<PlateMapping> mappings)
    {
        var slotProps = Get(slot, "_props") ?? Get(slot, "Properties");
        if (slotProps == null)
            return;

        var filtersObj = Get(slotProps, "filters") ?? Get(slotProps, "Filters");
        if (filtersObj is not IEnumerable filtersEnum || filtersObj is string)
            return;

        foreach (var filterEntry in filtersEnum.Cast<object>())
        {
            var filterCollection = Get(filterEntry, "Filter") ?? Get(filterEntry, "filter");
            if (filterCollection == null)
                continue;

            foreach (var mapping in mappings)
            {
                if (!ContainsTpl(filterCollection, mapping.SourcePlateTpl))
                    continue;

                foreach (var cloneTpl in mapping.ClonePlateTpls)
                {
                    AddTplIfMissing(filterCollection, cloneTpl);
                }
            }
        }
    }

    private static List<PlateMapping> LoadMappings()
    {
        var result = new List<PlateMapping>();

        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var modRoot = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(modRoot))
            return result;

        var configPath = Path.Combine(modRoot, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(configPath))
            return result;

        var json = File.ReadAllText(configPath);

        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true
        };

        var root = JsonSerializer.Deserialize<BallisticPlateCompatConfig>(json, options);
        if (root?.Mappings == null)
            return result;

        foreach (var m in root.Mappings)
        {
            if (m == null)
                continue;

            if (string.IsNullOrWhiteSpace(m.SourcePlateTpl))
                continue;

            if (m.ClonePlateTpls == null || m.ClonePlateTpls.Count == 0)
                continue;

            var cleaned = m.ClonePlateTpls
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (cleaned.Count == 0)
                continue;

            result.Add(new PlateMapping(m.SourcePlateTpl.Trim(), cleaned));
        }

        return result;
    }

    private static bool ContainsTpl(object collection, string tpl)
    {
        if (collection is IEnumerable enumerable && collection is not string)
        {
            foreach (var v in enumerable)
            {
                if (v == null)
                    continue;

                if (string.Equals(v.ToString(), tpl, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static void AddTplIfMissing(object collection, string tpl)
    {
        if (ContainsTpl(collection, tpl))
            return;

        // IList (List<string>, List<MongoId>, etc.)
        if (collection is IList list)
        {
            var elementType = GetIListElementType(list);
            list.Add(ConvertTplToElement(tpl, elementType));
            return;
        }

        // HashSet<T> / ISet<T> / other collections with Add(T)
        var addMethod = FindAddMethod(collection);
        if (addMethod != null)
        {
            var paramType = addMethod.GetParameters()[0].ParameterType;
            addMethod.Invoke(collection, new[] { ConvertTplToElement(tpl, paramType) });
        }
    }

    private static Type? GetIListElementType(IList list)
    {
        var t = list.GetType();

        if (t.IsArray)
            return t.GetElementType();

        if (t.IsGenericType)
            return t.GetGenericArguments().FirstOrDefault();

        return null;
    }

    private static MethodInfo? FindAddMethod(object collection)
    {
        var t = collection.GetType();
        return t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m =>
                m.Name == "Add" &&
                m.GetParameters().Length == 1);
    }

    private static object ConvertTplToElement(string tpl, Type? targetType)
    {
        if (targetType == null)
            return tpl;

        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
            targetType = underlying;

        if (targetType == typeof(string) || targetType == typeof(object))
            return tpl;

        if (targetType == typeof(MongoId))
            return new MongoId(tpl);

        var ctor = targetType.GetConstructor(new[] { typeof(string) });
        if (ctor != null)
            return ctor.Invoke(new object[] { tpl });

        return tpl;
    }

    private static object? Get(object obj, string name)
    {
        var t = obj.GetType();

        return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?.GetValue(obj)
               ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?.GetValue(obj);
    }

    private sealed record PlateMapping(string SourcePlateTpl, List<string> ClonePlateTpls);

    private sealed class BallisticPlateCompatConfig
    {
        public List<BallisticPlateCompatMapping>? Mappings { get; set; }
    }

    private sealed class BallisticPlateCompatMapping
    {
        public string? SourcePlateTpl { get; set; }
        public List<string>? ClonePlateTpls { get; set; }
    }
}
