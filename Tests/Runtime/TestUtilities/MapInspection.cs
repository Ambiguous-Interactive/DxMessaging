namespace DxMessaging.Tests
{
    using System.Reflection;
    using DxMessaging.Core.DataStructure;

    internal static class MapInspection
    {
        internal static int Capacity(object map) =>
            ((System.Array)MessageBusInspection.ReadField(map, "_values")).Length;

        private static int Bucket(object map, int key, int mask)
        {
            for (System.Type type = map.GetType(); type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(
                    "Bucket",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
                );
                if (method != null)
                {
                    return (int)method.Invoke(null, new object[] { key, mask });
                }
            }
            throw new System.MissingMethodException(map.GetType().FullName, "Bucket");
        }

        internal static int LongestCluster(object map)
        {
            System.Array values = (System.Array)MessageBusInspection.ReadField(map, "_values");

            if ((int)MessageBusInspection.ReadProperty(map, "Count") == 0)
            {
                return 0;
            }

            int mask = values.Length - 1;
            int empty = 0;
            while (values.GetValue(empty) != null)
            {
                empty++;
            }

            int longest = 0;
            int current = 0;
            for (int offset = 1; offset <= values.Length; ++offset)
            {
                int index = (empty + offset) & mask;
                if (values.GetValue(index) == null)
                {
                    current = 0;
                    continue;
                }

                current++;
                if (longest < current)
                {
                    longest = current;
                }
            }

            return longest;
        }

        internal static IntKeyMapTopologyObservation ObserveTopologyForBenchmark<TValue>(
            this IntKeyMap<TValue> map,
            int key
        )
            where TValue : class => Observe(map, key);

        internal static IntKeyMapTopologyObservation Observe(object map, int key)
        {
            System.Array values = (System.Array)MessageBusInspection.ReadField(map, "_values");
            int[] keys = (int[])MessageBusInspection.ReadField(map, "_keys");
            if (values.Length == 0)
            {
                return new IntKeyMapTopologyObservation(false, -1, 0, 0, 0);
            }

            int mask = values.Length - 1;
            int index = Bucket(map, key, mask);
            int lookupProbes = 1;
            while (values.GetValue(index) != null)
            {
                if (keys[index] == key)
                {
                    int deletionScans = 0;
                    int deletionMoves = 0;
                    int gap = index;
                    int scan = (gap + 1) & mask;
                    /*
                        This test-only geometry observer predicts the
                        exact gaps that production deletion will fill without instrumenting it.
                    */
                    while (values.GetValue(scan) != null)
                    {
                        deletionScans++;
                        int home = Bucket(map, keys[scan], mask);
                        if (((gap - home) & mask) < ((scan - home) & mask))
                        {
                            deletionMoves++;
                            gap = scan;
                        }

                        scan = (scan + 1) & mask;
                    }

                    return new IntKeyMapTopologyObservation(
                        true,
                        index,
                        lookupProbes,
                        deletionScans,
                        deletionMoves
                    );
                }

                index = (index + 1) & mask;
                lookupProbes++;
            }

            return new IntKeyMapTopologyObservation(false, -1, lookupProbes, 0, 0);
        }
    }
}
