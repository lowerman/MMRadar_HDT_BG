using System;
using System.Collections.Generic;
using System.Linq;

namespace MMRadar.Wallii
{
    /// <summary>
    /// Port of wallii.gg's placement estimation (wall-lii-app/utils/calculatePlacements.ts).
    /// wallii has no real per-game placements — it snapshots the official leaderboard
    /// every few minutes and infers a game (and its likely placement) from each MMR delta.
    /// </summary>
    public static class PlacementEstimator
    {
        private static readonly double[] Placements =
            { 1, 2, 3, 3.5, 4, 4.5, 5, 5.5, 6, 6.5, 7, 7.5, 8 };

        public static double EstimatePlacement(double start, double end)
        {
            var gain = end - start;
            var dexAvg = start < 8200 ? start : start - 0.85 * (start - 10000);

            var best = Placements[0];
            var bestDelta = double.PositiveInfinity;
            foreach (var p in Placements)
            {
                var avgOpp = start - 148.1181435 * (100 - ((p - 1) * (200.0 / 7) + gain));
                if (avgOpp > 10000)
                    continue;
                var delta = Math.Abs(dexAvg - avgOpp);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>
        /// Turns leaderboard snapshots into inferred game records, most recent first.
        /// Consecutive snapshots with identical rating are treated as "no game" and skipped.
        /// </summary>
        public static List<GameRecord> BuildGameRecords(IEnumerable<SnapshotRow> snapshots)
        {
            var sorted = snapshots.OrderBy(s => s.SnapshotTime).ToList();
            var records = new List<GameRecord>();
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                var start = sorted[i];
                var end = sorted[i + 1];
                var delta = end.Rating - start.Rating;
                if (delta == 0)
                    continue;
                records.Add(new GameRecord
                {
                    At = end.SnapshotTime,
                    Placement = EstimatePlacement(start.Rating, end.Rating),
                    DeltaMmr = delta,
                    EndingMmr = end.Rating,
                });
            }
            records.Reverse();
            return records;
        }

        public static double? Average(IReadOnlyList<GameRecord> records)
        {
            if (records == null || records.Count == 0)
                return null;
            return Math.Round(records.Average(r => r.Placement), 2);
        }
    }
}
