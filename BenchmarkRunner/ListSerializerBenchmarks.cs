using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SerializerTests.Implementations;
using SerializerTests.Interfaces;
using SerializerTests.Nodes;

namespace SerializerTests.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class ListSerializerBenchmarks
    {
        [Params(1_000_000)]
        public int ListSize { get; set; }

        [Params(typeof(IvanImplementation))]
        public Type SerializerType { get; set; }

        private ListNode _testList;
        private IListSerializer _serializer;
        private byte[] _serializedData;

        [GlobalSetup]
        public void Setup()
        {
            // Create test list
            _testList = CreateList(ListSize, randomize: true);

            // Initialize serializer
            _serializer = (IListSerializer)Activator.CreateInstance(SerializerType)!;

            // Pre-serialize data for Deserialize benchmark
            using (var stream = new MemoryStream())
            {
                _serializer.Serialize(_testList, stream).GetAwaiter().GetResult();
                _serializedData = stream.ToArray();
            }
        }

        private ListNode CreateList(int count, bool randomize = true)
        {
            if (count == 0) return null!;

            var nodes = new ListNode[count];
            for (int i = 0; i < count; i++)
            {
                nodes[i] = new ListNode
                {
                    Data = $"Node{i}",
                    Previous = i > 0 ? nodes[i - 1] : null
                };
            }

            for (int i = 0; i < count - 1; i++)
            {
                nodes[i].Next = nodes[i + 1];
            }

            if (randomize)
            {
                var rand = new Random(42);
                for (int i = 0; i < count; i++)
                {
                    if (rand.NextDouble() < 0.7)
                    {
                        nodes[i].Random = nodes[rand.Next(0, count)];
                    }
                }
            }

            return nodes[0];
        }

        [Benchmark]
        public async Task<ListNode> Serialize()
        {
            using var stream = new MemoryStream();
            await _serializer.Serialize(_testList, stream);
            return _testList; // dummy return
        }

        [Benchmark]
        public async Task<ListNode> Deserialize()
        {
            using var stream = new MemoryStream(_serializedData);
            return await _serializer.Deserialize(stream);
        }

        [Benchmark]
        public async Task<ListNode> DeepCopy()
        {
            return await _serializer.DeepCopy(_testList);
        }
    }

    public class BenchmarkProgram
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("Running list serializer benchmarks.");
            Console.WriteLine("Results are written to BenchmarkDotNet.Artifacts, including a GitHub flavoured markdown report.");
            Console.WriteLine();

            BenchmarkRunner.Run<ListSerializerBenchmarks>(config: null, args: args);
        }
    }
}
