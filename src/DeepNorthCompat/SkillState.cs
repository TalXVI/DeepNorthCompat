using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepNorthCompat
{
    // Pure rules shared by the publisher, owner-side readers and offline tests.
    public static class SkillState
    {
        public const int Protocol = 1;
        public static bool ValidFactor(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= 1;

        public static T? Closest<T>(IEnumerable<T> players, Func<T, float> squaredDistance,
            Func<T, long> id, float range) where T : class
            => players.Where(p => squaredDistance(p) <= range * range)
                .OrderBy(squaredDistance).ThenBy(id).FirstOrDefault();

        public static float RequireFactor(int protocol, float value)
        {
            if (protocol != Protocol || !ValidFactor(value))
                throw new InvalidOperationException("Missing or invalid DeepNorthCompat skill state. All players must use the matching compatibility package.");
            return value;
        }
    }
}
