using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace SalcosArsenal
{
    internal static class BuiltInPlatePatcher
    {
        private const string RigTpl = "6946eb7e67ba8110a6ddfd5e";

        private const string SoftFrontTpl = "6946ebb44e3088ef0bb4dfbe";
        private const string SoftBackTpl  = "6946ebbacf8f3b61ff54d3eb";
        private const string SoftGroinTpl = "6946ebc505411b23e69d540f";

        private const string DefaultPlateTpl = "656fae5f7c2d57afe200c0d7";

        public static void Apply(DatabaseService databaseService)
        {
            try
            {
                var items = databaseService.GetTables().Templates.Items;

                if (!items.TryGetValue(RigTpl, out var rig))
                    return;

                var rigProps = Get(rig, "Properties") ?? Get(rig, "_props");
                if (rigProps == null)
                    return;

                var slotsObj = Get(rigProps, "Slots") ?? Get(rigProps, "slots");
                if (slotsObj is not IEnumerable slotsEnum)
                    return;

                var slots = slotsEnum.Cast<object>().ToList();

                PatchSoftSlot(slots, "Soft_armor_front", SoftFrontTpl);
                PatchSoftSlot(slots, "Soft_armor_back",  SoftBackTpl);
                PatchSoftSlot(slots, "Groin",            SoftGroinTpl);

                PatchUserPlateSlot(slots, "Front_plate", DefaultPlateTpl);
                PatchUserPlateSlot(slots, "Back_plate",  DefaultPlateTpl);
            }
            catch
            {
                // IMPORTANT: Never crash server because of reflection/runtime template variance.
            }
        }

        private static void PatchSoftSlot(List<object> slots, string slotName, string tpl)
        {
            var slot = FindSlot(slots, slotName);
            if (slot == null)
                return;

            var slotProps = Get(slot, "Properties") ?? Get(slot, "_props");
            if (slotProps == null)
                return;

            var filtersObj = Get(slotProps, "filters") ?? Get(slotProps, "Filters");
            if (filtersObj is not IEnumerable filtersEnum)
                return;

            foreach (var filterEntry in filtersEnum.Cast<object>())
            {
                SetTyped(filterEntry, "Filter", tpl);
                SetTyped(filterEntry, "Plate", tpl);
                SetRaw(filterEntry, "locked", true);
            }

            SetRaw(slot, "_required", true);
        }

        private static void PatchUserPlateSlot(List<object> slots, string slotName, string tpl)
        {
            var slot = FindSlot(slots, slotName);
            if (slot == null)
                return;

            var slotProps = Get(slot, "Properties") ?? Get(slot, "_props");
            if (slotProps == null)
                return;

            var filtersObj = Get(slotProps, "filters") ?? Get(slotProps, "Filters");
            if (filtersObj is not IEnumerable filtersEnum)
                return;

            foreach (var filterEntry in filtersEnum.Cast<object>())
            {
                SetTyped(filterEntry, "Plate", tpl);
                SetRaw(filterEntry, "locked", false);
            }

            SetRaw(slot, "_required", false);
        }

        private static object? FindSlot(List<object> slots, string slotName)
        {
            return slots.FirstOrDefault(s =>
                string.Equals(Get(s, "_name")?.ToString(), slotName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Get(s, "Name")?.ToString(), slotName, StringComparison.OrdinalIgnoreCase));
        }

        private static void SetTyped(object obj, string name, string tpl)
        {
            var member = GetMember(obj, name);
            if (member == null)
                return;

            var targetType = member switch
            {
                PropertyInfo p => p.PropertyType,
                FieldInfo f => f.FieldType,
                _ => null
            };

            if (targetType == null)
                return;

            object converted = ConvertTplToTarget(tpl, targetType);
            SetMember(obj, member, converted);
        }

        private static object ConvertTplToTarget(string tpl, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ConvertTplToTarget(tpl, underlying);

            if (targetType == typeof(string))
                return tpl;

            if (targetType == typeof(MongoId))
                return new MongoId(tpl);

            if (targetType.IsGenericType
                && targetType.GetGenericTypeDefinition() == typeof(HashSet<>)
                && targetType.GetGenericArguments()[0] == typeof(MongoId))
            {
                return new HashSet<MongoId> { new MongoId(tpl) };
            }

            if (targetType.IsGenericType
                && targetType.GetGenericTypeDefinition() == typeof(HashSet<>)
                && targetType.GetGenericArguments()[0] == typeof(string))
            {
                return new HashSet<string> { tpl };
            }

            if (typeof(IList<string>).IsAssignableFrom(targetType))
                return new List<string> { tpl };

            if (targetType.IsArray)
            {
                var elem = targetType.GetElementType();
                if (elem == typeof(string))
                    return new[] { tpl };
                if (elem == typeof(MongoId))
                    return new[] { new MongoId(tpl) };
            }

            var ctor = targetType.GetConstructor(new[] { typeof(string) });
            if (ctor != null)
                return ctor.Invoke(new object[] { tpl });

            return tpl;
        }

        private static void SetRaw(object obj, string name, object value)
        {
            var member = GetMember(obj, name);
            if (member == null)
                return;

            var targetType = member switch
            {
                PropertyInfo p => p.PropertyType,
                FieldInfo f => f.FieldType,
                _ => null
            };

            if (targetType == null)
                return;

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                targetType = underlying;

            object converted = value;

            if (value != null && !targetType.IsInstanceOfType(value))
            {
                try { converted = Convert.ChangeType(value, targetType); }
                catch { return; }
            }

            SetMember(obj, member, converted);
        }

        private static object? Get(object obj, string name)
        {
            var t = obj.GetType();
            return t.GetProperty(name)?.GetValue(obj)
                ?? t.GetField(name)?.GetValue(obj);
        }

        private static MemberInfo? GetMember(object obj, string name)
        {
            var t = obj.GetType();
            return (MemberInfo?)t.GetProperty(name)
                ?? t.GetField(name);
        }

        private static void SetMember(object obj, MemberInfo member, object value)
        {
            switch (member)
            {
                case PropertyInfo p when p.CanWrite:
                    p.SetValue(obj, value);
                    break;
                case FieldInfo f:
                    f.SetValue(obj, value);
                    break;
            }
        }
    }
}
