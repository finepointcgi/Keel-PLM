namespace Keel.Process;

public sealed record MatrixCell(decimal Quantity, bool Whole);

/// <summary>One line of the bound MBOM revision with what each operation consumes of it. Coverage is none, partial, full, over or phantom (a phantom line is not consumed).</summary>
public sealed record MatrixRow(BoundLineView Line, IReadOnlyList<MatrixCell?> Cells, decimal Assigned, string Coverage);

public sealed record MatrixOperation(int Number, string Name);

/// <summary>The operation-by-component matrix of one routing (ADR-0042): the table alternative of the diagram and the answer of the REST API. Pure; built from what the reader may see.</summary>
public sealed record PlanMatrix(string RoutingCode, IReadOnlyList<MatrixOperation> Operations, IReadOnlyList<MatrixRow> Rows)
{
    public static PlanMatrix Build(RoutingView routing, IReadOnlyList<BoundLineView> lines)
    {
        var ops = routing.Operations.OrderBy(o => o.Number).ToList();
        var rows = new List<MatrixRow>();
        foreach (var line in lines.OrderBy(l => l.Find).ThenBy(l => l.Alt))
        {
            var cells = new List<MatrixCell?>();
            decimal total = 0;
            foreach (var o in ops)
            {
                var c = o.Components.FirstOrDefault(x => x.Find == line.Find && x.Alt == line.Alt);
                cells.Add(c is null ? null : new MatrixCell(c.AssignedQuantity, c.Whole));
                total += c?.AssignedQuantity ?? 0;
            }
            var coverage = line.Phantom ? "phantom" : total == 0 ? "none" : total > line.Quantity + 0.00001m ? "over" : total < line.Quantity - 0.00001m ? "partial" : "full";
            rows.Add(new MatrixRow(line, cells, total, coverage));
        }
        return new PlanMatrix(routing.Code, [.. ops.Select(o => new MatrixOperation(o.Number, o.Name))], rows);
    }

    public static string CoverageLabel(string coverage) => coverage switch
    {
        "none" => "Not consumed", "partial" => "Partly consumed", "full" => "Fully consumed", "over" => "Over-assigned", "phantom" => "Phantom (not consumed)", _ => coverage,
    };
}
