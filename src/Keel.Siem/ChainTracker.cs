using System.Security.Cryptography;
using Keel.Audit;

namespace Keel.Siem;

/// <summary>What is wrong with the chain the forwarder reads.</summary>
public enum ChainBreakKind
{
    /// <summary>A sequence number is missing: events were deleted from the middle of the log.</summary>
    Gap,
    /// <summary>An event's prev_hash is not the hash of the event before it: a link was changed.</summary>
    LinkMismatch,
    /// <summary>An event's hash is not SHA-256 over its prev_hash, seq and payload: the row was altered after it was written.</summary>
    HashMismatch,
    /// <summary>Events the forwarder already delivered are gone or different in the database: the tail was deleted or rewritten after it was forwarded.</summary>
    TailChanged,
}

public sealed record ChainBreak(ChainBreakKind Kind, long Seq, string Detail)
{
    public string KindText => Kind switch
    {
        ChainBreakKind.Gap => "gap",
        ChainBreakKind.LinkMismatch => "link_mismatch",
        ChainBreakKind.HashMismatch => "hash_mismatch",
        _ => "tail_changed",
    };
}

/// <summary>
/// Verifies, as events are read in order, that they continue the chain: gapless sequence numbers, each prev_hash equal to the previous event's hash,
/// each hash recomputable from the stored payload (ADR-0005). It starts from the forwarder's cursor, so a restart keeps verifying the same chain.
/// After a break it resynchronizes on the event that follows, so one break is reported once and forwarding continues.
/// </summary>
public sealed class ChainTracker(long lastSeq, byte[] lastHash)
{
    public long LastSeq { get; private set; } = lastSeq;
    public byte[] LastHash { get; private set; } = lastHash;

    /// <summary>Checks the next event and moves the tracker to it. Returns the problems found with it (usually none).</summary>
    public IReadOnlyList<ChainBreak> Accept(AuditRecord r)
    {
        var problems = new List<ChainBreak>();
        if (r.Seq != LastSeq + 1)
            problems.Add(new ChainBreak(ChainBreakKind.Gap, r.Seq, $"expected seq {LastSeq + 1} after {LastSeq}, read {r.Seq}"));
        if (!r.PrevHash.AsSpan().SequenceEqual(LastHash))
            problems.Add(new ChainBreak(ChainBreakKind.LinkMismatch, r.Seq, $"prev_hash of seq {r.Seq} is not the hash of seq {LastSeq}"));
        var recomputed = AuditChain.ComputeHash(r.PrevHash, r.Seq, r.Payload);
        if (!CryptographicOperations.FixedTimeEquals(recomputed, r.Hash))
            problems.Add(new ChainBreak(ChainBreakKind.HashMismatch, r.Seq, $"hash of seq {r.Seq} does not match its content"));
        LastSeq = r.Seq;
        LastHash = r.Hash;
        return problems;
    }
}
