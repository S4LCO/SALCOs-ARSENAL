using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services;

namespace SalcosArsenal;

/// <summary>
/// Expands ammo whitelist filters automatically by caliber.
/// If a filter already contains at least one ammo tpl, we infer its caliber(s)
/// and add all ammo tpls of those calibers into the same filter list.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 25)]
public sealed class AutoAmmoCompatPatcher(
    DatabaseService databaseService,
    ILogger<AutoAmmoCompatPatcher> logger
) : IOnLoad
{
    public Task OnLoad()
    {
        try
        {
            var items = databaseService.GetTables().Templates.Items;
            if (items == null || items.Count == 0)
                return Task.CompletedTask;

            // Build: ammoTpl -> caliber, caliber -> all ammoTpls
            var ammoTplToCaliber = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var caliberToAmmoTpls = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in items)
            {
                var tpl = kvp.Key;
                var item = kvp.Value;
                if (item == null)
                    continue;

                var props = Get(item, "_props") ?? Get(item, "Properties");
                if (props == null)
                    continue;

                // Ammo typically has _props.Caliber + Damage/PenetrationPower.
                var caliber = (Get(props, "Caliber") ?? Get(props, "caliber"))?.ToString();
                if (string.IsNullOrWhiteSpace(caliber))
                    continue;

                var hasDamage = Get(props, "Damage") != null || Get(props, "damage") != null;
                var hasPen = Get(props, "PenetrationPower") != null || Get(props, "penetrationPower") != null;
                if (!hasDamage && !hasPen)
                    continue;

                ammoTplToCaliber[tpl] = caliber;

                if (!caliberToAmmoTpls.TryGetValue(caliber, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    caliberToAmmoTpls[caliber] = set;
                }

                set.Add(tpl);
            }

            if (ammoTplToCaliber.Count == 0)
            {
                logger.LogWarning("[AutoAmmoCompat] No ammo templates with Caliber found - nothing to patch.");
                return Task.CompletedTask;
            }

            var filtersTouched = 0;
            var addedTotal = 0;

            foreach (var kvp in items)
            {
                var item = kvp.Value;
                if (item == null)
                    continue;

                var props = Get(item, "_props") ?? Get(item, "Properties");
                if (props == null)
                    continue;

                // 1) Weapons: Chambers
                addedTotal += PatchCollection(props, "Chambers", ammoTplToCaliber, caliberToAmmoTpls, ref filtersTouched);

                // 2) Magazines: Cartridges
                addedTotal += PatchCollection(props, "Cartridges", ammoTplToCaliber, caliberToAmmoTpls, ref filtersTouched);

                // 3) Edge cases: general Slots
                addedTotal += PatchSlots(props, ammoTplToCaliber, caliberToAmmoTpls, ref filtersTouched);
            }

            logger.LogInformation(
                "[AutoAmmoCompat] Done. Filters touched: {FiltersTouched}, Ammo tpl added: {Added}, Known ammo templates: {AmmoCount}",
                filtersTouched, addedTotal, ammoTplToCaliber.Count
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AutoAmmoCompat] Failed to apply auto ammo compatibility.");
        }

        return Task.CompletedTask;
    }

    private static int PatchCollection(
        object ownerProps,
        string collectionName,
        Dictionary<string, string> ammoTplToCaliber,
        Dictionary<string, HashSet<string>> caliberToAmmoTpls,
        ref int filtersTouched
    )
    {
        var collectionObj = Get(ownerProps, collectionName) ?? Get(ownerProps, collectionName.ToLowerInvariant());
        if (collectionObj is not IEnumerable enumerable || collectionObj is string)
            return 0;

        var added = 0;

        foreach (var entry in enumerable.Cast<object>())
        {
            if (entry == null)
                continue;

            var entryProps = Get(entry, "_props") ?? Get(entry, "Properties");
            if (entryProps == null)
                continue;

            added += ExpandFilters(entryProps, ammoTplToCaliber, caliberToAmmoTpls, ref filtersTouched);
        }

        return added;
    }

    private static int PatchSlots(
        object ownerProps,
        Dictionary<string, string> ammoTplToCaliber,
        Dictionary<string, HashSet<string>> caliberToAmmoTpls,
        ref int filtersTouched
    )
    {
        var slotsObj = Get(ownerProps, "Slots") ?? Get(ownerProps, "slots");
        if (slotsObj is not IEnumerable slotsEnum || slotsObj is string)
            return 0;

        var added = 0;

        foreach (var slot in slotsEnum.Cast<object>())
        {
            if (slot == null)
                continue;

            var slotProps = Get(slot, "_props") ?? Get(slot, "Properties");
            if (slotProps == null)
                continue;

            added += ExpandFilters(slotProps, ammoTplToCaliber, caliberToAmmoTpls, ref filtersTouched);
        }

        return added;
    }

    /// <summary>
    /// Finds props.filters (or Filters) and expands each entry's Filter list based on implied ammo calibers.
    /// </summary>
    private static int ExpandFilters(
        object propsWithFilters,
        Dictionary<string, string> ammoTplToCaliber,
        Dictionary<string, HashSet<string>> caliberToAmmoTpls,
        ref int filtersTouched
    )
    {
        var filtersObj = Get(propsWithFilters, "filters") ?? Get(propsWithFilters, "Filters");
        if (filtersObj is not IEnumerable filtersEnum || filtersObj is string)
            return 0;

        var addedTotal = 0;

        foreach (var filterEntry in filtersEnum.Cast<object>())
        {
            if (filterEntry == null)
                continue;

            var filterCollection = Get(filterEntry, "Filter") ?? Get(filterEntry, "filter");
            if (filterCollection == null)
                continue;

            // Works for List<string>, List<MongoId>, HashSet<...>, etc.
            var existingTpls = ReadTplSet(filterCollection);
            if (existingTpls.Count == 0)
                continue;

            // Infer calibers from existing ammo entries
            var impliedCalibers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tpl in existingTpls)
            {
                if (ammoTplToCaliber.TryGetValue(tpl, out var cal) && !string.IsNullOrWhiteSpace(cal))
                    impliedCalibers.Add(cal);
            }

            if (impliedCalibers.Count == 0)
                continue; // not an ammo whitelist

            // Union all ammo tpls of those calibers
            var toAdd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cal in impliedCalibers)
            {
                if (caliberToAmmoTpls.TryGetValue(cal, out var allAmmo))
                {
                    foreach (var tpl in allAmmo)
                        toAdd.Add(tpl);
                }
            }

            var addedHere = 0;
            foreach (var tpl in toAdd)
            {
                if (existingTpls.Contains(tpl))
                    continue;

                AddTpl(filterCollection, tpl);
                addedHere++;
            }

            if (addedHere > 0)
            {
                addedTotal += addedHere;
                filtersTouched++;
            }
        }

        return addedTotal;
    }

    // -------- Collection helpers (MongoId, string, etc.) --------

    private static HashSet<string> ReadTplSet(object collection)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (collection is IEnumerable enumerable && collection is not string)
        {
            foreach (var v in enumerable)
            {
                if (v == null) continue;
                var s = v.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    set.Add(s);
            }
        }

        return set;
    }

    private static void AddTpl(object collection, string tpl)
    {
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
            .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
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

    // -------- Reflection helper (same style as your compat patchers) --------

    private static object? Get(object obj, string name)
    {
        var t = obj.GetType();

        return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                   ?.GetValue(obj)
               ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                   ?.GetValue(obj);
    }
}
