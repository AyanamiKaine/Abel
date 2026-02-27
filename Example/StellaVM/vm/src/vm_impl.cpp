module;
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <memory_resource>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <variant>
#include <vector>
module vm;

namespace stella::vm
{
namespace
{
template <typename... Ts>
struct Overload : Ts...
{
    using Ts::operator()...;
};

template <typename... Ts>
Overload(Ts...) -> Overload<Ts...>;
} // namespace

MoveBuffer::MoveBuffer(std::size_t byte_count)
    : data(byte_count == 0 ? nullptr : std::make_unique<std::byte[]>(byte_count))
    , size(byte_count)
{
}

MoveBuffer::MoveBuffer(std::unique_ptr<std::byte[]> bytes, std::size_t byte_count) noexcept
    : data(std::move(bytes))
    , size(byte_count)
{
}

auto MoveBuffer::bytes() noexcept -> std::span<std::byte>
{
    return {data.get(), size};
}

auto MoveBuffer::bytes() const noexcept -> std::span<const std::byte>
{
    return {data.get(), size};
}

auto MoveBuffer::data_ptr() noexcept -> std::byte*
{
    return data.get();
}

auto MoveBuffer::data_ptr() const noexcept -> const std::byte*
{
    return data.get();
}

Value::Value(std::int64_t integer) noexcept
    : storage_(integer)
{
}

Value::Value(double floating_point) noexcept
    : storage_(floating_point)
{
}

Value::Value(std::string_view borrowed_text) noexcept
    : storage_(borrowed_text)
{
}

Value::Value(MoveBuffer buffer) noexcept
    : storage_(std::move(buffer))
{
}

Value::Value(const Value& other)
    : storage_(std::visit(
          Overload {
              [](std::monostate value) -> Storage { return value; },
              [](std::int64_t value) -> Storage { return value; },
              [](double value) -> Storage { return value; },
              [](std::string_view value) -> Storage { return value; },
              [](const MoveBuffer&) -> Storage {
                  throw std::logic_error(
                      "MoveBuffer is move-only; copy is disallowed to prevent implicit data copies.");
              }},
          other.storage_))
{
}

auto Value::operator=(const Value& other) -> Value&
{
    if (this == &other)
    {
        return *this;
    }

    Value copy(other);
    storage_ = std::move(copy.storage_);
    return *this;
}

auto Value::i64(std::int64_t integer) noexcept -> Value
{
    return Value(integer);
}

auto Value::f64(double floating_point) noexcept -> Value
{
    return Value(floating_point);
}

auto Value::borrowed_string(std::string_view borrowed_text) noexcept -> Value
{
    return Value(borrowed_text);
}

auto Value::owned_buffer(MoveBuffer buffer) noexcept -> Value
{
    return Value(std::move(buffer));
}

auto Value::is_empty() const noexcept -> bool
{
    return std::holds_alternative<std::monostate>(storage_);
}

auto Value::is_i64() const noexcept -> bool
{
    return std::holds_alternative<std::int64_t>(storage_);
}

auto Value::is_f64() const noexcept -> bool
{
    return std::holds_alternative<double>(storage_);
}

auto Value::is_string_view() const noexcept -> bool
{
    return std::holds_alternative<std::string_view>(storage_);
}

auto Value::is_buffer() const noexcept -> bool
{
    return std::holds_alternative<MoveBuffer>(storage_);
}

auto Value::as_i64() -> std::int64_t&
{
    return std::get<std::int64_t>(storage_);
}

auto Value::as_i64() const -> const std::int64_t&
{
    return std::get<std::int64_t>(storage_);
}

auto Value::as_f64() -> double&
{
    return std::get<double>(storage_);
}

auto Value::as_f64() const -> const double&
{
    return std::get<double>(storage_);
}

auto Value::as_string_view() -> std::string_view&
{
    return std::get<std::string_view>(storage_);
}

auto Value::as_string_view() const -> const std::string_view&
{
    return std::get<std::string_view>(storage_);
}

auto Value::as_buffer() -> MoveBuffer&
{
    return std::get<MoveBuffer>(storage_);
}

auto Value::as_buffer() const -> const MoveBuffer&
{
    return std::get<MoveBuffer>(storage_);
}

auto Value::take_buffer() -> MoveBuffer
{
    if (!is_buffer())
    {
        throw std::logic_error("Attempted to take MoveBuffer from non-buffer Value.");
    }

    MoveBuffer buffer = std::move(std::get<MoveBuffer>(storage_));
    storage_ = std::monostate {};
    return buffer;
}

Arena::Marker::Marker(Arena* arena, std::size_t rewind_to) noexcept
    : arena_(arena)
    , rewind_to_(rewind_to)
{
}

Arena::Marker::~Marker()
{
    if (arena_ != nullptr)
    {
        arena_->rewind(rewind_to_);
    }
}

Arena::Marker::Marker(Marker&& other) noexcept
    : arena_(std::exchange(other.arena_, nullptr))
    , rewind_to_(std::exchange(other.rewind_to_, 0))
{
}

auto Arena::Marker::operator=(Marker&& other) noexcept -> Marker&
{
    if (this == &other)
    {
        return *this;
    }

    if (arena_ != nullptr)
    {
        arena_->rewind(rewind_to_);
    }

    arena_ = std::exchange(other.arena_, nullptr);
    rewind_to_ = std::exchange(other.rewind_to_, 0);
    return *this;
}

void Arena::Marker::release() noexcept
{
    arena_ = nullptr;
}

Arena::Arena(std::size_t initial_bytes)
    : initial_buffer_(initial_bytes)
    , resource_(
          initial_buffer_.empty() ? nullptr : initial_buffer_.data(),
          initial_buffer_.size(),
          std::pmr::new_delete_resource())
{
}

Arena::~Arena()
{
    reset();
}

auto Arena::mark() noexcept -> Marker
{
    return Marker(this, tracked_allocations_.size());
}

void Arena::reset() noexcept
{
    rewind(0);
    resource_.release();
}

auto Arena::live_allocations() const noexcept -> std::size_t
{
    return static_cast<std::size_t>(std::count_if(
        tracked_allocations_.begin(),
        tracked_allocations_.end(),
        [](const TrackedAllocation& tracked) { return tracked.alive; }));
}

void* Arena::allocate_bytes(std::size_t size, std::size_t alignment)
{
    return resource_.allocate(size, alignment);
}

void Arena::register_destructor(void* object, void (*destroy)(void*) noexcept)
{
    tracked_allocations_.push_back({object, destroy, true});
}

void Arena::rewind(std::size_t index) noexcept
{
    if (index > tracked_allocations_.size())
    {
        index = tracked_allocations_.size();
    }

    for (std::size_t i = tracked_allocations_.size(); i > index; --i)
    {
        TrackedAllocation& tracked = tracked_allocations_[i - 1];
        if (tracked.alive && tracked.destroy != nullptr)
        {
            tracked.destroy(tracked.object);
            tracked.alive = false;
        }
    }

    tracked_allocations_.resize(index);
}

auto Program::add_constant(Value value) -> std::size_t
{
    constants.push_back(std::move(value));
    return constants.size() - 1;
}

VM::VM(std::size_t stack_reserve, std::size_t arena_bytes)
    : arena_(arena_bytes)
{
    stack_.reserve(stack_reserve);
}

auto VM::bind_native(std::string name, std::size_t arity, NativeFunction function) -> std::size_t
{
    native_bindings_.push_back({std::move(name), arity, std::move(function)});
    return native_bindings_.size() - 1;
}

auto VM::push_input(Value value) -> std::size_t
{
    inputs_.push_back(std::move(value));
    return inputs_.size() - 1;
}

void VM::clear_inputs()
{
    inputs_.clear();
}

void VM::clear_stack()
{
    stack_.clear();
}

auto VM::stack() noexcept -> std::span<Value>
{
    return stack_;
}

auto VM::stack() const noexcept -> std::span<const Value>
{
    return stack_;
}

auto VM::arena() noexcept -> Arena&
{
    return arena_;
}

auto VM::arena() const noexcept -> const Arena&
{
    return arena_;
}

auto VM::run(const Program& program) -> Value
{
    clear_stack();

    for (std::size_t pc = 0; pc < program.code.size(); ++pc)
    {
        const Instruction& instruction = program.code[pc];

        switch (instruction.opcode)
        {
            case OpCode::push_constant:
            {
                if (instruction.operand >= program.constants.size())
                {
                    throw std::runtime_error("push_constant operand out of range.");
                }
                stack_.push_back(program.constants[instruction.operand]);
                break;
            }
            case OpCode::push_input:
            {
                if (instruction.operand >= inputs_.size())
                {
                    throw std::runtime_error("push_input operand out of range.");
                }
                stack_.push_back(std::move(inputs_[instruction.operand]));
                inputs_[instruction.operand] = Value {};
                break;
            }
            case OpCode::add_i64:
            {
                stack_.push_back(execute_add_i64());
                break;
            }
            case OpCode::call_native:
            {
                stack_.push_back(execute_call_native(instruction.operand));
                break;
            }
            case OpCode::halt:
            {
                if (stack_.empty())
                {
                    return Value {};
                }
                return pop_value();
            }
            default:
            {
                throw std::runtime_error("Unknown opcode.");
            }
        }
    }

    if (stack_.empty())
    {
        return Value {};
    }

    return pop_value();
}

auto VM::pop_value() -> Value
{
    if (stack_.empty())
    {
        throw std::runtime_error("VM stack underflow.");
    }

    Value value = std::move(stack_.back());
    stack_.pop_back();
    return value;
}

auto VM::execute_add_i64() -> Value
{
    Value rhs = pop_value();
    Value lhs = pop_value();

    if (!lhs.is_i64() || !rhs.is_i64())
    {
        throw std::runtime_error("add_i64 expects two i64 values.");
    }

    return Value::i64(lhs.as_i64() + rhs.as_i64());
}

auto VM::execute_call_native(std::size_t binding_index) -> Value
{
    if (binding_index >= native_bindings_.size())
    {
        throw std::runtime_error("call_native operand out of range.");
    }

    NativeBinding& binding = native_bindings_[binding_index];
    if (!binding.function)
    {
        throw std::runtime_error("Native function binding is empty.");
    }

    if (stack_.size() < binding.arity)
    {
        throw std::runtime_error("call_native does not have enough stack arguments.");
    }

    const std::size_t args_offset = stack_.size() - binding.arity;
    std::span<Value> args(stack_.data() + args_offset, binding.arity);

    Value result = binding.function(*this, args);
    stack_.resize(args_offset);
    return result;
}
} // namespace stella::vm
