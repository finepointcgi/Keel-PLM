using System.Text;
using Keel.Core.Geometry;

namespace Keel.Cad.Structure;

/// <summary>
/// The STEP product count the model version history shows (ADR-0046): how many products the in-house STEP structure reader finds. Geometry is never read, so
/// nothing is claimed about size or volume. Hostile or damaged input yields null ("not read"), never an exception.
/// </summary>
public sealed class StepProductCounter : IStepProductCounter
{
    public int? CountProducts(ReadOnlyMemory<byte> content)
    {
        if (content.Length == 0 || content.Length > StructureLimits.MaxBytes) return null;
        try
        {
            return StepStructureReader.Read(StructureReader.DecodeStep(content.Span), new StructureReadOptions()).Parts.Count;
        }
        catch (Exception e) when (e is StructureFormatException or ArgumentException or FormatException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}
