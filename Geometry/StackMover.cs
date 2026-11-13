using System;
using System.Collections.Generic;
using System.Linq;
using BoltToolApp.Data;

namespace BoltToolApp.Geometry
{
    /// <summary>
    /// Moves washers / nuts along the bolt. Free gaps are independent of the solid clamped
    /// material thickness. Moving the final head-side element carries the plate and nut side
    /// along, while moving nut-side elements may increase their free gap after the plate.
    /// Pulling past a neighbour on the SAME side previews a swap; the clamped parts are a barrier.
    /// </summary>
    public static class StackMover
    {
        /// <summary>Smallest pull past the contact point (mm) that counts as "hop over the neighbour".</summary>
        const double HopMin = 2.5;

        sealed class Node
        {
            public StackItem It;
            public double Gap;      // actual free space before this element (plate thickness is separate)
        }

        /// <summary>
        /// The plate is a solid separator. Moving the last head-side part outward
        /// translates the plate and the entire nut-side stack without reducing the
        /// specified ClampLength. Conversely, the first nut-side part can move away
        /// from the plate by increasing its own free gap. Neither may hop through the plate.
        /// </summary>
        static (double lo, double hi) MoveLimits(List<Node> nodes, List<(int a, int b)> runs, double clamp)
        {
            int hc = nodes.Count(n => n.It.Side == StackSide.Head);
            if (runs.Any(r => r.a < hc) && runs.Any(r => r.b >= hc)) return (0, 0);
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            foreach (var (a, b) in runs)
            {
                // The first nut-side element may push into the clamped parts: their thickness shrinks down to 0,
                // so the nut side can come right up to the head-side elements.
                lo = Math.Max(lo, a == hc ? -(nodes[a].Gap + Math.Max(0, clamp)) : -nodes[a].Gap);
                // The separator is a solid plate, not a neighbour. Moving the last head-side
                // element shifts the plate and nut side; it does not consume a gap there.
                if (b != hc - 1 && b + 1 < nodes.Count)
                    hi = Math.Min(hi, nodes[b + 1].Gap);
            }
            return (Math.Min(lo, 0), Math.Max(hi, 0));
        }

        static List<Node> Read(BoltJob j)
        {
            j.EnsureStack();
            return j.Stack.Select(it => new Node { It = it, Gap = it.Gap }).ToList();
        }

        static void Write(BoltJob j, List<Node> nodes)
        {
            j.Stack = nodes.Select(n => n.It).ToList();
            foreach (var n in nodes) n.It.Gap = Math.Max(0, n.Gap);
            // ClampLength never changes when an element is dragged.
        }

        /// <summary>Maximal runs of consecutive selected elements, as inclusive index ranges.</summary>
        static List<(int a, int b)> Runs(List<Node> nodes, ICollection<string> sel)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int i = 0; i <= nodes.Count; i++)
            {
                bool s = i < nodes.Count && sel.Contains(nodes[i].It.Id);
                if (s && start < 0) start = i;
                if (!s && start >= 0) { runs.Add((start, i - 1)); start = -1; }
            }
            return runs;
        }

        /// <summary>How far the selection can move (mm, negative = toward the head) before it touches something.</summary>
        public static (double min, double max) Limits(BoltJob job, ICollection<string> sel)
        {
            var nodes = Read(job.Clone());
            return MoveLimits(nodes, Runs(nodes, sel), job.ClampLength);
        }

        /// <summary>
        /// Moves the selected elements by <paramref name="raw"/> mm from the state <paramref name="start"/>.
        /// live   = result with the movement stopped at contact (always returned).
        /// target = result when the group was pulled past a neighbour and takes its place; null when no swap.
        /// </summary>
        public static void Move(BoltJob start, ICollection<string> sel, double raw, out BoltJob live, out BoltJob target)
        {
            live = start.Clone();
            target = null;
            var nodes = Read(live);
            var runs = Runs(nodes, sel);
            if (runs.Count == 0 || Math.Abs(raw) < 1e-9) { Write(live, nodes); return; }

            var (lo, hi) = MoveLimits(nodes, runs, live.ClampLength);
            double d = Math.Clamp(raw, lo, hi);
            int hcLive = live.HeadCount;
            double squeeze = 0;                       // how much the clamped parts get thinner
            foreach (var (a, b) in runs)
            {
                if (a == hcLive && d < -nodes[a].Gap)
                {
                    squeeze = -d - nodes[a].Gap;      // free gap first, then the clamped parts
                    nodes[a].Gap = 0;
                }
                else nodes[a].Gap += d;
                if (b + 1 < nodes.Count && b != live.HeadCount - 1)
                    nodes[b + 1].Gap -= d; // do not alter the first nut's gap when a head part shifts the plate
            }
            Write(live, nodes);
            if (squeeze > 0) live.ClampLength = Math.Max(0, live.ClampLength - squeeze);

            double over = raw - d;
            if (runs.Count != 1 || Math.Abs(over) < 1e-9) return;
            int boundary = live.HeadCount;
            if (runs[0].a < boundary && runs[0].b >= boundary) return; // cannot hop a cross-plate selection

            // ---- the group was pulled past the contact point: let it hop over neighbours ----
            var t = live.Clone();
            var tn = Read(t);
            var row = t.Row;
            var (i0, i1) = Runs(tn, sel)[0];
            int headCount = t.HeadCount;
            bool swapped = false;
            for (int guard = 0; guard < tn.Count + 1; guard++)
            {
                if (over > 0)
                {
                    int j = i1 + 1;
                    if (j >= tn.Count || j == headCount) break; // clamped parts are a barrier
                    double free = Math.Min(over, tn[j].Gap);                 // travel through a free gap first
                    tn[i0].Gap += free; tn[j].Gap -= free; over -= free;
                    if (tn[j].Gap > 1e-9) break;
                    double th = PartBuilder.Thickness(tn[j].It.Kind, row, 0, tn[j].It);
                    if (over < Math.Max(th / 2, HopMin)) break;      // not pulled far enough to hop over it
                    // order  [run][N]  ->  [N][run];  N takes the leading gap, the run touches N
                    var n = tn[j];
                    double lead = tn[i0].Gap;
                    tn.RemoveAt(j);
                    tn.Insert(i0, n);
                    n.Gap = lead; tn[i0 + 1].Gap = 0;
                    for (int k = i0 + 1; k <= i1 + 1; k++) tn[k].It.Side = n.It.Side;
                    i0++; i1++;
                    over = Math.Max(0, over - th);
                    swapped = true;
                }
                else
                {
                    int j = i0 - 1;
                    if (i0 == headCount) break; // do not pull a nut-side item through the clamped parts
                    double free = Math.Min(-over, tn[i0].Gap);
                    tn[i0].Gap -= free; if (i1 + 1 < tn.Count) tn[i1 + 1].Gap += free; over += free;
                    if (tn[i0].Gap > 1e-9 || j < 0) break;
                    double th = PartBuilder.Thickness(tn[j].It.Kind, row, 0, tn[j].It);
                    if (-over < Math.Max(th / 2, HopMin)) break;     // not pulled far enough to hop over it
                    // order  [N][run]  ->  [run][N];  the run takes N's leading gap, N touches the run
                    var n = tn[j];
                    double lead = n.Gap;
                    tn.RemoveAt(j);
                    tn.Insert(i1, n);
                    i0--; i1--;
                    tn[i0].Gap = lead; n.Gap = 0;
                    for (int k = i0; k <= i1; k++) tn[k].It.Side = n.It.Side;
                    over = Math.Min(0, over + th);
                    swapped = true;
                }
                if (Math.Abs(over) < 1e-9) break;
            }
            if (!swapped) return;
            Write(t, tn);
            t.EnsureStack();
            target = t;
        }

        /// <summary>Usual order: head | (nut) spring washer | clamped parts | washer spring nut (thin nut last).</summary>
        public static void SortUsual(BoltJob job)
        {
            job.EnsureStack();
            int Rank(StackItem it)
            {
                bool head = it.Side == StackSide.Head;
                int r = it.Kind switch
                {
                    PartKind.PlainWasher or PartKind.LargeWasher => 0,      // washer touches the clamped part
                    PartKind.SpringWasher => 1,
                    PartKind.ChannelNut or PartKind.ChannelNutHolder => -1,                              // grips the channel lips directly
                    PartKind.FlangeNut or PartKind.HexNut => 2,
                    _ => 3                                                  // thin (lock) nut outermost
                };
                return head ? -r : r;                                       // mirrored on the head side
            }
            job.Stack = job.Stack.Where(i => i.Side == StackSide.Head).OrderBy(Rank)
                .Concat(job.Stack.Where(i => i.Side == StackSide.Nut).OrderBy(Rank)).ToList();
            job.EnsureStack();
        }
    }
}
