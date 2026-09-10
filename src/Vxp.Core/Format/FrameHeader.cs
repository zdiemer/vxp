namespace Vxp.Format;

/// <summary>
/// The decoded per-frame header that precedes the pixel data of every VideoNow frame.
/// </summary>
/// <remarks>
/// <para>
/// The header is not a struct but a little command stream aimed at the player's display
/// controller. After the repeated sync words it is a run of <c>(value, register)</c>
/// byte pairs, grouped four pairs at a time and separated by an <c>0xFF</c> byte, with
/// the tail of each block padded out with <c>0xFF</c>. Registers below
/// <see cref="FirstStateRegister"/> drive the panel (gamma ramp and timing) and are
/// identical on every disc; the block starting at 0x49 carries the player state that
/// actually matters for emulation.
/// </para>
/// <para>
/// See <c>docs/format.md</c> for the derivation and for the register map.
/// </para>
/// </remarks>
public sealed class FrameHeader
{
    /// <summary>Registers at or above this index carry player state rather than panel setup.</summary>
    public const int FirstStateRegister = 0x49;

    /// <summary>Register holding the low byte of the current frame index within the track.</summary>
    public const int RegFrameIndexLow = 0x49;

    /// <summary>Register holding the high byte of the current frame index.</summary>
    public const int RegFrameIndexHigh = 0x4A;

    /// <summary>Register holding the segment's <see cref="SegmentKind"/>.</summary>
    public const int RegSegmentKind = 0x4B;

    /// <summary>Register holding the low byte of the track's total frame count.</summary>
    public const int RegFrameCountLow = 0x4D;

    /// <summary>Register holding the high byte of the track's total frame count.</summary>
    public const int RegFrameCountHigh = 0x4E;

    /// <summary>Register holding the track to continue with when no branch is taken.</summary>
    public const int RegContinueTrack = 0x4F;

    /// <summary>First register of the branch table.</summary>
    public const int RegBranchTableBase = 0x50;

    /// <summary>Registers per branch table entry. Only the first two are populated.</summary>
    public const int BranchEntryStride = 4;

    /// <summary>Number of branch table entries.</summary>
    public const int BranchEntryCount = 6;

    /// <summary>Register holding the current track number.</summary>
    public const int RegTrackNumber = 0x77;

    private readonly byte[] _registers;

    private FrameHeader(byte[] registers) => _registers = registers;

    /// <summary>Raw register file, indexed by register number. Unwritten registers read as zero.</summary>
    public ReadOnlySpan<byte> Registers => _registers;

    /// <summary>Reads a single register.</summary>
    public byte this[int register] => (uint)register < (uint)_registers.Length ? _registers[register] : (byte)0;

    /// <summary>Track number the player reports for this frame.</summary>
    public int TrackNumber => this[RegTrackNumber];

    /// <summary>Zero-based index of this frame within its track.</summary>
    public int FrameIndex => this[RegFrameIndexLow] | (this[RegFrameIndexHigh] << 8);

    /// <summary>Total number of frames in this track, as declared by the disc.</summary>
    public int FrameCount => this[RegFrameCountLow] | (this[RegFrameCountHigh] << 8);

    /// <summary>How the player should treat this segment when it ends.</summary>
    public SegmentKind Kind => (SegmentKind)this[RegSegmentKind];

    /// <summary>
    /// Track to play when the segment ends without a branch being taken, or 0 for
    /// "carry on with the next track in disc order".
    /// </summary>
    public int ContinueTrack => this[RegContinueTrack];

    /// <summary>True when this segment puts a choice to the viewer.</summary>
    public bool OffersChoice => Kind is SegmentKind.Choice or SegmentKind.TaggedChoice;

    /// <summary>
    /// The segment's branch table: up to <see cref="BranchEntryCount"/> destinations,
    /// each occupying <see cref="BranchEntryStride"/> registers of which only the first
    /// two are populated. Entries naming track 0 are unused and omitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two bytes are read differently depending on <see cref="Kind"/>.
    /// <see cref="SegmentKind.TaggedChoice"/> segments put a selector tag first and the
    /// destination track second; every other kind puts the destination track first.
    /// Reading a tagged table the plain way yields destination numbers beyond the end of
    /// the disc, which is what makes the two cases tell apart.
    /// </para>
    /// <para>
    /// Note that branch entries also appear on segments that are not choice points, most
    /// often a lone entry in the last slot. Check <see cref="OffersChoice"/> before
    /// treating the table as something the viewer can act on.
    /// </para>
    /// </remarks>
    public IReadOnlyList<BranchEntry> Branches
    {
        get
        {
            var tagged = Kind == SegmentKind.TaggedChoice;
            var entries = new List<BranchEntry>(BranchEntryCount);

            for (var slot = 0; slot < BranchEntryCount; slot++)
            {
                var register = RegBranchTableBase + slot * BranchEntryStride;
                byte first = this[register];
                byte second = this[register + 1];

                var (tag, track) = tagged ? (first, second) : (second, first);
                if (track == 0) continue;

                entries.Add(new BranchEntry(slot, tag, track));
            }

            return entries;
        }
    }

    /// <summary>
    /// Parses the header from the de-interleaved video bytes of a frame.
    /// </summary>
    public static FrameHeader Parse(ReadOnlySpan<byte> videoBytes, FrameLayout layout)
    {
        if (videoBytes.Length < layout.HeaderBytes)
            throw new ArgumentException("Frame is shorter than its header.", nameof(videoBytes));

        var registers = new byte[256];
        var header = videoBytes[..layout.HeaderBytes];
        var sync = FrameLayout.SyncWord;

        var i = 0;
        while (i + 1 < header.Length)
        {
            // Skip the repeated sync words that open each block of the header.
            if (i + sync.Length <= header.Length && header.Slice(i, sync.Length).SequenceEqual(sync))
            {
                i += sync.Length;
                continue;
            }

            // 0xFF separates groups of four pairs and pads the tail of a block.
            if (header[i] == 0xFF)
            {
                i++;
                continue;
            }

            registers[header[i + 1]] = header[i];
            i += 2;
        }

        return new FrameHeader(registers);
    }
}

/// <summary>One selectable destination in a segment's branch table.</summary>
/// <param name="Slot">Index of the entry, 0 through 5. This is what a button press selects.</param>
/// <param name="Tag">
/// The entry's second byte. On a <see cref="SegmentKind.TaggedChoice"/> segment this is
/// a selector code drawn from a small fixed set that repeats across discs, so it most
/// likely identifies the control the viewer presses; on other segments it is usually the
/// number of the segment the entry belongs to. Its exact meaning is not established, and
/// it is preserved here so tooling can study it.
/// </param>
/// <param name="Track">Destination track number.</param>
public readonly record struct BranchEntry(int Slot, byte Tag, int Track);

/// <summary>
/// Value of register 0x4B, which tells the player how a segment behaves when it ends.
/// </summary>
/// <remarks>
/// The numbering comes from surveying interactive discs. Names describe observed
/// behaviour rather than any published specification.
/// </remarks>
public enum SegmentKind : byte
{
    /// <summary>Register was not written.</summary>
    None = 0,

    /// <summary>Plays straight through with no viewer choice.</summary>
    Linear = 1,

    /// <summary>Offers a choice; the branch table names the destinations.</summary>
    Choice = 2,

    /// <summary>Offers a choice using the tagged two-byte branch encoding.</summary>
    TaggedChoice = 3,

    /// <summary>Plays through and stops rather than running on.</summary>
    Terminal = 4,

    /// <summary>Hub segment that returns to the track named by register 0x4F.</summary>
    Hub = 5,

    /// <summary>End of the title; returns to the track named by register 0x4F.</summary>
    Restart = 6,
}
