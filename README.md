# Custom list serializer

Implementation of `IListSerializer` for a doubly linked list with optional `Random` links,
plus unit tests and BenchmarkDotNet measurements.

The task, verbatim, is kept in [`SaberInteractiveTest/serializer.md`](SaberInteractiveTest/serializer.md).

## Layout

| Path | Purpose |
|---|---|
| [`SaberInteractiveTest/IListSerializer.cs`](SaberInteractiveTest/IListSerializer.cs) | Given interface, unchanged |
| [`SaberInteractiveTest/ListNode.cs`](SaberInteractiveTest/ListNode.cs) | Given node, unchanged |
| [`SaberInteractiveTest/IvanImplementation.cs`](SaberInteractiveTest/IvanImplementation.cs) | The implementation |
| [`SaberInteractiveTest.Tests/ListSerializerTests.cs`](SaberInteractiveTest.Tests/ListSerializerTests.cs) | The original test suite |
| [`SaberInteractiveTest.Tests/ListSerializerRobustnessTests.cs`](SaberInteractiveTest.Tests/ListSerializerRobustnessTests.cs) | Truncated input, arbitrary node, source immutability |
| [`BenchmarkRunner/`](BenchmarkRunner) | BenchmarkDotNet runner |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | Build, test, and compile the benchmarks without running them |

All projects target `net9.0`. There is no solution file, so commands below name the project explicitly.

## Wire format

Little endian, self contained, no magic number and no version field.

```
nodeCount : int32                                  ( >= 1, present only for a non-null list )
repeated nodeCount times, in list order:
    dataLength   : int32                          ( -1 = null string, otherwise UTF-8 byte count )
    data         : byte[dataLength]               ( UTF-8 payload )
    randomIndex  : int32                          ( -1 = null link, otherwise 0 <= index < nodeCount )
```

- A `null` list is written as **zero bytes**, so an empty stream always deserializes to `null`.
  `nodeCount == 0` is accepted and also yields `null`.
- The list is always written from its head, therefore **any node may be passed in** to `Serialize` and to
  `DeepCopy`. Both walk `Previous` up to the head before doing anything else.
- `Previous` is not stored, it is rebuilt on the way in. This keeps the fixed part of a node at 8 bytes
  instead of 12.
- `Random` is stored as an index, so self references and forward references cost the same as any other link.
- Bytes following the last node are **ignored**, they are not an error.

## Behaviour on bad input

| Situation | Result |
|---|---|
| `Serialize(null, stream)` | nothing is written, stream stays empty |
| `Serialize(head, null)`, `Deserialize(null)` | `ArgumentNullException` |
| Stream truncated anywhere: header, string body, random index, missing nodes | `ArgumentException` |
| `nodeCount < 0`, `dataLength < -1` | `ArgumentException` |
| `randomIndex < -1` or `randomIndex >= nodeCount` | `ArgumentException` |
| `Random` points to a node outside the list, in `Serialize` or `DeepCopy` | `ArgumentException` |
| A `Next` chain that loops back onto itself | `ArgumentException` |

For a seekable stream the truncation check is done up front from `Length`, so a bogus `nodeCount`
cannot trigger a large allocation before the data is read. For a non seekable stream the same
condition is detected by the forward only reader. Both paths are covered by tests.

`DeepCopy` never writes to the source nodes. The old implementation interleaved the two lists and
restored the originals afterwards, which left the source list in a half rewritten state for the whole
duration of the call. The copy is now built through a `Dictionary` of original to clone, and the tests
assert a field by field fingerprint of the source before and after.

## Running

```bash
# tests
dotnet test SaberInteractiveTest.Tests/SaberInteractiveTest.Tests.csproj

# tests inside docker
docker build -t saber-list-serializer .
docker run --rm saber-list-serializer

# benchmarks, results land in BenchmarkDotNet.Artifacts
dotnet run -c Release --project BenchmarkRunner/BenchmarkRunner.csproj
```

## Benchmarks

1,000,000 nodes, `Data` of roughly 8 characters, about 70 percent of nodes carrying a `Random` link.
Measured with BenchmarkDotNet 0.15.8, .NET 9, Release build, `IterationCount=10`, `WarmupCount=3`.

| Method | Before, mean | Before, allocated | After, mean | After, allocated |
|---|---:|---:|---:|---:|
| `Serialize` | 467.9 ms | 95.05 MB | _pending_ | _pending_ |
| `Deserialize` | 551.0 ms | 106.79 MB | _pending_ | _pending_ |
| `DeepCopy` | 263.6 ms | 45.78 MB | _pending_ | _pending_ |

The **before** column is the previous implementation, measured on Linux Ubuntu 24.04, AMD Ryzen 7 2700.
The **after** column is produced by the current code, and is intentionally left unfilled until the run
has been executed on the machine that will also run the tests, so that both columns come from comparable
hardware. Fill it from the generated
`BenchmarkDotNet.Artifacts/results/SerializerTests.Benchmarks.ListSerializerBenchmarks-report-github.md`.

What changed in the implementation, in the order that matters for these numbers:

- `Serialize` writes through a 256 KB pooled buffer, so the number of `WriteAsync` calls is proportional
  to payload size instead of node count. The previous version rented, wrote and returned a buffer per node.
- `Deserialize` reads through a pooled forward only reader and decodes strings directly from that buffer,
  instead of renting a buffer per string. It also pre-validates seekable streams against `Length`.
- `DeepCopy` drops the three pass pointer juggling for a single dictionary, which trades some memory for
  clarity and for the guarantee that the source is never touched.

CI **builds** `BenchmarkRunner` in Release but never executes it. The measurements are machine dependent,
so a time or allocation threshold in CI would only produce flaky failures. The compile step still fails on
a broken `BenchmarkRunner` or a changed serializer signature, which is the part that can be verified
without hardware. Nothing in this repository gates on `GC.GetAllocatedBytesForCurrentThread` around the
async calls.

## Housekeeping

`.gitignore` covers `bin`, `obj`, `BenchmarkDotNet.Artifacts`, `TestResults` and coverage output. The
artifacts folder that existed before this cleanup is still tracked in git history, so if a clean index is
wanted, drop it with:

```bash
git rm -r --cached BenchmarkDotNet.Artifacts
```
