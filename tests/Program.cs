using System;
using System.Collections.Generic;
using System.Linq;
using BoltToolApp.Data;
using BoltToolApp.Geometry;
using BoltToolApp.Torque;

internal static class Program
{
    static int _count;

    static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + description);
        _count++;
        Console.WriteLine("PASS " + description);
    }

    static bool Near(double a, double b) => Math.Abs(a - b) < 1e-8;

    static BoltJob Assembly()
    {
        var j = new BoltJob { IsAssembly = true, Standard = FastenerStandard.ISO,
            Size = "M8", Kind = PartKind.HexBolt, ClampLength = 58, AutoLength = true };
        j.EnsureStack();
        return j;
    }

    // Parts may touch but never occupy the same non-zero axial interval.
    // The bolt itself runs THROUGH the washers and nuts and is intentionally excluded.
    static void NoOverlaps(BoltJob job)
    {
        var l = PartBuilder.ComputeLayout(job);
        var items = l.Parts.Where(p => p.Item != null).OrderBy(p => p.X).ToList();
        for (int i = 1; i < items.Count; i++)
            Check(items[i - 1].X + items[i - 1].Thickness <= items[i].X + 1e-8,
                "No overlap between " + items[i - 1].Role + " / " + items[i].Role);
        foreach (var p in items)
            if (p.Item.Side == StackSide.Head)
                Check(p.X + p.Thickness <= l.ClampStart + 1e-8, "Head item not inside plate: " + p.Role);
            else
                Check(p.X >= l.ClampEnd - 1e-8, "Nut item not inside plate: " + p.Role);
    }

    static void Main()
    {
        var j = Assembly();
        var initial = PartBuilder.ComputeLayout(j);
        Check(initial.BoltLength >= initial.Required, "Auto bolt length fits 58 mm plates + all washers/nuts");
        Check(Near(initial.BoltLength, j.Table.NextStandardLength(initial.Required)), "Auto length uses next standard length");
        Check(initial.BoltLength > j.Row.DefaultLength, "58 mm clamp is NOT stuck at M8 default length");
        NoOverlaps(j);

        // Moving a washer away from the head translates the plates and nut side; plate thickness stays 58.
        var headLast = j.Stack[j.HeadCount - 1];
        StackMover.Move(j, new[] { headLast.Id }, 12, out var headMoved, out var headTarget);
        var newHead = PartBuilder.ComputeLayout(headMoved);
        Check(Near(headMoved.ClampLength, j.ClampLength), "Dragging head-side washer preserves plate thickness");
        Check(Near(newHead.ClampStart - initial.ClampStart, 12), "Dragging head washer moves the plate face 12 mm");
        Check(Near(newHead.ClampEnd - initial.ClampEnd, 12), "Dragging head washer moves the nut-side contact face");
        Check(Near(newHead.StackEnd - initial.StackEnd, 12), "Dragging head washer moves nut-side parts too");
        Check(newHead.BoltLength >= initial.BoltLength, "Auto bolt length grows if required");
        Check(headTarget == null, "Head washer cannot jump through clamped parts");
        NoOverlaps(headMoved);

        // A separately specified free gap behind the clamped parts must not reassign ClampLength.
        var nutFirst = j.Stack[j.HeadCount];
        j.SetGapBefore(j.HeadCount, 9);
        var separated = PartBuilder.ComputeLayout(j);
        Check(Near(j.ClampLength, 58), "First nut-side gap independent of 58 mm plate");
        Check(Near(separated.ClampStart, initial.ClampStart), "Free nut gap does not move head-side bearing face");
        Check(Near(separated.ClampEnd, initial.ClampEnd), "Free nut gap does not alter clamp length");
        Check(Near(separated.Parts.First(p => p.Role == nutFirst.Id).X - initial.Parts.First(p => p.Role == nutFirst.Id).X, 9),
            "First nut-side washer starts 9 mm after clamped material");
        NoOverlaps(j);
        StackMover.Move(j, new[] { nutFirst.Id }, -100, out var nutLive, out var nutTarget);
        Check(Near(nutLive.Stack[nutLive.HeadCount].Gap, 0), "Nut-side washer uses its free gap first");
        Check(Near(nutLive.ClampLength, 0) && nutTarget == null, "Nut-side washer squeezes the clamped parts to 0 and never hops through");
        var squeezed = PartBuilder.ComputeLayout(nutLive);
        Check(Near(squeezed.ClampEnd, squeezed.ClampStart), "Nut side then touches the head-side elements");
        NoOverlaps(nutLive);

        // Ctrl+drag group: nut side moves together and increases first free gap, not solid plate thickness.
        var allNuts = j.Stack.Where(p => p.Side == StackSide.Nut).Select(p => p.Id).ToArray();
        StackMover.Move(j, allNuts, 7, out var nutGroup, out _);
        Check(Near(nutGroup.ClampLength, 58), "Group move does not edit ClampLength");
        Check(Near(nutGroup.Stack[nutGroup.HeadCount].Gap, 16), "Group move adds to free gap after plate");
        NoOverlaps(nutGroup);

        // Drag the spring back over the nut-side plain washer (a same-side reorder).
        var swap = Assembly();
        var spring = swap.Stack.First(p => p.Kind == PartKind.SpringWasher && p.Side == StackSide.Nut);
        StackMover.Move(swap, new[] { spring.Id }, -15, out var live, out var ghost);
        Check(ghost != null, "Pull beyond neighbour creates a swap preview");
        Check(ghost.Stack[ghost.HeadCount].Id == spring.Id, "Release puts spring before nut-side washer");
        Check(Near(ghost.ClampLength, swap.ClampLength), "Swap preserves plate thickness");
        NoOverlaps(ghost);

        var dbl = Assembly();
        dbl.Stack = BoltJob.BuildStack(WasherChoice.Plain, WasherChoice.Plain,
            SpringPosition.None, PartKind.HexNut, 2, false);
        dbl.EnsureStack();
        var nuts = dbl.Stack.Where(p => p.Kind == PartKind.HexNut).ToList();
        Check(nuts.Count == 2, "Double-nut preset produces two nuts");
        Check(Near(PartBuilder.Thickness(nuts[0].Kind, dbl.Row), PartBuilder.Thickness(nuts[1].Kind, dbl.Row)),
            "Double-nut default uses equal-height nuts");
        NoOverlaps(dbl);

        // A malformed stored value must not poison layout coordinates with NaN/infinity.
        j.Stack[0].Gap = double.PositiveInfinity;
        j.Stack[0].Thickness = double.NaN;
        j.EnsureStack();
        Check(Near(j.Stack[0].Gap, 0) && Near(j.Stack[0].Thickness, 0), "Invalid numerical overrides are sanitized");

        var (dw, dh, pd) = TorqueCalculator.Bearing(j.Row, PartKind.HexBolt, true);
        var tr = TorqueCalculator.Calculate(j.Row, TorqueCalculator.Classes[4], 0.12, 0.12, 0.7,
            dw, dh, TorqueCalculator.Materials[0].PG, pd, TorqueMethod.JisTorqueCoefficient);
        Check(tr.PreloadKN > 0 && tr.TorqueNm > 0, "Mechanical torque result positive for M8");
        Check(Near(tr.K * j.Row.D * tr.PreloadKN, tr.TorqueNm), "Torque coefficient K consistent with T=K d F");

        Console.WriteLine($"SUCCESS: {_count} core-model assertions passed.");
    }
}
