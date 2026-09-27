# syntax=docker/dockerfile:1
#
# Multi stage image. The LAST stage is the default target, so
#   docker build -t saber-list-serializer .
#   docker run --rm saber-list-serializer
# still builds and runs the test suite, exactly as before.
#
# The benchmark project is kept in a separate stage pair so that building the
# test image does not have to restore BenchmarkDotNet.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS base
WORKDIR /app
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# ---------- benchmarks ----------
# Kept before the tests stage so that `tests` stays the last stage, and the last stage
# is the one a plain `docker build` targets.

FROM base AS benchmarks-build
COPY . ./
RUN dotnet restore BenchmarkRunner/BenchmarkRunner.csproj
RUN dotnet build BenchmarkRunner/BenchmarkRunner.csproj -c Release --no-restore

# Arguments after `--` are forwarded to BenchmarkRunner.Run, so that
#   docker run --rm saber-benchmarks -- --job dry --filter '*'
# reaches BenchmarkDotNet.
FROM benchmarks-build AS benchmarks
ENTRYPOINT ["dotnet", "BenchmarkRunner/bin/Release/net9.0/BenchmarkRunner.dll"]

# ---------- tests ----------

FROM base AS tests-build
COPY . ./
RUN dotnet restore SaberInteractiveTest.Tests/SaberInteractiveTest.Tests.csproj
RUN dotnet build SaberInteractiveTest.Tests/SaberInteractiveTest.Tests.csproj -c Release --no-restore

FROM tests-build AS tests
# Kept on a single line on purpose: a backslash continuation inside a JSON array
# is not worth the build time risk for a cosmetic wrap.
CMD ["dotnet", "test", "SaberInteractiveTest.Tests/SaberInteractiveTest.Tests.csproj", "-c", "Release", "--no-build", "--logger", "console;verbosity=normal"]
