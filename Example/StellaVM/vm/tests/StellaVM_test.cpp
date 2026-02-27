#include <doctest/doctest.h>

import vm;

#include <cstddef>
#include <span>

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
    Value result = vm.run(program);

    CHECK(result.is_i64());
    CHECK(result.as_i64() == 42);
}

TEST_CASE("bytecode VM can call native C++ function")
{
    using namespace stella::vm;

    VM vm;
    const auto native_sum3 =
        static_cast<std::uint32_t>(vm.bind_native("sum3", 3, [](VM&, std::span<Value> args) -> Value {
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

    Value result = vm.run(program);

    CHECK(result.is_i64());
    CHECK(result.as_i64() == 42);
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
        vm.bind_native("identity_buffer", 1, [](VM&, std::span<Value> args) -> Value {
            CHECK(args[0].is_buffer());
            MoveBuffer buffer = args[0].take_buffer();
            buffer.bytes()[1] = std::byte {0x55};
            return Value::owned_buffer(std::move(buffer));
        }));

    const auto input_index = static_cast<std::uint32_t>(vm.push_input(Value::owned_buffer(std::move(payload))));

    Program program;
    program.code = {
        {OpCode::push_input, input_index},
        {OpCode::call_native, native_identity},
        {OpCode::halt, 0}};

    Value result = vm.run(program);

    CHECK(result.is_buffer());
    MoveBuffer returned = result.take_buffer();
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
