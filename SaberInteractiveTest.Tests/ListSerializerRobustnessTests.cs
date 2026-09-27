using SerializerTests.Implementations;
using SerializerTests.Nodes;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace SerializerTests.Tests
{
    /// <summary>
    /// Covers what the original test file does not exercise: working from an arbitrary node,
    /// leaving the source list untouched, and rejecting truncated or out of range input.
    /// </summary>
    public class ListSerializerRobustnessTests
    {
        private static readonly IvanImplementation sut = new();

        [Fact]
        public async Task Serialize_FromMiddleNode_ProducesWholeList()
        {
            ListNode head = BuildList(5, randomize: false);
            head.Random = head.Next!.Next;
            head.Next.Next!.Next!.Random = head;

            using var stream = new MemoryStream();
            await sut.Serialize(head.Next!.Next, stream);
            stream.Position = 0;

            var result = await sut.Deserialize(stream);

            AssertListsEqual(head, result);
        }

        [Fact]
        public async Task Serialize_FromTail_ProducesWholeList()
        {
            ListNode head = BuildList(4, randomize: false);
            head.Next!.Next!.Next!.Random = head;

            using var stream = new MemoryStream();
            await sut.Serialize(head.Next.Next.Next, stream);
            stream.Position = 0;

            var result = await sut.Deserialize(stream);

            AssertListsEqual(head, result);
        }

        [Fact]
        public async Task DeepCopy_FromMiddleNode_CopiesWholeList()
        {
            ListNode head = BuildList(6, randomize: true);
            ListNode middle = head.Next!.Next!.Next!;

            var copy = await sut.DeepCopy(middle);

            AssertListsEqual(head, copy);
            Assert.NotSame(head, copy);
        }

        [Fact]
        public async Task DeepCopy_LeavesOriginalListUntouched()
        {
            ListNode head = BuildList(6, randomize: true);
            NodeState[] before = Snapshot(head);

            var copy = await sut.DeepCopy(head);

            Assert.Equal(before, Snapshot(head));
            AssertListsEqual(head, copy);
        }

        [Fact]
        public async Task DeepCopy_ReturnsIndependentNodeInstances()
        {
            ListNode head = BuildList(5, randomize: true);

            var copy = await sut.DeepCopy(head);

            ListNode? original = head;
            ListNode? cloned = copy;
            int visited = 0;
            while (original is not null)
            {
                Assert.NotSame(original, cloned);
                if (cloned!.Random is not null)
                    Assert.NotSame(original.Random, cloned.Random);

                original = original.Next;
                cloned = cloned.Next;
                visited++;
            }

            Assert.Equal(5, visited);
            Assert.Same(copy.Next!.Previous, copy);
        }

        [Fact]
        public async Task DeepCopy_CopyIsStableAfterOriginalIsMutated()
        {
            ListNode head = BuildList(4, randomize: true);
            var copy = await sut.DeepCopy(head);
            NodeState[] copyBefore = Snapshot(copy);

            // Rewiring the original must not become visible through the copy.
            head.Random = head.Next;
            head.Next!.Next!.Random = null;
            head.Next.Next!.Data = "changed";
            head.Next.Next.Next!.Next = null;

            Assert.Equal(copyBefore, Snapshot(copy));
        }

        [Fact]
        public async Task SerializeDeserialize_EmptyString_IsNotNullData()
        {
            var node1 = new ListNode { Data = string.Empty };
            var node2 = new ListNode { Data = null, Previous = node1 };
            node1.Next = node2;
            node1.Random = node2;

            using var stream = new MemoryStream();
            await sut.Serialize(node1, stream);
            stream.Position = 0;

            var result = await sut.Deserialize(stream);

            Assert.NotNull(result.Data);
            Assert.Equal(string.Empty, result.Data);
            Assert.Null(result.Next!.Data);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("Привет, мир!")]
        [InlineData("emoji 🐍🚀 and zero \0 char")]
        [InlineData("line1\nline2\r\n\ttabbed")]
        [InlineData("𝄞 musical symbol")]
        public async Task SerializeDeserialize_TextPayloads_PreserveData(string data)
        {
            var node = new ListNode { Data = data };
            node.Random = node;

            using var stream = new MemoryStream();
            await sut.Serialize(node, stream);
            stream.Position = 0;

            var result = await sut.Deserialize(stream);

            Assert.Equal(data, result.Data);
            Assert.Same(result, result.Random);
        }

        [Fact]
        public async Task SerializeDeserialize_PayloadLargerThanInternalBuffer_PreservesData()
        {
            // Longer than the 256 KB working buffer, exercises the buffer growth path.
            string big = new('Z', 400_000);
            var node1 = new ListNode { Data = big };
            var node2 = new ListNode { Data = "tail", Previous = node1 };
            node1.Next = node2;
            node1.Random = node2;
            node2.Random = node2;

            using var stream = new MemoryStream();
            await sut.Serialize(node1, stream);
            stream.Position = 0;

            var result = await sut.Deserialize(stream);

            Assert.Equal(big, result.Data);
            Assert.Equal("tail", result.Next!.Data);
            Assert.Same(result.Next, result.Random);
            Assert.Same(result.Next, result.Next!.Random);
        }

        [Fact]
        public async Task SerializeDeserialize_ForwardOnlyStream_RoundTrips()
        {
            ListNode head = BuildList(50, randomize: true);

            var forward = new ForwardOnlyStream();
            await sut.Serialize(head, forward);

            var result = await sut.Deserialize(new ForwardOnlyStream(forward.Written));

            AssertListsEqual(head, result);
        }

        [Fact]
        public async Task Serialize_WritesExpectedWireFormat()
        {
            var node1 = new ListNode { Data = "A" };
            var node2 = new ListNode { Data = "B", Previous = node1 };
            node1.Next = node2;
            node2.Random = node2; // self reference

            using var stream = new MemoryStream();
            await sut.Serialize(node1, stream);

            byte[] expected = Concat(
                Int32(2),                                             // nodeCount
                Int32(1), Encoding.UTF8.GetBytes("A"), Int32(-1),    // null Random
                Int32(1), Encoding.UTF8.GetBytes("B"), Int32(1));    // Random points to node 1

            Assert.Equal(expected, stream.ToArray());
        }

        [Fact]
        public async Task Serialize_NullData_WritesNegativeLength()
        {
            var node = new ListNode { Data = null };

            using var stream = new MemoryStream();
            await sut.Serialize(node, stream);

            Assert.Equal(Concat(Int32(1), Int32(-1), Int32(-1)), stream.ToArray());
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task Deserialize_TruncatedHeader_Throws(int availableBytes)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => sut.Deserialize(new ForwardOnlyStream(new byte[availableBytes])));
        }

        [Fact]
        public async Task Deserialize_MissingListBody_Throws()
        {
            // nodeCount = 1 announced, but not a single node follows.
            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new ForwardOnlyStream(Int32(1))));
        }

        [Fact]
        public async Task Deserialize_TruncatedStringBody_Throws()
        {
            byte[] payload = Concat(Int32(1), Int32(5), Encoding.UTF8.GetBytes("abc"));

            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new ForwardOnlyStream(payload)));
        }

        [Fact]
        public async Task Deserialize_TruncatedRandomIndex_Throws()
        {
            byte[] payload = Concat(Int32(1), Int32(1), Encoding.UTF8.GetBytes("A"), new byte[2]);

            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new ForwardOnlyStream(payload)));
        }

        [Fact]
        public async Task Deserialize_TruncatedSeekableStream_ThrowsBeforeAllocating()
        {
            // A seekable stream lets the reader reject the cut from Length instead of a per node check.
            ListNode head = BuildList(4, randomize: true);
            using var stream = new MemoryStream();
            await sut.Serialize(head, stream);

            byte[] full = stream.ToArray();
            byte[] truncated = new byte[full.Length - 4];
            Array.Copy(full, truncated, truncated.Length);

            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new MemoryStream(truncated)));
        }

        [Fact]
        public async Task Deserialize_TruncatedForwardOnlyStream_Throws()
        {
            ListNode head = BuildList(4, randomize: true);
            using var stream = new MemoryStream();
            await sut.Serialize(head, stream);

            byte[] full = stream.ToArray();
            byte[] truncated = new byte[full.Length - 5];
            Array.Copy(full, truncated, truncated.Length);

            // A non seekable stream cannot be pre-validated by length, the reader must still detect the cut.
            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new ForwardOnlyStream(truncated)));
        }

        [Fact]
        public async Task Deserialize_NegativeNodeCount_Throws()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new MemoryStream(Int32(-7))));
        }

        [Fact]
        public async Task Deserialize_ZeroNodeCount_ReturnsNull()
        {
            var result = await sut.Deserialize(new MemoryStream(Int32(0)));

            Assert.Null(result);
        }

        [Fact]
        public async Task Deserialize_NegativeDataLength_Throws()
        {
            byte[] payload = Concat(Int32(1), Int32(-2), Int32(-1));

            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new MemoryStream(payload)));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(99)]
        [InlineData(int.MaxValue)]
        public async Task Deserialize_RandomIndexAboveNodeCount_Throws(int randomIndex)
        {
            byte[] payload = Concat(Int32(1), Int32(0), Int32(randomIndex));

            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new MemoryStream(payload)));
        }

        [Theory]
        [InlineData(-2)]
        [InlineData(int.MinValue)]
        public async Task Deserialize_RandomIndexBelowMinusOne_Throws(int randomIndex)
        {
            byte[] payload = Concat(Int32(1), Int32(0), Int32(randomIndex));

            await Assert.ThrowsAsync<ArgumentException>(() => sut.Deserialize(new MemoryStream(payload)));
        }

        [Fact]
        public async Task Deserialize_TrailingDataAfterList_IsIgnored()
        {
            ListNode head = BuildList(3, randomize: true);
            using var stream = new MemoryStream();
            await sut.Serialize(head, stream);

            byte[] payload = stream.ToArray();
            byte[] withTail = new byte[payload.Length + 16];
            Array.Copy(payload, withTail, payload.Length);
            Encoding.UTF8.GetBytes("TRAILING-GARBAGE").CopyTo(withTail, payload.Length);

            var result = await sut.Deserialize(new MemoryStream(withTail));

            AssertListsEqual(head, result);
        }

        [Fact]
        public async Task Deserialize_TwoListsInOneStream_ReadsOnlyTheFirst()
        {
            ListNode first = BuildList(3, randomize: false);
            ListNode second = BuildList(2, randomize: false);

            using var stream = new MemoryStream();
            await sut.Serialize(first, stream);
            await sut.Serialize(second, stream);
            stream.Position = 0;

            var result = await sut.Deserialize(stream);

            AssertListsEqual(first, result);
        }

        [Fact]
        public async Task Serialize_RandomPointingOutsideList_Throws()
        {
            var node = new ListNode { Data = "lonely" };
            node.Random = new ListNode { Data = "foreign" };

            using var stream = new MemoryStream();

            await Assert.ThrowsAsync<ArgumentException>(() => sut.Serialize(node, stream));
        }

        [Fact]
        public async Task DeepCopy_RandomPointingOutsideList_Throws()
        {
            var node = new ListNode { Data = "lonely" };
            node.Random = new ListNode { Data = "foreign" };

            await Assert.ThrowsAsync<ArgumentException>(() => sut.DeepCopy(node));
        }

        [Fact]
        public async Task SerializeDeserialize_RoundTrip_IsStableAcrossThreeGenerations()
        {
            ListNode original = BuildList(30, randomize: true);

            ListNode generation = original;
            for (int i = 0; i < 3; i++)
            {
                using var stream = new MemoryStream();
                await sut.Serialize(generation, stream);
                stream.Position = 0;
                generation = await sut.Deserialize(stream);
                AssertListsEqual(original, generation);
            }
        }

        [Fact]
        public async Task RoundTrip_ManyNodes_KeepsEveryRandomIndex()
        {
            const int count = 5_000;
            ListNode head = BuildList(count, randomize: true);
            ListNode[] expected = ToArray(head);
            var expectedRandomIndices = new int[count];
            for (int i = 0; i < count; i++)
            {
                expectedRandomIndices[i] = expected[i].Random is null ? -1 : Array.IndexOf(expected, expected[i].Random);
            }

            using var stream = new MemoryStream();
            await sut.Serialize(head, stream);
            stream.Position = 0;
            var result = await sut.Deserialize(stream);

            ListNode[] actual = ToArray(result);
            Assert.Equal(count, actual.Length);
            for (int i = 0; i < count; i++)
            {
                int actualIndex = actual[i].Random is null ? -1 : Array.IndexOf(actual, actual[i].Random);
                Assert.Equal(expectedRandomIndices[i], actualIndex);
            }
        }

        // ---------- helpers ----------

        private static ListNode BuildList(int count, bool randomize)
        {
            var nodes = new ListNode[count];
            for (int i = 0; i < count; i++)
            {
                nodes[i] = new ListNode
                {
                    Data = $"Node{i}",
                    Previous = i > 0 ? nodes[i - 1] : null
                };

                if (i > 0)
                    nodes[i - 1].Next = nodes[i];
            }

            if (randomize)
            {
                var random = new Random(12345);
                for (int i = 0; i < count; i++)
                {
                    if (random.NextDouble() < 0.7)
                        nodes[i].Random = nodes[random.Next(count)];
                }
            }

            return nodes[0];
        }

        private static ListNode[] ToArray(ListNode head)
        {
            var list = new List<ListNode>();
            for (ListNode? current = head; current is not null; current = current.Next)
                list.Add(current);

            return list.ToArray();
        }

        /// <summary>Fingerprint of every field, used to prove the source list was not modified.</summary>
        private static NodeState[] Snapshot(ListNode head)
        {
            var states = new List<NodeState>();
            foreach (ListNode node in ToArray(head))
            {
                states.Add(new NodeState(
                    node.Data,
                    IndexOf(head, node.Previous),
                    IndexOf(head, node.Next),
                    IndexOf(head, node.Random)));
            }

            return states.ToArray();
        }

        private static int IndexOf(ListNode head, ListNode? node)
        {
            if (node is null)
                return -1;

            int index = 0;
            for (ListNode? current = head; current is not null; current = current.Next, index++)
            {
                if (ReferenceEquals(current, node))
                    return index;
            }

            return int.MinValue;
        }

        private static void AssertListsEqual(ListNode expected, ListNode actual)
        {
            ListNode[] expectedNodes = ToArray(expected);
            ListNode[] actualNodes = ToArray(actual);
            Assert.Equal(expectedNodes.Length, actualNodes.Length);

            for (int i = 0; i < expectedNodes.Length; i++)
            {
                int expectedNext = i + 1 < actualNodes.Length ? i + 1 : -1;

                Assert.Equal(expectedNodes[i].Data, actualNodes[i].Data);
                Assert.Equal(i - 1, IndexOf(actual, actualNodes[i].Previous));
                Assert.Equal(expectedNext, IndexOf(actual, actualNodes[i].Next));
                Assert.Equal(
                    IndexOf(expected, expectedNodes[i].Random),
                    IndexOf(actual, actualNodes[i].Random));
            }
        }

        private static byte[] Int32(int value)
        {
            var buffer = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            return buffer;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int length = 0;
            foreach (byte[] part in parts)
                length += part.Length;

            var result = new byte[length];
            int offset = 0;
            foreach (byte[] part in parts)
            {
                Array.Copy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private sealed record NodeState(string? Data, int Previous, int Next, int Random);

        /// <summary>In memory stream that refuses to seek, which forces forward only access.</summary>
        private sealed class ForwardOnlyStream : Stream
        {
            private byte[] _buffer;
            private int _position;

            public ForwardOnlyStream()
            {
                _buffer = Array.Empty<byte>();
            }

            public ForwardOnlyStream(byte[] content)
            {
                _buffer = content;
            }

            public byte[] Written => _buffer;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int available = Math.Min(count, _buffer.Length - _position);
                if (available <= 0)
                    return 0;

                Array.Copy(_buffer, _position, buffer, offset, available);
                _position += available;
                return available;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                int required = _position + count;
                if (required > _buffer.Length)
                    Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));

                Array.Copy(buffer, offset, _buffer, _position, count);
                _position += count;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
