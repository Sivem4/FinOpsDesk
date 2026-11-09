using System.Buffers.Binary;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public enum AllocationDisputeQueueCursorDirection
{
    Forward = 1,
    Backward = 2,
}

public sealed record AllocationDisputeQueueCursor(
    AllocationDisputeQueueCursorDirection Direction,
    AllocationDisputeQueue Queue,
    DateTimeOffset RecordedAt,
    Guid DisputeId)
{
    public const int MaxTextLength = 64;

    public const int PayloadLength = 29;

    public const byte Version = 1;

    public const short MinimumOffsetMinutes = -840;

    public const short MaximumOffsetMinutes = 840;

    public static string Encode(
        AllocationDisputeQueueCursorDirection direction,
        AllocationDisputeQueue queue,
        DateTimeOffset recordedAt,
        Guid disputeId)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "Cursor direction is not defined.");
        }

        if (!Enum.IsDefined(queue))
        {
            throw new ArgumentOutOfRangeException(nameof(queue), "Cursor queue is not defined.");
        }

        long minuteTicks = recordedAt.Offset.Ticks;
        if (minuteTicks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                "Cursor offset must be a whole number of minutes.");
        }

        short minutes = checked((short)(minuteTicks / TimeSpan.TicksPerMinute));
        if (minutes < MinimumOffsetMinutes || minutes > MaximumOffsetMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordedAt),
                "Cursor offset is outside the DateTimeOffset range.");
        }

        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Version;
        payload[1] = (byte)direction;
        payload[2] = (byte)queue;
        BinaryPrimitives.WriteInt64LittleEndian(payload[3..], recordedAt.Ticks);
        BinaryPrimitives.WriteInt16LittleEndian(payload[11..], minutes);
        if (!disputeId.TryWriteBytes(payload[13..]))
        {
            throw new InvalidOperationException("Dispute id did not fit in the cursor payload.");
        }

        return Base64Url.Encode(payload);
    }

    public static bool TryDecode(
        string text,
        AllocationDisputeQueue expectedQueue,
        out AllocationDisputeQueueCursor? cursor)
    {
        cursor = null;
        if (text.Length == 0 || text.Length > MaxTextLength || !Base64Url.IsUnpadded(text))
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = Base64Url.Decode(text);
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.Length != PayloadLength
            || payload[0] != Version
            || payload[1] is not ((byte)AllocationDisputeQueueCursorDirection.Forward
                or (byte)AllocationDisputeQueueCursorDirection.Backward)
            || payload[2] is not ((byte)AllocationDisputeQueue.NeedsDecision
                or (byte)AllocationDisputeQueue.NeedsCorrection))
        {
            return false;
        }

        AllocationDisputeQueue queue = (AllocationDisputeQueue)payload[2];
        if (!Enum.IsDefined(expectedQueue) || queue != expectedQueue)
        {
            return false;
        }

        short minutes = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(11));
        if (minutes < MinimumOffsetMinutes || minutes > MaximumOffsetMinutes)
        {
            return false;
        }

        long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(3));
        DateTimeOffset recordedAt;
        try
        {
            recordedAt = new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        cursor = new AllocationDisputeQueueCursor(
            (AllocationDisputeQueueCursorDirection)payload[1],
            queue,
            recordedAt,
            new Guid(payload.AsSpan(13)));
        return true;
    }

    private static class Base64Url
    {
        public static string Encode(ReadOnlySpan<byte> payload)
        {
            string encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (encoded.Length > MaxTextLength)
            {
                throw new InvalidOperationException("Encoded allocation dispute cursor exceeds 64 characters.");
            }

            return encoded;
        }

        public static bool IsUnpadded(string text)
        {
            foreach (char character in text)
            {
                if ((character is < 'A' or > 'Z')
                    && (character is < 'a' or > 'z')
                    && (character is < '0' or > '9')
                    && character is not '-' and not '_')
                {
                    return false;
                }
            }

            return true;
        }

        public static byte[] Decode(string text)
        {
            string padded = text.Replace('-', '+').Replace('_', '/');
            int remainder = padded.Length % 4;
            if (remainder == 1)
            {
                throw new FormatException("Base64Url payload length is not valid.");
            }

            if (remainder > 0)
            {
                padded = padded.PadRight(padded.Length + (4 - remainder), '=');
            }

            return Convert.FromBase64String(padded);
        }
    }
}