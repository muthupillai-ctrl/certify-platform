namespace Certyfi.Infrastructure.Anchoring.OpenTimestamps;

// Direct, line-for-line port of python-opentimestamps'
// opentimestamps/core/timestamp.py Timestamp class. A recursive proof tree:
// a message, a set of attestations (leaves), and a map of Op -> child
// Timestamp (edges). The serialize/deserialize interleaving below — "0xff
// prefix means more siblings follow, 0x00 tag means the next thing is an
// attestation, anything else is an Op tag" — is read verbatim from that
// file, not paraphrased; it's easy to get subtly wrong (off-by-one in which
// sibling gets the 0xff prefix) without the original in hand.
public sealed class Timestamp
{
    private const int DefaultRecursionLimit = 256;

    public byte[] Msg { get; }
    public HashSet<TimeAttestation> Attestations { get; } = [];
    public Dictionary<Op, Timestamp> Ops { get; } = [];

    public Timestamp(byte[] msg) => Msg = msg;

    // Adds all attestations/ops from `other` into this tree, recursing into
    // shared child ops — required so results from multiple calendar servers
    // (submitted to for redundancy) combine into one proof instead of
    // silently dropping whichever came back later.
    public void Merge(Timestamp other)
    {
        if (!Msg.AsSpan().SequenceEqual(other.Msg))
        {
            throw new InvalidOperationException("Can't merge timestamps for different messages.");
        }

        foreach (var attestation in other.Attestations)
        {
            Attestations.Add(attestation);
        }

        foreach (var (otherOp, otherChildStamp) in other.Ops)
        {
            if (Ops.TryGetValue(otherOp, out var ourChildStamp))
            {
                ourChildStamp.Merge(otherChildStamp);
            }
            else
            {
                Ops[otherOp] = otherChildStamp;
            }
        }
    }

    public void Serialize(OtsWriter w)
    {
        if (Attestations.Count == 0 && Ops.Count == 0)
        {
            throw new InvalidOperationException("An empty timestamp can't be serialized.");
        }

        var sortedAttestations = Attestations.OrderBy(a => a).ToList();
        if (sortedAttestations.Count > 1)
        {
            for (var i = 0; i < sortedAttestations.Count - 1; i++)
            {
                w.WriteBytes([0xff, 0x00]);
                sortedAttestations[i].Serialize(w);
            }
        }

        if (Ops.Count == 0)
        {
            w.WriteUInt8(0x00);
            sortedAttestations[^1].Serialize(w);
        }
        else
        {
            if (sortedAttestations.Count > 0)
            {
                w.WriteBytes([0xff, 0x00]);
                sortedAttestations[^1].Serialize(w);
            }

            var sortedOps = Ops.OrderBy(kv => kv.Key).ToList();
            for (var i = 0; i < sortedOps.Count - 1; i++)
            {
                w.WriteUInt8(0xff);
                sortedOps[i].Key.Serialize(w);
                sortedOps[i].Value.Serialize(w);
            }

            var (lastOp, lastStamp) = sortedOps[^1];
            lastOp.Serialize(w);
            lastStamp.Serialize(w);
        }
    }

    // `initialMsg` must be supplied because the wire format doesn't repeat
    // the message at every node — matches timestamp.py's own deserialize()
    // doc comment on this exact point.
    public static Timestamp Deserialize(OtsReader r, byte[] initialMsg, int recursionLimit = DefaultRecursionLimit)
    {
        if (recursionLimit <= 0)
        {
            throw new OtsFormatException("Reached timestamp recursion depth limit while deserializing.");
        }

        var self = new Timestamp(initialMsg);

        void DoTagOrAttestation(byte tag)
        {
            if (tag == 0x00)
            {
                self.Attestations.Add(TimeAttestation.Deserialize(r));
            }
            else
            {
                var op = Op.DeserializeFromTag(r, tag);

                byte[] result;
                try
                {
                    result = op.Apply(initialMsg);
                }
                catch (NotSupportedException ex)
                {
                    throw new OtsFormatException($"Invalid timestamp; message invalid for op {op}: {ex.Message}");
                }

                self.Ops[op] = Deserialize(r, result, recursionLimit - 1);
            }
        }

        var t = r.ReadUInt8();
        while (t == 0xff)
        {
            DoTagOrAttestation(r.ReadUInt8());
            t = r.ReadUInt8();
        }
        DoTagOrAttestation(t);

        return self;
    }

    // Like AllAttestations, but yields the tree node holding each
    // attestation rather than just its message — upgrading a pending
    // attestation means merging the calendar's answer INTO that node (see
    // OpenTimestampsCalendarClient.UpgradePendingAttestationsAsync). Port of
    // python-opentimestamps' Timestamp.all_attestations(), which yields
    // (msg, attestation) where the caller then walks back to the node.
    public IEnumerable<(Timestamp Node, TimeAttestation Attestation)> AllAttestationNodes()
    {
        foreach (var attestation in Attestations)
        {
            yield return (this, attestation);
        }

        foreach (var childStamp in Ops.Values)
        {
            foreach (var pair in childStamp.AllAttestationNodes())
            {
                yield return pair;
            }
        }
    }

    // Recursively yields every (leafMsg, attestation) pair in the tree —
    // this is what LedgerAnchorBackgroundService walks to find a
    // BitcoinBlockHeaderAttestation (and the exact leaf message it attests
    // to) regardless of how deep the calendar's merkle path nested it.
    public IEnumerable<(byte[] Msg, TimeAttestation Attestation)> AllAttestations()
    {
        foreach (var attestation in Attestations)
        {
            yield return (Msg, attestation);
        }

        foreach (var childStamp in Ops.Values)
        {
            foreach (var pair in childStamp.AllAttestations())
            {
                yield return pair;
            }
        }
    }
}
