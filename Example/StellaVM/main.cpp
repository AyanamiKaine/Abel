import vm;

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <expected>
#include <print>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace
{
using stella::vm::Error;
using stella::vm::ErrorCode;

auto make_unexpected(const ErrorCode code, const std::string_view message) -> std::unexpected<Error>
{
    return std::unexpected<Error> {Error {code, std::string(message)}};
}

auto error_code_name(const ErrorCode code) -> const char*
{
    switch (code)
    {
        case ErrorCode::type_mismatch:
            return "type_mismatch";
        case ErrorCode::invalid_buffer_access:
            return "invalid_buffer_access";
        case ErrorCode::invalid_constant_index:
            return "invalid_constant_index";
        case ErrorCode::invalid_input_index:
            return "invalid_input_index";
        case ErrorCode::stack_underflow:
            return "stack_underflow";
        case ErrorCode::invalid_native_index:
            return "invalid_native_index";
        case ErrorCode::empty_native_binding:
            return "empty_native_binding";
        case ErrorCode::insufficient_native_arguments:
            return "insufficient_native_arguments";
        case ErrorCode::unknown_opcode:
            return "unknown_opcode";
        case ErrorCode::division_by_zero:
            return "division_by_zero";
        case ErrorCode::invalid_jump_target:
            return "invalid_jump_target";
        case ErrorCode::verification_failed:
            return "verification_failed";
        case ErrorCode::invalid_function_index:
            return "invalid_function_index";
        case ErrorCode::invalid_local_index:
            return "invalid_local_index";
        case ErrorCode::missing_call_frame:
            return "missing_call_frame";
        case ErrorCode::step_budget_exceeded:
            return "step_budget_exceeded";
        case ErrorCode::invalid_function_signature:
            return "invalid_function_signature";
        case ErrorCode::invalid_shift_amount:
            return "invalid_shift_amount";
        case ErrorCode::invalid_bytecode_magic:
            return "invalid_bytecode_magic";
        case ErrorCode::unsupported_bytecode_version:
            return "unsupported_bytecode_version";
        case ErrorCode::malformed_bytecode:
            return "malformed_bytecode";
        case ErrorCode::arithmetic_overflow:
            return "arithmetic_overflow";
        case ErrorCode::native_reentrancy:
            return "native_reentrancy";
        case ErrorCode::bytecode_limit_exceeded:
            return "bytecode_limit_exceeded";
    }
    return "unknown";
}

auto sample_input(const std::uint64_t i) -> std::int64_t
{
    std::uint64_t x = i + 0x9E3779B97F4A7C15ULL;
    x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ULL;
    x = (x ^ (x >> 27)) * 0x94D049BB133111EBULL;
    x = x ^ (x >> 31);
    return static_cast<std::int64_t>(x & 0x7FFF);
}

struct BenchmarkStats final
{
    std::string_view name {};
    std::uint64_t warmup_iterations = 0;
    std::uint64_t measured_iterations = 0;
    double elapsed_seconds = 0.0;
    double runs_per_second = 0.0;
    double nanos_per_run = 0.0;
    std::uint64_t checksum = 0;
};

template <typename IterationFn>
auto run_case(
    const std::string_view name,
    const std::uint64_t warmup_iterations,
    const std::uint64_t measured_iterations,
    IterationFn&& run_iteration) -> stella::vm::Result<BenchmarkStats>
{
    using namespace stella::vm;
    using Clock = std::chrono::steady_clock;

    std::println("\n[{}]", name);
    std::println("Warmup iterations: {}", warmup_iterations);
    std::println("Measured iterations: {}", measured_iterations);

    for (std::uint64_t i = 0; i < warmup_iterations; ++i)
    {
        Result<std::uint64_t> result = run_iteration(i);
        if (!result.has_value())
        {
            return std::unexpected(result.error());
        }
    }

    std::uint64_t checksum = 0;
    const auto started = Clock::now();

    for (std::uint64_t i = 0; i < measured_iterations; ++i)
    {
        Result<std::uint64_t> result = run_iteration(i + warmup_iterations);
        if (!result.has_value())
        {
            return std::unexpected(result.error());
        }
        checksum += result.value();
    }

    const auto finished = Clock::now();
    const double elapsed_seconds = std::chrono::duration<double>(finished - started).count();
    if (elapsed_seconds <= 0.0)
    {
        return make_unexpected(ErrorCode::unknown_opcode, "Benchmark timer reported non-positive elapsed time.");
    }
    const double runs_per_second = static_cast<double>(measured_iterations) / elapsed_seconds;
    const double nanos_per_run = (elapsed_seconds * 1'000'000'000.0) / static_cast<double>(measured_iterations);

    std::println("Elapsed: {:.6f} s", elapsed_seconds);
    std::println("Throughput: {:.2f} runs/s", runs_per_second);
    std::println("Latency: {:.2f} ns/run", nanos_per_run);
    std::println("Checksum: {}", checksum);

    return BenchmarkStats {
        .name = name,
        .warmup_iterations = warmup_iterations,
        .measured_iterations = measured_iterations,
        .elapsed_seconds = elapsed_seconds,
        .runs_per_second = runs_per_second,
        .nanos_per_run = nanos_per_run,
        .checksum = checksum,
    };
}

auto run_arith_heavy_case() -> stella::vm::Result<BenchmarkStats>
{
    using namespace stella::vm;

    VM vm;
    Program program;
    const std::uint32_t c0 = static_cast<std::uint32_t>(program.add_constant(Value::i64(5)));
    const std::uint32_t c1 = static_cast<std::uint32_t>(program.add_constant(Value::i64(11)));
    const std::uint32_t c2 = static_cast<std::uint32_t>(program.add_constant(Value::i64(17)));
    const std::uint32_t c3 = static_cast<std::uint32_t>(program.add_constant(Value::i64(23)));
    const std::uint32_t c4 = static_cast<std::uint32_t>(program.add_constant(Value::i64(29)));
    const std::uint32_t c5 = static_cast<std::uint32_t>(program.add_constant(Value::i64(31)));
    const std::uint32_t c6 = static_cast<std::uint32_t>(program.add_constant(Value::i64(37)));
    const std::uint32_t c7 = static_cast<std::uint32_t>(program.add_constant(Value::i64(41)));

    program.code = {
        {OpCode::push_input, 0},
        {OpCode::push_constant, c0},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, c1},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, c2},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, c3},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, c4},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, c5},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, c6},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, c7},
        {OpCode::add_i64, 0},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 1);
    if (!verify.has_value())
    {
        return std::unexpected(verify.error());
    }

    return run_case(
        "Arith Heavy (pricing accumulator)",
        100'000,
        4'000'000,
        [&](const std::uint64_t iteration) -> Result<std::uint64_t> {
            vm.clear_inputs();
            const auto index = static_cast<std::uint32_t>(vm.push_input(Value::i64(sample_input(iteration))));
            if (index != 0)
            {
                return make_unexpected(
                    ErrorCode::invalid_input_index,
                    "Expected input slot 0 after clear_inputs.");
            }

            Result<Value> result = vm.run_unchecked(program);
            if (!result.has_value())
            {
                return std::unexpected(result.error());
            }
            if (!result->is_i64())
            {
                return make_unexpected(ErrorCode::type_mismatch, "Arith case returned non-i64.");
            }

            return static_cast<std::uint64_t>(result->as_i64());
        });
}

auto run_native_heavy_case() -> stella::vm::Result<BenchmarkStats>
{
    using namespace stella::vm;

    VM vm;

    const auto native_scale = static_cast<std::uint32_t>(vm.native("scale").bind([](std::int64_t value) {
        return (value * 5) + 13;
    }));

    const auto native_mix = static_cast<std::uint32_t>(vm.native("mix").bind([](std::int64_t lhs, std::int64_t rhs) {
        return (lhs * 3) + (rhs * 7) + ((lhs ^ rhs) & 31);
    }));

    const auto native_clamp = static_cast<std::uint32_t>(vm.native("clamp").bind([](std::int64_t value) {
        if (value < 0)
        {
            value = -value;
        }
        if (value > 1'000'000)
        {
            value = 1'000'000 + (value % 17);
        }
        return value;
    }));

    Program program;
    const auto c0 = static_cast<std::uint32_t>(program.add_constant(Value::i64(97)));
    const auto c1 = static_cast<std::uint32_t>(program.add_constant(Value::i64(211)));
    const auto c2 = static_cast<std::uint32_t>(program.add_constant(Value::i64(503)));

    program.code = {
        {OpCode::push_input, 0},
        {OpCode::call_native, native_scale},
        {OpCode::push_constant, c0},
        {OpCode::call_native, native_mix},
        {OpCode::push_constant, c1},
        {OpCode::call_native, native_mix},
        {OpCode::call_native, native_clamp},
        {OpCode::push_constant, c2},
        {OpCode::call_native, native_mix},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 1);
    if (!verify.has_value())
    {
        return std::unexpected(verify.error());
    }

    return run_case(
        "Native Call Heavy (rule chain)",
        100'000,
        3'000'000,
        [&](const std::uint64_t iteration) -> Result<std::uint64_t> {
            vm.clear_inputs();
            const auto index = static_cast<std::uint32_t>(vm.push_input(Value::i64(sample_input(iteration))));
            if (index != 0)
            {
                return make_unexpected(
                    ErrorCode::invalid_input_index,
                    "Expected input slot 0 after clear_inputs.");
            }

            Result<Value> result = vm.run_unchecked(program);
            if (!result.has_value())
            {
                return std::unexpected(result.error());
            }
            if (!result->is_i64())
            {
                return make_unexpected(ErrorCode::type_mismatch, "Native-heavy case returned non-i64.");
            }

            return static_cast<std::uint64_t>(result->as_i64());
        });
}

auto run_buffer_heavy_case() -> stella::vm::Result<BenchmarkStats>
{
    using namespace stella::vm;

    VM vm;

    const auto native_transform = static_cast<std::uint32_t>(vm.native("packet_transform").bind([](MoveBuffer buffer) {
        auto bytes = buffer.bytes();
        for (std::size_t i = 0; i < bytes.size(); ++i)
        {
            std::uint8_t value = std::to_integer<std::uint8_t>(bytes[i]);
            value = static_cast<std::uint8_t>((value + static_cast<std::uint8_t>(i)) ^ 0x5A);
            if ((i & 1U) == 0U)
            {
                value = static_cast<std::uint8_t>(value ^ (value << 1));
            }
            else
            {
                value = static_cast<std::uint8_t>(value + ((value >> 3) | 1U));
            }
            bytes[i] = std::byte {value};
        }

        return buffer;
    }));

    const auto native_hash = static_cast<std::uint32_t>(vm.native("packet_hash").bind([](MoveBuffer buffer) {
        auto bytes = buffer.bytes();
        std::uint64_t hash = 1469598103934665603ULL;
        for (const std::byte byte : bytes)
        {
            hash ^= std::to_integer<std::uint8_t>(byte);
            hash *= 1099511628211ULL;
        }

        return static_cast<std::int64_t>(hash & 0x7FFF'FFFF'FFFF'FFFFULL);
    }));

    Program program;
    program.code = {
        {OpCode::push_input, 0},
        {OpCode::call_native, native_transform},
        {OpCode::call_native, native_hash},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 1);
    if (!verify.has_value())
    {
        return std::unexpected(verify.error());
    }

    return run_case(
        "Buffer Heavy (packet transform/hash)",
        10'000,
        200'000,
        [&](const std::uint64_t iteration) -> Result<std::uint64_t> {
            constexpr std::size_t payload_size = 512;

            MoveBuffer payload(payload_size);
            auto bytes = payload.bytes();
            const std::int64_t seed = sample_input(iteration);
            for (std::size_t i = 0; i < bytes.size(); ++i)
            {
                const auto value = static_cast<std::uint8_t>((seed + static_cast<std::int64_t>(i * 13U)) & 0xFF);
                bytes[i] = std::byte {value};
            }

            vm.clear_inputs();
            const auto index = static_cast<std::uint32_t>(vm.push_input(Value::owned_buffer(std::move(payload))));
            if (index != 0)
            {
                return make_unexpected(
                    ErrorCode::invalid_input_index,
                    "Expected input slot 0 after clear_inputs.");
            }

            Result<Value> result = vm.run_unchecked(program);
            if (!result.has_value())
            {
                return std::unexpected(result.error());
            }
            if (!result->is_i64())
            {
                return make_unexpected(ErrorCode::type_mismatch, "Buffer-heavy case returned non-i64.");
            }

            return static_cast<std::uint64_t>(result->as_i64());
        });
}

auto run_branchy_case() -> stella::vm::Result<BenchmarkStats>
{
    using namespace stella::vm;

    VM vm;

    Program program;
    const auto mod_base = static_cast<std::uint32_t>(program.add_constant(Value::i64(11)));
    const auto low_cut = static_cast<std::uint32_t>(program.add_constant(Value::i64(3)));
    const auto mid_cut = static_cast<std::uint32_t>(program.add_constant(Value::i64(7)));
    const auto low_mul = static_cast<std::uint32_t>(program.add_constant(Value::i64(2)));
    const auto low_add = static_cast<std::uint32_t>(program.add_constant(Value::i64(80)));
    const auto mid_mul = static_cast<std::uint32_t>(program.add_constant(Value::i64(5)));
    const auto mid_add = static_cast<std::uint32_t>(program.add_constant(Value::i64(40)));
    const auto high_mul = static_cast<std::uint32_t>(program.add_constant(Value::i64(9)));
    const auto high_sub = static_cast<std::uint32_t>(program.add_constant(Value::i64(15)));
    const auto bias = static_cast<std::uint32_t>(program.add_constant(Value::i64(19)));
    const auto xor_salt = static_cast<std::uint32_t>(program.add_constant(Value::i64(3)));
    const auto mask = static_cast<std::uint32_t>(program.add_constant(Value::i64(15)));

    program.code = {
        {OpCode::push_input, 0},
        {OpCode::push_constant, mod_base},
        {OpCode::mod_i64, 0},
        {OpCode::push_constant, xor_salt},
        {OpCode::xor_i64, 0},
        {OpCode::push_constant, mask},
        {OpCode::and_i64, 0},
        {OpCode::dup, 0},
        {OpCode::push_constant, low_cut},
        {OpCode::cmp_lt_i64, 0},
        {OpCode::jump_if_true, 20},
        {OpCode::dup, 0},
        {OpCode::push_constant, mid_cut},
        {OpCode::cmp_lt_i64, 0},
        {OpCode::jump_if_true, 25},
        {OpCode::push_constant, high_mul},
        {OpCode::mul_i64, 0},
        {OpCode::push_constant, high_sub},
        {OpCode::sub_i64, 0},
        {OpCode::jump, 29},
        {OpCode::push_constant, low_mul},
        {OpCode::mul_i64, 0},
        {OpCode::push_constant, low_add},
        {OpCode::add_i64, 0},
        {OpCode::jump, 29},
        {OpCode::push_constant, mid_mul},
        {OpCode::mul_i64, 0},
        {OpCode::push_constant, mid_add},
        {OpCode::add_i64, 0},
        {OpCode::push_constant, bias},
        {OpCode::add_i64, 0},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 1);
    if (!verify.has_value())
    {
        return std::unexpected(verify.error());
    }

    return run_case(
        "Branchy (policy routing bytecode)",
        100'000,
        2'500'000,
        [&](const std::uint64_t iteration) -> Result<std::uint64_t> {
            const std::int64_t input =
                sample_input(iteration) ^ static_cast<std::int64_t>((iteration * 1103515245ULL) & 0x7FFF'FFFFULL);

            vm.clear_inputs();
            const auto index = static_cast<std::uint32_t>(vm.push_input(Value::i64(input)));
            if (index != 0)
            {
                return make_unexpected(
                    ErrorCode::invalid_input_index,
                    "Expected input slot 0 after clear_inputs.");
            }

            Result<Value> result = vm.run_unchecked(program);
            if (!result.has_value())
            {
                return std::unexpected(result.error());
            }
            if (!result->is_i64())
            {
                return make_unexpected(ErrorCode::type_mismatch, "Branchy case returned non-i64.");
            }

            return static_cast<std::uint64_t>(result->as_i64());
        });
}

auto run_benchmark_suite() -> stella::vm::VoidResult
{
    using namespace stella::vm;

    std::vector<BenchmarkStats> all_stats;
    all_stats.reserve(4);

    Result<BenchmarkStats> arith = run_arith_heavy_case();
    if (!arith.has_value())
    {
        return std::unexpected(arith.error());
    }
    all_stats.push_back(arith.value());

    Result<BenchmarkStats> native_heavy = run_native_heavy_case();
    if (!native_heavy.has_value())
    {
        return std::unexpected(native_heavy.error());
    }
    all_stats.push_back(native_heavy.value());

    Result<BenchmarkStats> buffer_heavy = run_buffer_heavy_case();
    if (!buffer_heavy.has_value())
    {
        return std::unexpected(buffer_heavy.error());
    }
    all_stats.push_back(buffer_heavy.value());

    Result<BenchmarkStats> branchy = run_branchy_case();
    if (!branchy.has_value())
    {
        return std::unexpected(branchy.error());
    }
    all_stats.push_back(branchy.value());

    const double baseline = all_stats.front().runs_per_second;

    std::println("\n=== Benchmark Summary ===");
    std::println(
        "{:<36} {:>12} {:>12} {:>12} {:>10}",
        "Case",
        "Iterations",
        "M runs/s",
        "ns/run",
        "Rel");

    for (const BenchmarkStats& stats : all_stats)
    {
        const double million_runs = stats.runs_per_second / 1'000'000.0;
        const double relative = stats.runs_per_second / baseline;
        std::println(
            "{:<36} {:>12} {:>12.2f} {:>12.2f} {:>9.2f}x",
            stats.name,
            stats.measured_iterations,
            million_runs,
            stats.nanos_per_run,
            relative);
    }

    std::println("\nChecksums");
    for (const BenchmarkStats& stats : all_stats)
    {
        std::println("{:<36} {}", stats.name, stats.checksum);
    }

    return {};
}
} // namespace

auto main() -> int
{
    const auto result = run_benchmark_suite();
    if (!result.has_value())
    {
        const auto& error = result.error();
        std::println("VM benchmark suite failed [{}]: {}", error_code_name(error.code), error.message);
        return 1;
    }

    return 0;
}
