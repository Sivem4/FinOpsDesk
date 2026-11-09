using FinOpsDesk.Modules.Reconciliation.Application.Allocations;

namespace FinOpsDesk.UnitTests.Application.Allocations;

public sealed class AllocationDisputeQueueCursorTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 9, 10, 0, 0, 123, TimeSpan.FromMinutes(120));

    [Fact]
    public void Encode_round_trips_the_boundary_including_an_empty_identifier()
    {
        string text = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Backward,
            AllocationDisputeQueue.NeedsCorrection,
            RecordedAt,
            Guid.Empty);

        Assert.True(text.Length <= 64);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.DoesNotContain('=', text);
        Assert.True(AllocationDisputeQueueCursor.TryDecode(text, AllocationDisputeQueue.NeedsCorrection, out AllocationDisputeQueueCursor? cursor));
        Assert.Equal(AllocationDisputeQueueCursorDirection.Backward, cursor!.Direction);
        Assert.Equal(AllocationDisputeQueue.NeedsCorrection, cursor.Queue);
        Assert.Equal(RecordedAt.Ticks, cursor.RecordedAt.Ticks);
        Assert.Equal(RecordedAt.Offset, cursor.RecordedAt.Offset);
        Assert.Equal(Guid.Empty, cursor.DisputeId);
    }

    [Theory]
    [InlineData(-840)]
    [InlineData(840)]
    public void Encode_accepts_whole_minute_offsets_at_the_boundary(int minutes)
    {
        DateTimeOffset recordedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.FromMinutes(minutes));
        string text = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Forward,
            AllocationDisputeQueue.NeedsDecision,
            recordedAt,
            Guid.Parse("01000000-0000-0000-0000-000000000000"));

        Assert.True(AllocationDisputeQueueCursor.TryDecode(text, AllocationDisputeQueue.NeedsDecision, out AllocationDisputeQueueCursor? cursor));
        Assert.Equal(recordedAt, cursor!.RecordedAt);
    }

    [Fact]
    public void TryDecode_rejects_a_different_queue_before_a_row_lookup()
    {
        string text = AllocationDisputeQueueCursor.Encode(
            AllocationDisputeQueueCursorDirection.Forward,
            AllocationDisputeQueue.NeedsDecision,
            RecordedAt,
            Guid.NewGuid());

        Assert.False(AllocationDisputeQueueCursor.TryDecode(text, AllocationDisputeQueue.NeedsCorrection, out _));
        Assert.False(AllocationDisputeQueueCursor.TryDecode(text + "A", AllocationDisputeQueue.NeedsDecision, out _));
        Assert.False(AllocationDisputeQueueCursor.TryDecode(new string('A', 65), AllocationDisputeQueue.NeedsDecision, out _));
    }
}