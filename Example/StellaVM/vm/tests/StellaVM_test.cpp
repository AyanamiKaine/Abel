#include <doctest/doctest.h>

import vm;

#include <cstddef>
#include <span>
#include <cstdint>
#include <string>
#include <vector>
#include <utility>

namespace
{
struct DestructionProbe final
{
    explicit DestructionProbe(int* counter_ptr)
        : counter(counter_ptr)
    {
    }

    ~DestructionProbe()
    {
        ++(*counter);
    }

    int* counter = nullptr;
};
}

TEST_CASE("bytecode VM executes add_i64")
{
    using namespace stella::vm;

    Program program;
    const auto lhs = static_cast<std::uint32_t>(program.add_constant(Value::i64(40)));
    const auto rhs = static_cast<std::uint32_t>(program.add_constant(Value::i64(2)));

    program.code = {
        {OpCode::push_constant, lhs},
        {OpCode::push_constant, rhs},
        {OpCode::add_i64, 0},
        {OpCode::halt, 0}};

    VM vm;
    Result<Value> result = vm.run(program);

    REQUIRE(result.has_value());
    CHECK(result->is_i64());
    CHECK(result->as_i64() == 42);
}

TEST_CASE("bytecode VM can call native C++ function")
{
    using namespace stella::vm;

    VM vm;
    const auto native_sum3 =
        static_cast<std::uint32_t>(vm.bind_native("sum3", 3, [](VM&, std::span<Value> args) -> Result<Value> {
            return Value::i64(args[0].as_i64() + args[1].as_i64() + args[2].as_i64());
        }));

    Program program;
    const auto a = static_cast<std::uint32_t>(program.add_constant(Value::i64(10)));
    const auto b = static_cast<std::uint32_t>(program.add_constant(Value::i64(20)));
    const auto c = static_cast<std::uint32_t>(program.add_constant(Value::i64(12)));

    program.code = {
        {OpCode::push_constant, a},
        {OpCode::push_constant, b},
        {OpCode::push_constant, c},
        {OpCode::call_native, native_sum3},
        {OpCode::halt, 0}};

    Result<Value> result = vm.run(program);

    REQUIRE(result.has_value());
    CHECK(result->is_i64());
    CHECK(result->as_i64() == 42);
}

TEST_CASE("native binding builder binds typed lambdas with trivial API")
{
    using namespace stella::vm;

    VM vm;
    const auto native_sum = static_cast<std::uint32_t>(vm.native("sum2").bind([](std::int64_t a, std::int64_t b) {
        return a + b;
    }));

    Program program;
    const auto lhs = static_cast<std::uint32_t>(program.add_constant(Value::i64(20)));
    const auto rhs = static_cast<std::uint32_t>(program.add_constant(Value::i64(22)));
    program.code = {
        {OpCode::push_constant, lhs},
        {OpCode::push_constant, rhs},
        {OpCode::call_native, native_sum},
        {OpCode::halt, 0},
    };

    Result<Value> result = vm.run(program);
    REQUIRE(result.has_value());
    REQUIRE(result->is_i64());
    CHECK(result->as_i64() == 42);
}

TEST_CASE("native binding builder supports VM-aware callbacks")
{
    using namespace stella::vm;

    VM vm;
    const auto native_stack_size = static_cast<std::uint32_t>(vm.native("stack_size").bind([](VM& host_vm) {
        return static_cast<std::int64_t>(host_vm.stack().size());
    }));

    Program program;
    const auto c0 = static_cast<std::uint32_t>(program.add_constant(Value::i64(5)));
    const auto c1 = static_cast<std::uint32_t>(program.add_constant(Value::i64(7)));
    program.code = {
        {OpCode::push_constant, c0},
        {OpCode::push_constant, c1},
        {OpCode::call_native, native_stack_size},
        {OpCode::halt, 0},
    };

    Result<Value> result = vm.run(program);
    REQUIRE(result.has_value());
    REQUIRE(result->is_i64());
    CHECK(result->as_i64() == 2);
}

TEST_CASE("native binding builder decodes const reference parameters")
{
    using namespace stella::vm;

    VM vm;
    const auto native_strlen =
        static_cast<std::uint32_t>(vm.native("strlen").bind([](const std::string& text) {
            return static_cast<std::int64_t>(text.size());
        }));

    Program program;
    const auto text = static_cast<std::uint32_t>(program.add_constant(Value::owned_string("stella")));
    program.code = {
        {OpCode::push_constant, text},
        {OpCode::call_native, native_strlen},
        {OpCode::halt, 0},
    };

    Result<Value> result = vm.run(program);
    REQUIRE(result.has_value());
    REQUIRE(result->is_i64());
    CHECK(result->as_i64() == 6);
}

TEST_CASE("native binding builder reports explicit arity mismatch")
{
    using namespace stella::vm;

    VM vm;
    const auto native_bad = static_cast<std::uint32_t>(vm.native("bad").arity(2).bind([](std::int64_t value) {
        return value;
    }));

    Program program;
    const auto c0 = static_cast<std::uint32_t>(program.add_constant(Value::i64(7)));
    const auto c1 = static_cast<std::uint32_t>(program.add_constant(Value::i64(9)));
    program.code = {
        {OpCode::push_constant, c0},
        {OpCode::push_constant, c1},
        {OpCode::call_native, native_bad},
        {OpCode::halt, 0},
    };

    Result<Value> result = vm.run(program);
    REQUIRE(!result.has_value());
    CHECK(result.error().code == ErrorCode::invalid_function_signature);
}

TEST_CASE("native binding builder forwards move-only buffer arguments")
{
    using namespace stella::vm;

    VM vm;
    const auto native_identity =
        static_cast<std::uint32_t>(vm.native("identity").bind([](MoveBuffer buffer) {
            return buffer;
        }));

    MoveBuffer payload(4);
    payload.bytes()[0] = std::byte {0x12};
    payload.bytes()[1] = std::byte {0x34};
    payload.bytes()[2] = std::byte {0x56};
    payload.bytes()[3] = std::byte {0x78};
    auto* original_ptr = payload.data_ptr();

    Program program;
    const auto input_index = static_cast<std::uint32_t>(vm.push_input(Value::owned_buffer(std::move(payload))));
    program.code = {
        {OpCode::push_input, input_index},
        {OpCode::call_native, native_identity},
        {OpCode::halt, 0},
    };

    Result<Value> result = vm.run(program);
    REQUIRE(result.has_value());
    REQUIRE(result->is_buffer());

    auto moved = result->take_buffer();
    REQUIRE(moved.has_value());
    CHECK(moved->data_ptr() == original_ptr);
    CHECK(moved->size == 4);
    CHECK(std::to_integer<int>(moved->bytes()[0]) == 0x12);
    CHECK(std::to_integer<int>(moved->bytes()[1]) == 0x34);
    CHECK(std::to_integer<int>(moved->bytes()[2]) == 0x56);
    CHECK(std::to_integer<int>(moved->bytes()[3]) == 0x78);
}

TEST_CASE("move-only payload crosses host, VM, and native call without copying bytes")
{
    using namespace stella::vm;

    VM vm;
    MoveBuffer payload(8);
    REQUIRE(payload.data_ptr() != nullptr);
    auto* original_ptr = payload.data_ptr();
    payload.bytes()[0] = std::byte {0x2A};

    const auto native_identity = static_cast<std::uint32_t>(
        vm.bind_native("identity_buffer", 1, [](VM&, std::span<Value> args) -> Result<Value> {
            CHECK(args[0].is_buffer());
            Result<MoveBuffer> buffer_result = args[0].take_buffer();
            REQUIRE(buffer_result.has_value());
            MoveBuffer buffer = std::move(buffer_result).value();
            buffer.bytes()[1] = std::byte {0x55};
            return Value::owned_buffer(std::move(buffer));
        }));

    const auto input_index = static_cast<std::uint32_t>(vm.push_input(Value::owned_buffer(std::move(payload))));

    Program program;
    program.code = {
        {OpCode::push_input, input_index},
        {OpCode::call_native, native_identity},
        {OpCode::halt, 0}};

    Result<Value> result = vm.run(program);
    REQUIRE(result.has_value());

    CHECK(result->is_buffer());
    Result<MoveBuffer> returned_result = result->take_buffer();
    REQUIRE(returned_result.has_value());
    MoveBuffer returned = std::move(returned_result).value();
    CHECK(returned.size == 8);
    CHECK(returned.data_ptr() == original_ptr);
    CHECK(std::to_integer<int>(returned.bytes()[0]) == 0x2A);
    CHECK(std::to_integer<int>(returned.bytes()[1]) == 0x55);
}

TEST_CASE("arena marker provides RAII rewind for temporary VM allocations")
{
    using namespace stella::vm;

    int destroyed = 0;
    Arena arena(256);

    {
        auto marker = arena.mark();
        arena.emplace<DestructionProbe>(&destroyed);
        CHECK(arena.live_allocations() == 1);
        CHECK(destroyed == 0);
    }

    CHECK(destroyed == 1);
    CHECK(arena.live_allocations() == 0);

    arena.emplace<DestructionProbe>(&destroyed);
    CHECK(arena.live_allocations() == 1);
    arena.reset();
    CHECK(destroyed == 2);
    CHECK(arena.live_allocations() == 0);
}

TEST_CASE("bytecode VM executes branch and arithmetic opcodes")
{
    using namespace stella::vm;

    VM vm;
    Program program;

    const auto mod_base = static_cast<std::uint32_t>(program.add_constant(Value::i64(7)));
    const auto threshold = static_cast<std::uint32_t>(program.add_constant(Value::i64(3)));
    const auto false_mul = static_cast<std::uint32_t>(program.add_constant(Value::i64(5)));
    const auto false_add = static_cast<std::uint32_t>(program.add_constant(Value::i64(100)));
    const auto true_mul = static_cast<std::uint32_t>(program.add_constant(Value::i64(3)));
    const auto true_add = static_cast<std::uint32_t>(program.add_constant(Value::i64(17)));

    program.code = {
        {OpCode::push_input, 0},
        {OpCode::dup, 0},
        {OpCode::push_constant, mod_base},
        {OpCode::mod_i64, 0},
        {OpCode::push_constant, threshold},
        {OpCode::cmp_lt_i64, 0},
        {OpCode::jump_if_true, 12},
        {OpCode::push_constant, false_mul},
        {OpCode::mul_i64, 0},
        {OpCode::push_constant, false_add},
        {OpCode::add_i64, 0},
        {OpCode::jump, 16},
        {OpCode::push_constant, true_mul},
        {OpCode::mul_i64, 0},
        {OpCode::push_constant, true_add},
        {OpCode::add_i64, 0},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 1);
    REQUIRE(verify.has_value());

    vm.clear_inputs();
    const auto first_input = static_cast<std::uint32_t>(vm.push_input(Value::i64(10)));
    REQUIRE(first_input == 0);
    Result<Value> first_result = vm.run_unchecked(program);
    REQUIRE_MESSAGE(first_result.has_value(), first_result.error().message);
    CHECK(first_result->is_i64());
    CHECK(first_result->as_i64() == 150);

    vm.clear_inputs();
    const auto second_input = static_cast<std::uint32_t>(vm.push_input(Value::i64(9)));
    REQUIRE(second_input == 0);
    Result<Value> second_result = vm.run_unchecked(program);
    REQUIRE_MESSAGE(second_result.has_value(), second_result.error().message);
    CHECK(second_result->is_i64());
    CHECK(second_result->as_i64() == 44);
}

TEST_CASE("verifier rejects invalid jump target")
{
    using namespace stella::vm;

    VM vm;
    Program program;
    program.code = {
        {OpCode::jump, 99},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 0);
    REQUIRE(!verify.has_value());
    CHECK(verify.error().code == ErrorCode::invalid_jump_target);
}

TEST_CASE("verifier rejects inconsistent stack depth at merge")
{
    using namespace stella::vm;

    VM vm;
    Program program;
    const auto one = static_cast<std::uint32_t>(program.add_constant(Value::i64(1)));

    program.code = {
        {OpCode::push_constant, one},
        {OpCode::jump_if_true, 3},
        {OpCode::push_constant, one},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 0);
    REQUIRE(!verify.has_value());
    CHECK(verify.error().code == ErrorCode::verification_failed);
}

TEST_CASE("bytecode VM supports function call frames and locals")
{
    using namespace stella::vm;

    VM vm;
    Program program;

    const auto input_value = static_cast<std::uint32_t>(program.add_constant(Value::i64(6)));
    const auto add_value = static_cast<std::uint32_t>(program.add_constant(Value::i64(3)));
    const auto mul_value = static_cast<std::uint32_t>(program.add_constant(Value::i64(2)));
    const auto function_index = static_cast<std::uint32_t>(program.add_function(3, 1, 2));

    program.code = {
        {OpCode::push_constant, input_value},
        {OpCode::call, function_index},
        {OpCode::halt, 0},
        {OpCode::load_local, 0},
        {OpCode::push_constant, add_value},
        {OpCode::add_i64, 0},
        {OpCode::store_local, 1},
        {OpCode::load_local, 1},
        {OpCode::push_constant, mul_value},
        {OpCode::mul_i64, 0},
        {OpCode::ret, 0},
    };

    const auto verify = vm.verify(program, 0);
    REQUIRE(verify.has_value());

    Result<Value> result = vm.run(program);
    REQUIRE(result.has_value());
    CHECK(result->is_i64());
    CHECK(result->as_i64() == 18);
}

TEST_CASE("VM step budget prevents runaway execution")
{
    using namespace stella::vm;

    VM vm;
    Program program;
    const auto value = static_cast<std::uint32_t>(program.add_constant(Value::i64(42)));
    program.code = {
        {OpCode::push_constant, value},
        {OpCode::halt, 0},
    };

    vm.set_step_budget(1);
    Result<Value> limited = vm.run_unchecked(program);
    REQUIRE(!limited.has_value());
    CHECK(limited.error().code == ErrorCode::step_budget_exceeded);

    vm.set_step_budget(2);
    Result<Value> allowed = vm.run_unchecked(program);
    REQUIRE(allowed.has_value());
    CHECK(allowed->is_i64());
    CHECK(allowed->as_i64() == 42);

    vm.clear_step_budget();
}

TEST_CASE("Value string ownership model is explicit and stable")
{
    using namespace stella::vm;

    std::string backing = "alpha";
    Value borrowed = Value::borrowed_string(backing);
    Value owned = Value::owned_string(backing);

    backing[0] = 'o';

    const auto borrowed_text = borrowed.expect_string("borrowed");
    REQUIRE(borrowed_text.has_value());
    CHECK(borrowed_text.value() == "olpha");
    CHECK(borrowed.is_string_view());
    CHECK(!borrowed.is_owned_string());

    const auto owned_text = owned.expect_string("owned");
    REQUIRE(owned_text.has_value());
    CHECK(owned_text.value() == "alpha");
    CHECK(owned.is_owned_string());
    CHECK(owned.is_string());
}

TEST_CASE("Value expect_i64 provides contextual diagnostics")
{
    using namespace stella::vm;

    Value text = Value::owned_string("123");
    const auto as_i64 = text.expect_i64("input parser");
    REQUIRE(!as_i64.has_value());
    CHECK(as_i64.error().code == ErrorCode::type_mismatch);
    CHECK(as_i64.error().message.find("input parser") != std::string::npos);
    CHECK(as_i64.error().message.find("owned_string") != std::string::npos);
}

TEST_CASE("bytecode VM executes bitwise and shift opcodes")
{
    using namespace stella::vm;

    VM vm;
    Program program;
    const auto v13 = static_cast<std::uint32_t>(program.add_constant(Value::i64(13)));
    const auto v7 = static_cast<std::uint32_t>(program.add_constant(Value::i64(7)));
    const auto v2 = static_cast<std::uint32_t>(program.add_constant(Value::i64(2)));
    const auto v6 = static_cast<std::uint32_t>(program.add_constant(Value::i64(6)));
    const auto v3 = static_cast<std::uint32_t>(program.add_constant(Value::i64(3)));
    const auto v1 = static_cast<std::uint32_t>(program.add_constant(Value::i64(1)));

    program.code = {
        {OpCode::push_constant, v13},
        {OpCode::push_constant, v7},
        {OpCode::and_i64, 0},
        {OpCode::push_constant, v2},
        {OpCode::shl_i64, 0},
        {OpCode::push_constant, v6},
        {OpCode::or_i64, 0},
        {OpCode::push_constant, v3},
        {OpCode::xor_i64, 0},
        {OpCode::push_constant, v1},
        {OpCode::shr_i64, 0},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 0);
    REQUIRE(verify.has_value());

    Result<Value> result = vm.run(program);
    REQUIRE(result.has_value());
    CHECK(result->is_i64());
    CHECK(result->as_i64() == 10);
}

TEST_CASE("shift opcodes reject out-of-range shift amount")
{
    using namespace stella::vm;

    VM vm;
    Program program;
    const auto one = static_cast<std::uint32_t>(program.add_constant(Value::i64(1)));
    const auto sixty_four = static_cast<std::uint32_t>(program.add_constant(Value::i64(64)));

    program.code = {
        {OpCode::push_constant, one},
        {OpCode::push_constant, sixty_four},
        {OpCode::shl_i64, 0},
        {OpCode::halt, 0},
    };

    const auto verify = vm.verify(program, 0);
    REQUIRE(verify.has_value());

    Result<Value> result = vm.run_unchecked(program);
    REQUIRE(!result.has_value());
    CHECK(result.error().code == ErrorCode::invalid_shift_amount);
}

TEST_CASE("bytecode serialization roundtrip preserves executable behavior")
{
    using namespace stella::vm;

    std::string borrowed_backing = "borrowed";
    MoveBuffer payload(3);
    payload.bytes()[0] = std::byte {0x11};
    payload.bytes()[1] = std::byte {0x22};
    payload.bytes()[2] = std::byte {0x33};

    Program program;
    const auto c0 = static_cast<std::uint32_t>(program.add_constant(Value::i64(7)));
    const auto c1 = static_cast<std::uint32_t>(program.add_constant(Value::i64(5)));
    const auto c2 = static_cast<std::uint32_t>(program.add_constant(Value::f64(3.5)));
    const auto c3 = static_cast<std::uint32_t>(program.add_constant(Value::borrowed_string(borrowed_backing)));
    const auto c4 = static_cast<std::uint32_t>(program.add_constant(Value::owned_string("owned")));
    const auto c5 = static_cast<std::uint32_t>(program.add_constant(Value::owned_buffer(std::move(payload))));
    const auto fn = static_cast<std::uint32_t>(program.add_function(3, 1, 1));

    (void)c2;
    (void)c3;
    (void)c4;
    (void)c5;

    program.code = {
        {OpCode::push_constant, c0},
        {OpCode::call, fn},
        {OpCode::halt, 0},
        {OpCode::load_local, 0},
        {OpCode::push_constant, c1},
        {OpCode::mul_i64, 0},
        {OpCode::ret, 0},
    };

    const auto encoded = serialize_program(program);
    REQUIRE(encoded.has_value());

    const auto decoded = deserialize_program(encoded->bytes());
    REQUIRE(decoded.has_value());

    CHECK(decoded->code.size() == program.code.size());
    CHECK(decoded->constants.size() == program.constants.size());
    CHECK(decoded->functions.size() == program.functions.size());

    CHECK(decoded->constants[3].is_string());
    CHECK(decoded->constants[3].expect_string("decoded").value() == "borrowed");
    CHECK(decoded->constants[4].is_string());
    CHECK(decoded->constants[4].expect_string("decoded").value() == "owned");
    CHECK(decoded->constants[5].is_buffer());
    CHECK(decoded->constants[5].as_buffer().size == 3);

    VM vm;
    const auto verify = vm.verify(*decoded, 0);
    REQUIRE(verify.has_value());
    const auto result = vm.run(*decoded);
    REQUIRE(result.has_value());
    CHECK(result->is_i64());
    CHECK(result->as_i64() == 35);
}

TEST_CASE("profiling and trace hooks collect execution telemetry")
{
    using namespace stella::vm;

    VM vm;
    vm.reset_profile();
    vm.set_profiling_enabled(true);

    std::vector<OpCode> trace;
    vm.set_trace_sink([&trace](const TraceEvent& event) { trace.push_back(event.opcode); });

    Program program;
    const auto c0 = static_cast<std::uint32_t>(program.add_constant(Value::i64(5)));
    program.code = {
        {OpCode::push_constant, c0},
        {OpCode::dup, 0},
        {OpCode::add_i64, 0},
        {OpCode::halt, 0},
    };

    const auto first = vm.run(program);
    REQUIRE(first.has_value());
    const auto second = vm.run(program);
    REQUIRE(second.has_value());

    const ProfileStats& profile = vm.profile();
    CHECK(profile.runs == 2);
    CHECK(profile.executed_steps == 8);
    CHECK(profile.opcode_counts[static_cast<std::size_t>(OpCode::push_constant)] == 2);
    CHECK(profile.opcode_counts[static_cast<std::size_t>(OpCode::dup)] == 2);
    CHECK(profile.opcode_counts[static_cast<std::size_t>(OpCode::add_i64)] == 2);
    CHECK(profile.opcode_counts[static_cast<std::size_t>(OpCode::halt)] == 2);
    CHECK(trace.size() == 8);

    vm.clear_trace_sink();
    vm.set_profiling_enabled(false);
}

TEST_CASE("property arithmetic differentials match host reference")
{
    using namespace stella::vm;

    auto next_random = [](std::uint64_t& state) -> std::uint64_t {
        state = (state * 6364136223846793005ULL) + 1442695040888963407ULL;
        return state;
    };

    VM vm;
    std::uint64_t state = 0xA1B2C3D4E5F60789ULL;

    for (int i = 0; i < 200; ++i)
    {
        std::int64_t lhs = static_cast<std::int64_t>(next_random(state) % 200001ULL) - 100000;
        std::int64_t rhs = static_cast<std::int64_t>(next_random(state) % 200001ULL) - 100000;
        const std::uint32_t op = static_cast<std::uint32_t>(next_random(state) % 11ULL);

        if (op == 2)
        {
            lhs %= 1000;
            rhs %= 1000;
        }
        if (op == 3)
        {
            if (rhs == 0)
            {
                rhs = 1;
            }
        }
        if (op == 9 || op == 10)
        {
            rhs = static_cast<std::int64_t>(next_random(state) % 64ULL);
        }

        Program program;
        const auto c_lhs = static_cast<std::uint32_t>(program.add_constant(Value::i64(lhs)));
        const auto c_rhs = static_cast<std::uint32_t>(program.add_constant(Value::i64(rhs)));
        OpCode opcode = OpCode::add_i64;
        std::int64_t expected = 0;

        switch (op)
        {
            case 0:
                opcode = OpCode::add_i64;
                expected = lhs + rhs;
                break;
            case 1:
                opcode = OpCode::sub_i64;
                expected = lhs - rhs;
                break;
            case 2:
                opcode = OpCode::mul_i64;
                expected = lhs * rhs;
                break;
            case 3:
                opcode = OpCode::mod_i64;
                expected = lhs % rhs;
                break;
            case 4:
                opcode = OpCode::and_i64;
                expected = lhs & rhs;
                break;
            case 5:
                opcode = OpCode::or_i64;
                expected = lhs | rhs;
                break;
            case 6:
                opcode = OpCode::xor_i64;
                expected = lhs ^ rhs;
                break;
            case 7:
                opcode = OpCode::cmp_eq_i64;
                expected = lhs == rhs ? 1 : 0;
                break;
            case 8:
                opcode = OpCode::cmp_lt_i64;
                expected = lhs < rhs ? 1 : 0;
                break;
            case 9:
                opcode = OpCode::shl_i64;
                expected = lhs << rhs;
                break;
            case 10:
                opcode = OpCode::shr_i64;
                expected = lhs >> rhs;
                break;
            default:
                FAIL("Unexpected opcode selector");
                break;
        }

        program.code = {
            {OpCode::push_constant, c_lhs},
            {OpCode::push_constant, c_rhs},
            {opcode, 0},
            {OpCode::halt, 0},
        };

        const auto result = vm.run(program);
        REQUIRE(result.has_value());
        REQUIRE(result->is_i64());
        CHECK(result->as_i64() == expected);
    }
}

TEST_CASE("bytecode parser rejects invalid magic")
{
    using namespace stella::vm;

    MoveBuffer bytes(20);
    auto view = bytes.bytes();
    view[0] = std::byte {0x00}; // bad magic (little-endian u32)
    view[1] = std::byte {0x00};
    view[2] = std::byte {0x00};
    view[3] = std::byte {0x00};
    view[4] = std::byte {0x01}; // version = 1
    view[5] = std::byte {0x00};
    view[6] = std::byte {0x00}; // reserved = 0
    view[7] = std::byte {0x00};
    view[8] = std::byte {0x00}; // instruction_count = 0
    view[9] = std::byte {0x00};
    view[10] = std::byte {0x00};
    view[11] = std::byte {0x00};
    view[12] = std::byte {0x00}; // constant_count = 0
    view[13] = std::byte {0x00};
    view[14] = std::byte {0x00};
    view[15] = std::byte {0x00};
    view[16] = std::byte {0x00}; // function_count = 0
    view[17] = std::byte {0x00};
    view[18] = std::byte {0x00};
    view[19] = std::byte {0x00};

    const auto decoded = deserialize_program(bytes.bytes());
    REQUIRE(!decoded.has_value());
    CHECK(decoded.error().code == ErrorCode::invalid_bytecode_magic);
}
