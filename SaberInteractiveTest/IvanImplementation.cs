using SerializerTests.Interfaces;
using SerializerTests.Nodes;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SerializerTests.Implementations
{
    /// <summary>
    /// Serializer for a doubly linked list with optional <see cref="ListNode.Random"/> links.
    /// </summary>
    /// <remarks>
    /// <para>Wire format (little-endian, self-contained, no magic and no version field):</para>
    /// <code>
    /// nodeCount : int32                                  ( >= 1, present only for a non-null list )
    /// repeated nodeCount times, in list order:
    ///     dataLength   : int32                          ( -1 = null string, otherwise UTF-8 byte count )
    ///     data         : byte[dataLength]               ( UTF-8 payload )
    ///     randomIndex  : int32                          ( -1 = null link, otherwise 0 <= index < nodeCount )
    /// </code>
    /// <para>
    /// A null list is written as zero bytes, therefore an empty stream always deserializes to
    /// <c>null</c>. The list is emitted from its head, so any node of the list can be passed in.
    /// Bytes following the last node are ignored by <see cref="Deserialize"/>.
    /// </para>
    /// <para>This type is not thread safe.</para>
    /// </remarks>
    public class IvanImplementation : IListSerializer
    {
        /// <summary>Working buffer size, keeps the number of stream calls proportional to payload size, not to node count.</summary>
        private const int BufferCapacity = 256 * 1024;

        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        public IvanImplementation()
        {

        }

        /// <summary>
        /// Writes the whole list that contains <paramref name="head"/> into <paramref name="s"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="s"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The list is malformed, or a <c>Random</c> link points outside of it.</exception>
        public async Task Serialize(ListNode head, Stream s)
        {
            if (s is null)
                throw new ArgumentNullException(nameof(s));

            // A null list is encoded as an empty stream, which deserializes back to null.
            if (head is null)
                return;

            // The caller may hand over any node of the list, the format always starts at the head.
            while (head.Previous is not null)
                head = head.Previous;

            // One pass: a duplicate key means the Next chain loops back, i.e. the input is not a valid list.
            var nodeIndex = new Dictionary<ListNode, int>();
            int index = 0;
            for (ListNode? current = head; current is not null; current = current.Next)
                nodeIndex.Add(current, index++);

            int count = nodeIndex.Count;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferCapacity);
            try
            {
                int written = 0;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(written), count);
                written = sizeof(int);

                for (ListNode? current = head; current is not null; current = current.Next)
                {
                    string? data = current.Data;
                    int dataLength = data is null ? -1 : Utf8.GetByteCount(data);
                    int needed = sizeof(int) + (dataLength < 0 ? 0 : dataLength) + sizeof(int);

                    if (buffer.Length - written < needed)
                    {
                        await s.WriteAsync(buffer.AsMemory(0, written));
                        written = 0;
                    }

                    // Payloads larger than the buffer are written in one go, the buffer grows to fit.
                    if (buffer.Length < needed)
                        buffer = Grow(buffer, written, needed);

                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(written), dataLength);
                    written += sizeof(int);

                    if (dataLength > 0)
                    {
                        Utf8.GetBytes(data!.AsSpan(), buffer.AsSpan(written));
                        written += dataLength;
                    }

                    int randomIndex = -1;
                    if (current.Random is not null)
                    {
                        if (!nodeIndex.TryGetValue(current.Random, out randomIndex))
                        {
                            throw new ArgumentException(
                                "The Random link of a node does not reference a node of the serialized list.",
                                nameof(head));
                        }
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(written), randomIndex);
                    written += sizeof(int);
                }

                if (written > 0)
                    await s.WriteAsync(buffer.AsMemory(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Reads one list from <paramref name="s"/> and returns its head.
        /// </summary>
        /// <returns>The head node, or <c>null</c> for an empty stream or a zero node count.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="s"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The stream is truncated or contains out of range values.</exception>
        public async Task<ListNode> Deserialize(Stream s)
        {
            if (s is null)
                throw new ArgumentNullException(nameof(s));

            using var reader = new BufferedStreamReader(s);

            if (!await reader.EnsureAsync(sizeof(int)))
            {
                // A partially present header is corruption, a fully empty stream is an absent list.
                if (reader.Buffered == 0)
                    return null;

                throw Truncated("node count", sizeof(int), reader.Buffered);
            }

            int nodeCount = reader.ReadInt32();
            if (nodeCount < 0)
                throw new ArgumentException($"Negative node count: {nodeCount}.", nameof(s));

            if (nodeCount == 0)
                return null;

            // Every node occupies at least 8 bytes, so a seekable stream can be validated up front
            // and a bogus node count cannot trigger a huge allocation. The node count header has
            // already been consumed, so only the node bodies are still outstanding.
            if (s.CanSeek)
            {
                long required = (long)nodeCount * (2 * sizeof(int));

                // The reader buffers ahead, so Position is ahead of the logical read position and
                // has to be corrected by whatever is still sitting in the buffer. Without the
                // correction a short list in a MemoryStream looks like an empty tail and every
                // valid payload would be rejected as truncated.
                long available = s.Length - s.Position + reader.Buffered;
                if (available < required)
                    throw Truncated($"a list of {nodeCount} node(s)", required, available);
            }

            var nodes = new ListNode[nodeCount];
            var randomIndices = new int[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                int dataLength = await reader.ReadInt32Async();
                if (dataLength < -1)
                    throw new ArgumentException($"Negative data length {dataLength} at node {i}.", nameof(s));

                string? data = dataLength < 0 ? null : await reader.ReadStringAsync(dataLength);

                int randomIndex = await reader.ReadInt32Async();
                if (randomIndex < -1 || randomIndex >= nodeCount)
                {
                    throw new ArgumentException(
                        $"Random index {randomIndex} at node {i} is outside the valid range [-1, {nodeCount}).",
                        nameof(s));
                }

                var node = new ListNode { Data = data! };
                if (i > 0)
                {
                    node.Previous = nodes[i - 1];
                    nodes[i - 1].Next = node;
                }

                nodes[i] = node;
                randomIndices[i] = randomIndex;
            }

            // Second pass, a Random link may point forward to a node that was not created yet.
            for (int i = 0; i < nodeCount; i++)
            {
                int randomIndex = randomIndices[i];
                if (randomIndex >= 0)
                    nodes[i].Random = nodes[randomIndex];
            }

            return nodes[0];
        }

        /// <summary>
        /// Copies the whole list that contains <paramref name="head"/> without touching the original nodes.
        /// </summary>
        /// <returns>The head of the copy, or <c>null</c> when <paramref name="head"/> is <c>null</c>.</returns>
        /// <exception cref="ArgumentException">The list is malformed, or a <c>Random</c> link points outside of it.</exception>
        public Task<ListNode> DeepCopy(ListNode head)
        {
            if (head is null)
                return Task.FromResult<ListNode>(null!);

            while (head.Previous is not null)
                head = head.Previous;

            return Task.FromResult(CopyList(head));
        }

        private static ListNode CopyList(ListNode head)
        {
            var copies = new Dictionary<ListNode, ListNode>();
            ListNode? previousCopy = null;

            for (ListNode? current = head; current is not null; current = current.Next)
            {
                var copy = new ListNode { Data = current.Data, Previous = previousCopy! };
                if (previousCopy is not null)
                    previousCopy.Next = copy;

                // A duplicate key means the Next chain loops back, i.e. the input is not a valid list.
                copies.Add(current, copy);
                previousCopy = copy;
            }

            // The original nodes are only read here, so the source list stays intact during the whole copy.
            for (ListNode? current = head; current is not null; current = current.Next)
            {
                if (current.Random is null)
                    continue;

                if (!copies.TryGetValue(current.Random, out ListNode? randomCopy))
                {
                    throw new ArgumentException(
                        "The Random link of a node does not reference a node of the copied list.",
                        nameof(head));
                }

                copies[current].Random = randomCopy;
            }

            return copies[head];
        }

        private static byte[] Grow(byte[] buffer, int written, int needed)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, buffer.Length * 2));
            Buffer.BlockCopy(buffer, 0, larger, 0, written);
            ArrayPool<byte>.Shared.Return(buffer);
            return larger;
        }

        private static ArgumentException Truncated(string what, long required, long available)
        {
            return new ArgumentException(
                $"Truncated stream: {what} needs {required} byte(s) but only {available} byte(s) are available.");
        }

        /// <summary>
        /// Minimal forward-only reader over a stream, backed by a pooled buffer.
        /// </summary>
        private sealed class BufferedStreamReader : IDisposable
        {
            private readonly Stream _stream;
            private byte[] _buffer;
            private int _start;
            private int _end;

            public BufferedStreamReader(Stream stream)
            {
                _stream = stream;
                _buffer = ArrayPool<byte>.Shared.Rent(BufferCapacity);
            }

            /// <summary>Number of bytes read from the stream but not consumed yet.</summary>
            public int Buffered => _end - _start;

            public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);

            /// <summary>Waits until <paramref name="count"/> bytes are buffered, or returns <c>false</c> on end of stream.</summary>
            public async ValueTask<bool> EnsureAsync(int count)
            {
                if (_end - _start >= count)
                    return true;

                if (count > _buffer.Length)
                    Grow(count);

                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }

                while (_end - _start < count)
                {
                    int read = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end));
                    if (read <= 0)
                        return false;

                    _end += read;
                }

                return true;
            }

            public int ReadInt32()
            {
                int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_start, sizeof(int)));
                _start += sizeof(int);
                return value;
            }

            public async ValueTask<int> ReadInt32Async()
            {
                if (!await EnsureAsync(sizeof(int)))
                    throw Truncated("an int32 value", sizeof(int), Buffered);

                return ReadInt32();
            }

            public async ValueTask<string> ReadStringAsync(int byteCount)
            {
                if (!await EnsureAsync(byteCount))
                    throw Truncated($"a string of {byteCount} byte(s)", byteCount, Buffered);

                string value = Utf8.GetString(_buffer, _start, byteCount);
                _start += byteCount;
                return value;
            }

            private void Grow(int count)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent(count);
                Buffer.BlockCopy(_buffer, _start, larger, 0, _end - _start);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = larger;
                _end -= _start;
                _start = 0;
            }
        }
    }
}
