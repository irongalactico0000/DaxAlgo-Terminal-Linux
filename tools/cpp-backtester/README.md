# TickBacktester core

A reusable C++20 tick-backtesting foundation for quantitative research. The macOS product does not
ship a native strategy implementation or a strategy-specific runner; consumers provide their own
`bt::Strategy` implementation when integrating the library.

## Included components

- Priority-queue event engine and cache-friendly market-data structures
- Order management, portfolio accounting, and execution simulation
- CSV and Apache Parquet loaders
- Performance metrics
- A strategy interface for consumer-owned implementations
- A strategy-neutral performance benchmark and core unit tests
- A market-data generation utility

## Build

### Prerequisites

On macOS with Homebrew:

```bash
brew install cmake ninja eigen fmt spdlog apache-arrow
```

Equivalent Eigen, fmt, spdlog, Arrow, and Parquet packages are required on other platforms.

### Compile and test

```bash
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build --parallel
ctest --test-dir build --output-on-failure
```

Run the strategy-neutral performance benchmark with `./build/benchmark`. Generate sample market
data with `./build/data_fetcher --source generate --symbol NIFTY --ticks 1000000 --out nifty.csv`.

## Data format

CSV input uses the following columns:

```text
timestamp,bid,ask,last,volume
1700000001000000000,19499.50,19500.50,19500.00,1523
```

Timestamps are nanoseconds since the Unix epoch. Parquet uses the corresponding columnar schema.

## Integrating a strategy

Implement the abstract interface in `strategy/strategy.h`, construct the engine components required
by the host, and register an adapter with the event engine. Strategy source remains consumer-owned
and is not bundled with this project.
