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

    /// <summary>Registers per branch table entry: a destination and up to three tracks to follow it.</summary>
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
    /// Track to play when the segment ends without a branch being taken, or 0 when the
    /// segment names none. A segment naming itself repeats until the viewer acts.
    /// </summary>
    public int ContinueTrack => this[RegContinueTrack];

    /// <summary>True when this segment puts a choice to the viewer.</summary>
    public bool OffersChoice => Kind is SegmentKind.Choice or SegmentKind.TaggedChoice;

    /// <summary>
    /// The branch table of this frame: up to <see cref="BranchEntryCount"/> entries of
    /// <see cref="BranchEntryStride"/> registers each. Entries naming track 0 are unused
    /// and omitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An entry is a short play list: a destination track, then up to three more tracks
    /// to play after it, ending at the first zero. On a
    /// <see cref="SegmentKind.TaggedChoice"/> segment the first register is a score
    /// threshold instead, and the play list starts in the second. Reading a tagged table
    /// the plain way yields destination numbers beyond the end of the disc, which is what
    /// makes the two cases tell apart.
    /// </para>
    /// <para>
    /// The table belongs to the frame, not the segment, and changes within a track: a
    /// timed prompt is a run of frames whose table differs from the rest. A lone entry in
    /// the last slot, standing for a whole segment, is how discs wire a button back to
    /// their menu.
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
                var tag = tagged ? this[register] : (byte)0;
                var first = tagged ? register + 1 : register;

                int track = this[first];
                if (track == 0) continue;

                var then = new List<int>(BranchEntryStride - 1);
                for (var r = first + 1; r < register + BranchEntryStride && this[r] != 0; r++)
                    then.Add(this[r]);

                entries.Add(new BranchEntry(slot, tag, track, then.ToArray()));
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

        // The header is laid out in nine-byte groups: either a sync word, or four
        // (value, register) pairs closed by an 0xFF separator. Reading it by position
        // rather than by scanning for 0xFF matters, because 0xFF is also a legal value:
        // the low byte of the frame index is 0xFF on frames 255, 511 and so on.
        const int group = 9;
        var i = 0;
        while (i + group <= header.Length)
        {
            if (header.Slice(i, sync.Length).SequenceEqual(sync))
            {
                i += group;
                continue;
            }

            for (var pair = i; pair < i + group - 1; pair += 2)
            {
                // Padding is written as 0xFF pairs; no state register is numbered 0xFF.
                if (header[pair + 1] != 0xFF) registers[header[pair + 1]] = header[pair];
            }

            i += group;
        }

        return new FrameHeader(registers);
    }
}

/// <summary>One entry in a branch table: a play list that a button press, or the score, selects.</summary>
/// <param name="Slot">Index of the entry, 0 through 5. This is what a button press selects.</param>
/// <param name="Tag">
/// On a <see cref="SegmentKind.TaggedChoice"/> segment, the score the player must have
/// reached for the entry to apply; entries are written highest first, and the first one
/// the score meets is taken. Zero on every other kind of segment.
/// </param>
/// <param name="Track">Destination track number.</param>
/// <param name="Then">
/// Tracks to play in order after the destination, whenever a track in the list names no
/// continuation of its own in register 0x4F. Null or empty for a plain jump.
/// </param>
public readonly record struct BranchEntry(int Slot, byte Tag, int Track, IReadOnlyList<int>? Then = null)
{
    /// <summary>Tracks queued behind the destination; never null.</summary>
    public IReadOnlyList<int> FollowOn => Then ?? [];

    /// <summary>The whole play list: the destination, then <see cref="FollowOn"/>.</summary>
    public IEnumerable<int> PlayList => FollowOn.Prepend(Track);
}

/// <summary>
/// Value of register 0x4B: what kind of segment this is, and what it does to the
/// player's score when it starts playing.
/// </summary>
/// <remarks>
/// The numbering comes from surveying retail discs; no published specification exists.
/// The player keeps a single score, starting at <c>0x64</c> (100), which
/// <see cref="TaggedChoice"/> segments branch on. See <c>docs/format.md</c>.
/// </remarks>
public enum SegmentKind : byte
{
    /// <summary>Register was not written.</summary>
    None = 0,

    /// <summary>Plays straight through with no viewer choice.</summary>
    Linear = 1,

    /// <summary>Offers a choice; the branch table names the destinations.</summary>
    Choice = 2,

    /// <summary>Branches on the score: each entry opens with the threshold it needs.</summary>
    TaggedChoice = 3,

    /// <summary>Adds one to the score: a right answer, or a prompt met.</summary>
    ScoreUp = 4,

    /// <summary>Takes one from the score: a wrong answer, or a prompt missed.</summary>
    ScoreDown = 5,

    /// <summary>Puts the score back to its starting value.</summary>
    ScoreReset = 6,
}
