using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepNorthCompat
{
    public static class BowCalculation
    {
        public const float VanillaReduction = 0.33f;

        // Upstream documents this value as the total reduction at skill 100.
        // Zero explicitly restores vanilla rather than removing vanilla's skill benefit.
        public static float Reduction(float configured, bool enabled)
        {
            return !enabled || configured <= 0 || configured > 1 || float.IsNaN(configured)
                ? VanillaReduction : configured;
        }

        public static float Drain(float baseDrain, float skill, float configured, bool enabled = true)
        {
            return baseDrain - baseDrain * Reduction(configured, enabled) * skill;
        }
    }

    public sealed class ResourceStack<TItem, TSource> where TItem : notnull where TSource : notnull
    {
        public readonly TItem Id;
        public readonly TSource Source;
        public readonly string Name;
        public readonly int Quality;
        public readonly int WorldLevel;
        public readonly int Count;
        public readonly bool Container;
        public readonly int Index;

        public ResourceStack(TItem id, TSource source, string name, int quality, int worldLevel,
            int count, bool container, int index = 0)
        {
            Id = id; Source = source; Name = name; Quality = quality;
            WorldLevel = worldLevel; Count = count; Container = container; Index = index;
        }
    }

    public sealed class ResourceNeed<TSource>
    {
        public readonly string Name;
        public readonly int MaximumQuality;
        public readonly int Count;
        public readonly Func<TSource, bool> AllowsSource;

        public ResourceNeed(string name, int maximumQuality, int count, Func<TSource, bool> allowsSource)
        {
            Name = name; MaximumQuality = maximumQuality; Count = count; AllowsSource = allowsSource;
        }
    }

    public sealed class Allocation<TItem, TSource> where TItem : notnull where TSource : notnull
    {
        public readonly ResourceStack<TItem, TSource> Stack;
        public readonly int Count;
        public Allocation(ResourceStack<TItem, TSource> stack, int count) { Stack = stack; Count = count; }
    }

    public sealed class SelectedIngredient<TItem, TSource> where TItem : notnull where TSource : notnull
    {
        public readonly ResourceNeed<TSource> Need;
        public readonly int Tier;
        public readonly IReadOnlyList<Allocation<TItem, TSource>> Allocations;
        public SelectedIngredient(ResourceNeed<TSource> need, int tier, List<Allocation<TItem, TSource>> allocations)
        {
            Need = need; Tier = tier; Allocations = allocations.AsReadOnly();
        }
    }

    public sealed class IngredientPlan<TItem, TSource> where TItem : notnull where TSource : notnull
    {
        public readonly IReadOnlyList<SelectedIngredient<TItem, TSource>> Ingredients;
        public IngredientPlan(List<SelectedIngredient<TItem, TSource>> ingredients) { Ingredients = ingredients.AsReadOnly(); }
        public IEnumerable<Allocation<TItem, TSource>> Allocations => Ingredients.SelectMany(i => i.Allocations);
    }

    public static class IngredientSelector
    {
        // Match ImpactfulSkills 0.20.2: lowest tier that covers the entire requirement.
        // If no single tier suffices, consume across tiers and award no quality bonus.
        public static IngredientPlan<TItem, TSource>? Select<TItem, TSource>(IReadOnlyList<ResourceStack<TItem, TSource>> stacks,
            IReadOnlyList<ResourceNeed<TSource>> needs, int worldLevel, bool leaveOne) where TItem : notnull where TSource : notnull
        {
            var remaining = stacks.ToDictionary(s => s.Id, s => s.Count);
            var result = new List<SelectedIngredient<TItem, TSource>>();
            foreach (ResourceNeed<TSource> need in needs)
            {
                if (need.Count <= 0) continue;
                var candidates = stacks.Where(s => s.Name == need.Name
                    && s.WorldLevel >= worldLevel
                    && need.AllowsSource(s.Source)).ToList();
                int Capacity(IEnumerable<ResourceStack<TItem, TSource>> subset)
                {
                    return subset.GroupBy(s => s.Source).Sum(group =>
                    {
                        int count = group.Sum(s => remaining[s.Id]);
                        if (!leaveOne || !group.First().Container) return count;

                        int total = candidates.Where(s => Equals(s.Source, group.Key)).Sum(s => remaining[s.Id]);
                        return Math.Min(count, Math.Max(0, total - 1));
                    });
                }

                if (Capacity(candidates) < need.Count) return null;
                int tier = 0;
                for (int quality = 1; quality <= need.MaximumQuality && need.MaximumQuality > 1; quality++)
                {
                    if (Capacity(candidates.Where(s => s.Quality == quality)) >= need.Count)
                    {
                        tier = quality;
                        break;
                    }
                }

                int outstanding = need.Count;
                var allocations = new List<Allocation<TItem, TSource>>();
                foreach (ResourceStack<TItem, TSource> stack in candidates)
                {
                    if (tier > 0 && stack.Quality != tier) continue;
                    int available = remaining[stack.Id];

                    if (leaveOne && stack.Container)
                    {
                        int total = candidates.Where(s => Equals(s.Source, stack.Source)).Sum(s => remaining[s.Id]);
                        available = Math.Min(available, Math.Max(0, total - 1));
                    }

                    int take = Math.Min(available, outstanding);
                    if (take <= 0) continue;
                    allocations.Add(new Allocation<TItem, TSource>(stack, take));
                    remaining[stack.Id] -= take;
                    outstanding -= take;
                    if (outstanding == 0) break;
                }

                if (outstanding != 0) throw new InvalidOperationException("Selection capacity and allocation disagreed.");
                result.Add(new SelectedIngredient<TItem, TSource>(need, tier, allocations));
            }
            return new IngredientPlan<TItem, TSource>(result);
        }
    }

    public enum ReservationState { Planned, Reserved, Committed, RolledBack }

    public sealed class IngredientReservation<TItem, TSource> where TItem : notnull where TSource : notnull
    {
        private readonly IngredientPlan<TItem, TSource> plan;
        private readonly List<Tuple<ResourceStack<TItem, TSource>, int>> before = new List<Tuple<ResourceStack<TItem, TSource>, int>>();
        public ReservationState State { get; private set; }

        public IngredientReservation(IngredientPlan<TItem, TSource> plan) { this.plan = plan; }

        public bool Reserve(Func<ResourceStack<TItem, TSource>, int> currentCount, Func<Allocation<TItem, TSource>, bool> remove,
            Action<ResourceStack<TItem, TSource>, int> restore)
        {
            if (State != ReservationState.Planned) return false;
            foreach (var group in plan.Allocations.GroupBy(a => a.Stack.Id))
            {
                ResourceStack<TItem, TSource> stack = group.First().Stack;
                int count = currentCount(stack);
                if (count < group.Sum(a => a.Count)) return false;
                before.Add(Tuple.Create(stack, count));
            }
            try
            {
                foreach (Allocation<TItem, TSource> allocation in plan.Allocations)
                {
                    if (!remove(allocation)) { Rollback(restore); return false; }
                }
                State = ReservationState.Reserved;
                return true;
            }
            catch { Rollback(restore); throw; }
        }

        public void Complete(bool outputCreated, Action<ResourceStack<TItem, TSource>, int> restore)
        {
            if (State != ReservationState.Reserved) return;
            if (outputCreated) State = ReservationState.Committed;
            else Rollback(restore);
        }

        public void Rollback(Action<ResourceStack<TItem, TSource>, int> restore)
        {
            if (State == ReservationState.Committed || State == ReservationState.RolledBack) return;
            // Restore original counts, even if a removal hook mutated a stack before throwing.
            // Original list indices also retain player-inventory ordering after rollback.
            foreach (var group in before.GroupBy(entry => entry.Item1.Source))
            {
                foreach (var entry in group.OrderBy(entry => entry.Item1.Index))
                {
                    restore(entry.Item1, entry.Item2);
                }
            }
            before.Clear();
            State = ReservationState.RolledBack;
        }
    }
}
