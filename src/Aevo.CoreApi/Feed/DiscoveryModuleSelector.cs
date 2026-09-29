using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

/// <summary>
/// Additive module boundary. The flat Feed items remain the compatibility
/// surface while server-owned modules expose only entity references. Module
/// minimums are allocated as a disjoint set before each shelf is filled so a
/// broad module cannot consume the inventory needed by a narrower shelf.
/// </summary>
public static class DiscoveryModuleSelector
{
    public static IReadOnlyList<DiscoveryModuleContract> Select(
        IReadOnlyList<IFeedItemContract> items,
        string? selectedArea,
        FeedDiscoveryIntentContract? intent,
        FeedDiscoveryModulesConfig? modules = null,
        IReadOnlyList<FeedGeneratorTelemetry>? generators = null)
    {
        var modulePolicy = modules ?? FeedDiscoveryConfigDefaults.Config.Modules;
        if (!modulePolicy.Enabled)
        {
            return Array.Empty<DiscoveryModuleContract>();
        }

        var candidateItems = items
            .Where(item => DiscoveryContract.IsCandidateType(item.ItemType)
                && !string.IsNullOrWhiteSpace(item.Id)
                && !string.IsNullOrWhiteSpace(item.ItemToken))
            .GroupBy(ItemKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (candidateItems.Length < modulePolicy.MinimumItems) return Array.Empty<DiscoveryModuleContract>();

        var moduleIds = modulePolicy.Order
            .Where(moduleId => !string.IsNullOrWhiteSpace(moduleId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidateIndexByKey = candidateItems
            .Select((item, index) => (key: ItemKey(item), index))
            .ToDictionary(value => value.key, value => value.index, StringComparer.Ordinal);
        var modulePools = moduleIds
            .Select(moduleId => ModulePool(moduleId, candidateItems, selectedArea)
                .Select(item => candidateIndexByKey[ItemKey(item)])
                .ToArray())
            .ToArray();
        var requiredItems = Math.Min(Math.Max(1, modulePolicy.MinimumItems), modulePolicy.MaximumItems);
        if (requiredItems == 0) return Array.Empty<DiscoveryModuleContract>();

        var slots = new List<(int ModuleIndex, int SlotIndex)>();
        for (var moduleIndex = 0; moduleIndex < modulePools.Length; moduleIndex++)
        {
            if (modulePools[moduleIndex].Length < requiredItems) continue;
            for (var slotIndex = 0; slotIndex < requiredItems; slotIndex++)
            {
                slots.Add((moduleIndex, slotIndex));
            }
        }

        var itemOwners = Enumerable.Repeat(-1, candidateItems.Length).ToArray();
        var slotItems = Enumerable.Repeat(-1, slots.Count).ToArray();
        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            TryAssignSlot(
                slotIndex,
                slots,
                modulePools,
                itemOwners,
                slotItems,
                new HashSet<int>());
        }

        var assignedByModule = slots
            .Select((slot, index) => (slot, itemIndex: slotItems[index]))
            .Where(value => value.itemIndex >= 0)
            .GroupBy(value => value.slot.ModuleIndex)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.itemIndex).ToHashSet());
        var completeModules = assignedByModule
            .Where(pair => pair.Value.Count >= requiredItems)
            .Select(pair => pair.Key)
            .ToHashSet();

        var claimed = assignedByModule
            .Where(pair => completeModules.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToHashSet();
        var selectedByModule = new Dictionary<int, IReadOnlyList<IFeedItemContract>>();
        foreach (var moduleIndex in completeModules.OrderBy(index => index))
        {
            var mandatoryItems = modulePools[moduleIndex]
                .Where(itemIndex => assignedByModule[moduleIndex].Contains(itemIndex))
                .ToArray();
            var fillItems = modulePools[moduleIndex]
                .Where(itemIndex => !assignedByModule[moduleIndex].Contains(itemIndex) && claimed.Add(itemIndex))
                .Take(Math.Max(0, modulePolicy.MaximumItems - mandatoryItems.Length));
            var moduleItems = mandatoryItems
                .Concat(fillItems)
                .Select(itemIndex => candidateItems[itemIndex])
                .ToArray();
            if (moduleItems.Length < requiredItems) continue;

            selectedByModule[moduleIndex] = moduleItems;
        }

        var selected = new List<DiscoveryModuleContract>();
        for (var moduleIndex = 0; moduleIndex < moduleIds.Length; moduleIndex++)
        {
            if (!selectedByModule.TryGetValue(moduleIndex, out var moduleItems)) continue;

            var references = moduleItems
                .Select(item => new DiscoveryModuleItemReferenceContract(item.ItemType, item.Id, item.ItemToken))
                .ToArray();
            if (references.Length == 0 || references.Length < modulePolicy.MinimumItems) continue;

            selected.Add(new DiscoveryModuleContract(
                moduleIds[moduleIndex],
                ModuleReason(moduleIds[moduleIndex], selectedArea, intent),
                references,
                IsModuleDegraded(moduleIds[moduleIndex], generators)));
        }

        return selected;
    }

    /// <summary>
    /// A shelf is degraded only when one of the bounded generators it depends
    /// on failed or timed out. Disabled, empty, and surface-not-supported
    /// generators are deliberate configuration outcomes, not outages. This is
    /// generator health only; booking availability is not read on the Feed hot
    /// path and never inferred here.
    /// </summary>
    private static bool IsModuleDegraded(
        string moduleId,
        IReadOnlyList<FeedGeneratorTelemetry>? generators)
    {
        if (generators is null || generators.Count == 0) return false;

        var failedGenerators = generators
            .Where(generator => generator.Status is "failed" or "timeout")
            .Select(generator => generator.Generator)
            .ToHashSet(StringComparer.Ordinal);
        if (failedGenerators.Count == 0) return false;

        return moduleId switch
        {
            "NEW_AND_USEFUL" => failedGenerators.Contains("trace-v1"),
            "FOR_YOU" or "NEAR_SELECTED_AREA" or "COMMUNITY_FAVORITES"
                => failedGenerators.Contains("trace-v1") || failedGenerators.Contains("place-v1"),
            _ => false
        };
    }

    private static bool TryAssignSlot(
        int slotIndex,
        IReadOnlyList<(int ModuleIndex, int SlotIndex)> slots,
        IReadOnlyList<int[]> modulePools,
        int[] itemOwners,
        int[] slotItems,
        ISet<int> visitedItems)
    {
        var moduleIndex = slots[slotIndex].ModuleIndex;
        foreach (var itemIndex in modulePools[moduleIndex])
        {
            if (!visitedItems.Add(itemIndex)) continue;

            var ownerSlot = itemOwners[itemIndex];
            if (ownerSlot >= 0 && !TryAssignSlot(ownerSlot, slots, modulePools, itemOwners, slotItems, visitedItems)) continue;

            itemOwners[itemIndex] = slotIndex;
            slotItems[slotIndex] = itemIndex;
            return true;
        }

        return false;
    }

    private static IFeedItemContract[] ModulePool(
        string moduleId,
        IReadOnlyList<IFeedItemContract> candidateItems,
        string? selectedArea) => moduleId switch
        {
            "FOR_YOU" => candidateItems.ToArray(),
            "NEAR_SELECTED_AREA" when !string.IsNullOrWhiteSpace(selectedArea)
                => candidateItems.Where(item => string.Equals(ItemArea(item), selectedArea, StringComparison.OrdinalIgnoreCase)).ToArray(),
            "NEW_AND_USEFUL" => candidateItems.Where(item => string.Equals(ItemReason(item), "NEW_TRACE", StringComparison.Ordinal)).ToArray(),
            "COMMUNITY_FAVORITES" when !string.IsNullOrWhiteSpace(selectedArea)
                => candidateItems.Where(item => string.Equals(ItemArea(item), selectedArea, StringComparison.OrdinalIgnoreCase)
                    && (ItemReason(item) is "POPULAR" or "POPULAR_PLACE")).ToArray(),
            "COMMUNITY_FAVORITES" => candidateItems.Where(item => ItemReason(item) is "POPULAR" or "POPULAR_PLACE").ToArray(),
            _ => Array.Empty<IFeedItemContract>()
        };

    private static string ItemKey(IFeedItemContract item) => $"{item.ItemType}:{item.Id}";

    private static string? ItemArea(IFeedItemContract item) => item switch
    {
        FeedTraceItemContract trace => trace.Area,
        FeedPlaceItemContract place => place.Area,
        _ => null
    };

    private static string? ItemReason(IFeedItemContract item) => item switch
    {
        FeedTraceItemContract trace => trace.ReasonCode,
        FeedPlaceItemContract place => place.ReasonCode,
        _ => null
    };

    private static string? ModuleReason(string moduleId, string? selectedArea, FeedDiscoveryIntentContract? intent) => moduleId switch
    {
        "NEAR_SELECTED_AREA" => "NEAR_SELECTED_AREA",
        "NEW_AND_USEFUL" => string.IsNullOrWhiteSpace(selectedArea) ? null : "NEW_IN_AREA",
        "COMMUNITY_FAVORITES" => string.IsNullOrWhiteSpace(selectedArea) ? "COMMUNITY_CONFIDENCE" : "POPULAR_IN_AREA",
        "FOR_YOU" => intent?.Vibe is not null and not "match"
            ? "BECAUSE_VIBE"
            : intent?.Category is not null
                ? "BECAUSE_CATEGORY"
                : selectedArea is not null
                    ? "NEAR_SELECTED_AREA"
                    : null,
        _ => null
    };
}
