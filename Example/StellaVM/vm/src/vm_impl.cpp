module;
#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <expected>
#include <functional>
#include <limits>
#include <memory>
#include <memory_resource>
#include <optional>
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

[[nodiscard]] auto make_unexpected(ErrorCode code, std::string message) -> std::unexpected<Error>
{
    return std::unexpected<Error> {Error {code, std::move(message)}};
}

enum class ConstantTag : std::uint8_t
{
    empty = 0,
    i64 = 1,
    f64 = 2,
    string = 3,
    buffer = 4
};

class ByteWriter final
{
public:
    void write_u8(std::uint8_t value)
    {
        bytes_.push_back(std::byte {value});
    }

    void write_u16(std::uint16_t value)
    {
        write_raw(value);
    }

    void write_u32(std::uint32_t value)
    {
        write_raw(value);
    }

    void write_u64(std::uint64_t value)
    {
        write_raw(value);
    }

    void write_i64(std::int64_t value)
    {
        write_raw(value);
    }

    void write_f64(double value)
    {
        write_raw(value);
    }

    void write_bytes(std::span<const std::byte> bytes)
    {
        bytes_.insert(bytes_.end(), bytes.begin(), bytes.end());
    }

    [[nodiscard]] auto finish() -> MoveBuffer
    {
        MoveBuffer buffer(bytes_.size());
        if (!bytes_.empty())
        {
            std::copy(bytes_.begin(), bytes_.end(), buffer.bytes().begin());
        }
        return buffer;
    }

private:
    template <typename T>
    void write_raw(const T value)
    {
        constexpr std::size_t size = sizeof(T);
        const auto* raw = reinterpret_cast<const std::byte*>(&value);
        bytes_.insert(bytes_.end(), raw, raw + size);
    }

    std::vector<std::byte> bytes_ {};
};

class ByteReader final
{
public:
    explicit ByteReader(std::span<const std::byte> bytes)
        : bytes_(bytes)
    {
    }

    [[nodiscard]] auto read_u8(std::uint8_t& value) -> bool
    {
        return read_raw(value);
    }

    [[nodiscard]] auto read_u16(std::uint16_t& value) -> bool
    {
        return read_raw(value);
    }

    [[nodiscard]] auto read_u32(std::uint32_t& value) -> bool
    {
        return read_raw(value);
    }

    [[nodiscard]] auto read_u64(std::uint64_t& value) -> bool
    {
        return read_raw(value);
    }

    [[nodiscard]] auto read_i64(std::int64_t& value) -> bool
    {
        return read_raw(value);
    }

    [[nodiscard]] auto read_f64(double& value) -> bool
    {
        return read_raw(value);
    }

    [[nodiscard]] auto read_bytes(const std::size_t count, std::span<const std::byte>& bytes) -> bool
    {
        if (remaining() < count)
        {
            return false;
        }

        bytes = bytes_.subspan(offset_, count);
        offset_ += count;
        return true;
    }

    [[nodiscard]] auto remaining() const -> std::size_t
    {
        return bytes_.size() - offset_;
    }

private:
    template <typename T>
    [[nodiscard]] auto read_raw(T& value) -> bool
    {
        constexpr std::size_t size = sizeof(T);
        if (remaining() < size)
        {
            return false;
        }

        std::memcpy(&value, bytes_.data() + offset_, size);
        offset_ += size;
        return true;
    }

    std::span<const std::byte> bytes_ {};
    std::size_t offset_ = 0;
};
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

Value::Value(std::string owned_text) noexcept
    : storage_(std::move(owned_text))
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
              [](const std::string& value) -> Storage { return value; },
              [](const MoveBuffer& value) -> Storage {
                  MoveBuffer copied(value.size);
                  if (value.size != 0)
                  {
                      std::copy(value.bytes().begin(), value.bytes().end(), copied.bytes().begin());
                  }
                  return Storage(std::move(copied));
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

auto Value::owned_string(std::string owned_text) noexcept -> Value
{
    return Value(std::move(owned_text));
}

auto Value::owned_buffer(MoveBuffer buffer) noexcept -> Value
{
    return Value(std::move(buffer));
}

auto Value::kind() const noexcept -> Kind
{
    if (std::holds_alternative<std::monostate>(storage_))
    {
        return Kind::empty;
    }
    if (std::holds_alternative<std::int64_t>(storage_))
    {
        return Kind::i64;
    }
    if (std::holds_alternative<double>(storage_))
    {
        return Kind::f64;
    }
    if (std::holds_alternative<std::string_view>(storage_))
    {
        return Kind::borrowed_string;
    }
    if (std::holds_alternative<std::string>(storage_))
    {
        return Kind::owned_string;
    }

    return Kind::buffer;
}

auto Value::kind_name(const Kind kind) noexcept -> std::string_view
{
    switch (kind)
    {
        case Kind::empty:
            return "empty";
        case Kind::i64:
            return "i64";
        case Kind::f64:
            return "f64";
        case Kind::borrowed_string:
            return "borrowed_string";
        case Kind::owned_string:
            return "owned_string";
        case Kind::buffer:
            return "buffer";
    }

    return "unknown";
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

auto Value::is_owned_string() const noexcept -> bool
{
    return std::holds_alternative<std::string>(storage_);
}

auto Value::is_string() const noexcept -> bool
{
    return is_string_view() || is_owned_string();
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

auto Value::as_owned_string() -> std::string&
{
    return std::get<std::string>(storage_);
}

auto Value::as_owned_string() const -> const std::string&
{
    return std::get<std::string>(storage_);
}

auto Value::as_buffer() -> MoveBuffer&
{
    return std::get<MoveBuffer>(storage_);
}

auto Value::as_buffer() const -> const MoveBuffer&
{
    return std::get<MoveBuffer>(storage_);
}

auto Value::expect_i64(std::string_view context) const -> Result<std::int64_t>
{
    if (is_i64())
    {
        return as_i64();
    }

    std::string message(context);
    message += " expected i64 but got ";
    message += kind_name(kind());
    message += '.';
    return make_unexpected(ErrorCode::type_mismatch, std::move(message));
}

auto Value::expect_string(std::string_view context) const -> Result<std::string_view>
{
    if (is_string_view())
    {
        return as_string_view();
    }
    if (is_owned_string())
    {
        return as_owned_string();
    }

    std::string message(context);
    message += " expected string but got ";
    message += kind_name(kind());
    message += '.';
    return make_unexpected(ErrorCode::type_mismatch, std::move(message));
}

auto Value::take_buffer() -> Result<MoveBuffer>
{
    if (!is_buffer())
    {
        return make_unexpected(
            ErrorCode::invalid_buffer_access,
            "Attempted to take MoveBuffer from non-buffer Value.");
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

auto Program::add_function(std::uint32_t entry, std::uint32_t arity, std::uint32_t local_count) -> std::size_t
{
    functions.push_back({entry, arity, local_count});
    return functions.size() - 1;
}

auto serialize_program(const Program& program) -> Result<MoveBuffer>
{
    if (program.code.size() > std::numeric_limits<std::uint32_t>::max() ||
        program.constants.size() > std::numeric_limits<std::uint32_t>::max() ||
        program.functions.size() > std::numeric_limits<std::uint32_t>::max())
    {
        return make_unexpected(
            ErrorCode::malformed_bytecode,
            "Program exceeds bytecode format size limits.");
    }

    ByteWriter writer;
    writer.write_u32(bytecode_magic);
    writer.write_u16(bytecode_version);
    writer.write_u16(0);
    writer.write_u32(static_cast<std::uint32_t>(program.code.size()));
    writer.write_u32(static_cast<std::uint32_t>(program.constants.size()));
    writer.write_u32(static_cast<std::uint32_t>(program.functions.size()));

    for (const Instruction& instruction : program.code)
    {
        writer.write_u8(static_cast<std::uint8_t>(instruction.opcode));
        writer.write_u32(instruction.operand);
    }

    for (const Value& constant : program.constants)
    {
        if (constant.is_empty())
        {
            writer.write_u8(static_cast<std::uint8_t>(ConstantTag::empty));
            continue;
        }
        if (constant.is_i64())
        {
            writer.write_u8(static_cast<std::uint8_t>(ConstantTag::i64));
            writer.write_i64(constant.as_i64());
            continue;
        }
        if (constant.is_f64())
        {
            writer.write_u8(static_cast<std::uint8_t>(ConstantTag::f64));
            writer.write_f64(constant.as_f64());
            continue;
        }
        if (constant.is_string())
        {
            const auto text_result = constant.expect_string("serialize_program");
            if (!text_result.has_value())
            {
                return std::unexpected(text_result.error());
            }

            if (text_result->size() > std::numeric_limits<std::uint32_t>::max())
            {
                return make_unexpected(
                    ErrorCode::malformed_bytecode,
                    "String constant exceeds bytecode format size limits.");
            }

            writer.write_u8(static_cast<std::uint8_t>(ConstantTag::string));
            writer.write_u32(static_cast<std::uint32_t>(text_result->size()));
            const auto* raw = reinterpret_cast<const std::byte*>(text_result->data());
            writer.write_bytes(std::span<const std::byte>(raw, text_result->size()));
            continue;
        }
        if (constant.is_buffer())
        {
            const MoveBuffer& buffer = constant.as_buffer();
            if (buffer.size > std::numeric_limits<std::uint32_t>::max())
            {
                return make_unexpected(
                    ErrorCode::malformed_bytecode,
                    "Buffer constant exceeds bytecode format size limits.");
            }

            writer.write_u8(static_cast<std::uint8_t>(ConstantTag::buffer));
            writer.write_u32(static_cast<std::uint32_t>(buffer.size));
            writer.write_bytes(buffer.bytes());
            continue;
        }

        return make_unexpected(
            ErrorCode::malformed_bytecode,
            "Encountered unsupported constant kind during serialization.");
    }

    for (const Program::Function& function : program.functions)
    {
        writer.write_u32(function.entry);
        writer.write_u32(function.arity);
        writer.write_u32(function.local_count);
    }

    return writer.finish();
}

auto deserialize_program(std::span<const std::byte> bytes) -> Result<Program>
{
    ByteReader reader(bytes);

    std::uint32_t magic = 0;
    std::uint16_t version = 0;
    std::uint16_t reserved = 0;
    std::uint32_t instruction_count = 0;
    std::uint32_t constant_count = 0;
    std::uint32_t function_count = 0;

    if (!reader.read_u32(magic) || !reader.read_u16(version) || !reader.read_u16(reserved) ||
        !reader.read_u32(instruction_count) || !reader.read_u32(constant_count) ||
        !reader.read_u32(function_count))
    {
        return make_unexpected(ErrorCode::malformed_bytecode, "Bytecode header is truncated.");
    }

    if (magic != bytecode_magic)
    {
        return make_unexpected(ErrorCode::invalid_bytecode_magic, "Bytecode magic number mismatch.");
    }
    if (version != bytecode_version)
    {
        return make_unexpected(
            ErrorCode::unsupported_bytecode_version,
            "Unsupported bytecode version.");
    }

    Program program;
    program.code.reserve(instruction_count);
    program.constants.reserve(constant_count);
    program.functions.reserve(function_count);

    for (std::uint32_t i = 0; i < instruction_count; ++i)
    {
        std::uint8_t opcode_raw = 0;
        std::uint32_t operand = 0;
        if (!reader.read_u8(opcode_raw) || !reader.read_u32(operand))
        {
            return make_unexpected(ErrorCode::malformed_bytecode, "Instruction table is truncated.");
        }

        program.code.push_back({static_cast<OpCode>(opcode_raw), operand});
    }

    for (std::uint32_t i = 0; i < constant_count; ++i)
    {
        std::uint8_t tag_raw = 0;
        if (!reader.read_u8(tag_raw))
        {
            return make_unexpected(ErrorCode::malformed_bytecode, "Constant table is truncated.");
        }

        switch (static_cast<ConstantTag>(tag_raw))
        {
            case ConstantTag::empty:
                program.constants.push_back(Value {});
                break;
            case ConstantTag::i64:
            {
                std::int64_t value = 0;
                if (!reader.read_i64(value))
                {
                    return make_unexpected(ErrorCode::malformed_bytecode, "i64 constant is truncated.");
                }
                program.constants.push_back(Value::i64(value));
                break;
            }
            case ConstantTag::f64:
            {
                double value = 0.0;
                if (!reader.read_f64(value))
                {
                    return make_unexpected(ErrorCode::malformed_bytecode, "f64 constant is truncated.");
                }
                program.constants.push_back(Value::f64(value));
                break;
            }
            case ConstantTag::string:
            {
                std::uint32_t length = 0;
                if (!reader.read_u32(length))
                {
                    return make_unexpected(ErrorCode::malformed_bytecode, "String constant length is truncated.");
                }

                std::span<const std::byte> text_bytes;
                if (!reader.read_bytes(length, text_bytes))
                {
                    return make_unexpected(ErrorCode::malformed_bytecode, "String constant payload is truncated.");
                }

                std::string value;
                value.resize(length);
                if (length != 0)
                {
                    std::memcpy(value.data(), text_bytes.data(), length);
                }
                program.constants.push_back(Value::owned_string(std::move(value)));
                break;
            }
            case ConstantTag::buffer:
            {
                std::uint32_t length = 0;
                if (!reader.read_u32(length))
                {
                    return make_unexpected(ErrorCode::malformed_bytecode, "Buffer constant length is truncated.");
                }

                std::span<const std::byte> payload;
                if (!reader.read_bytes(length, payload))
                {
                    return make_unexpected(ErrorCode::malformed_bytecode, "Buffer constant payload is truncated.");
                }

                MoveBuffer buffer(length);
                if (length != 0)
                {
                    std::copy(payload.begin(), payload.end(), buffer.bytes().begin());
                }
                program.constants.push_back(Value::owned_buffer(std::move(buffer)));
                break;
            }
            default:
            {
                return make_unexpected(
                    ErrorCode::malformed_bytecode,
                    "Unknown constant tag in bytecode.");
            }
        }
    }

    for (std::uint32_t i = 0; i < function_count; ++i)
    {
        std::uint32_t entry = 0;
        std::uint32_t arity = 0;
        std::uint32_t local_count = 0;

        if (!reader.read_u32(entry) || !reader.read_u32(arity) || !reader.read_u32(local_count))
        {
            return make_unexpected(ErrorCode::malformed_bytecode, "Function table is truncated.");
        }

        program.functions.push_back({entry, arity, local_count});
    }

    if (reader.remaining() != 0)
    {
        return make_unexpected(
            ErrorCode::malformed_bytecode,
            "Bytecode payload has trailing bytes.");
    }

    return program;
}

VM::VM(std::size_t stack_reserve, std::size_t arena_bytes)
    : arena_(arena_bytes)
{
    stack_.reserve(stack_reserve);
    call_frames_.reserve(16);
}

NativeBindingBuilder::NativeBindingBuilder(VM& vm, std::string name)
    : vm_(&vm)
    , name_(std::move(name))
{
}

auto NativeBindingBuilder::arity(const std::size_t expected_arity) -> NativeBindingBuilder&
{
    explicit_arity_ = expected_arity;
    return *this;
}

auto VM::bind_native(std::string name, std::size_t arity, NativeFunction function) -> std::size_t
{
    native_bindings_.push_back({std::move(name), arity, std::move(function)});
    return native_bindings_.size() - 1;
}

auto VM::native(std::string name) -> NativeBindingBuilder
{
    return NativeBindingBuilder(*this, std::move(name));
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

void VM::set_step_budget(std::size_t max_steps) noexcept
{
    step_budget_ = max_steps;
}

void VM::clear_step_budget() noexcept
{
    step_budget_ = 0;
}

void VM::set_trace_sink(std::move_only_function<void(const TraceEvent&)> trace_sink)
{
    trace_sink_ = std::move(trace_sink);
}

void VM::clear_trace_sink()
{
    trace_sink_ = {};
}

void VM::set_profiling_enabled(const bool enabled) noexcept
{
    profiling_enabled_ = enabled;
}

auto VM::profiling_enabled() const noexcept -> bool
{
    return profiling_enabled_;
}

void VM::reset_profile() noexcept
{
    profile_stats_ = {};
}

auto VM::profile() const noexcept -> const ProfileStats&
{
    return profile_stats_;
}

auto VM::verify(const Program& program, std::size_t available_inputs) const -> VoidResult
{
    if (program.code.empty())
    {
        return make_unexpected(ErrorCode::verification_failed, "Program has no instructions.");
    }

    for (const auto& function : program.functions)
    {
        if (function.entry >= program.code.size())
        {
            return make_unexpected(
                ErrorCode::invalid_function_index,
                "Function entry points outside bytecode.");
        }
        if (function.local_count < function.arity)
        {
            return make_unexpected(
                ErrorCode::invalid_function_signature,
                "Function local_count must be >= arity.");
        }
    }

    std::vector<std::optional<std::size_t>> stack_depth_at_pc(program.code.size());
    std::optional<std::size_t> stack_depth_at_end;
    std::vector<std::size_t> worklist;
    worklist.reserve(program.code.size());

    const auto enqueue_successor = [&](const std::size_t pc, const std::size_t depth) -> VoidResult {
        if (pc == program.code.size())
        {
            if (!stack_depth_at_end.has_value())
            {
                stack_depth_at_end = depth;
                return {};
            }

            if (stack_depth_at_end.value() != depth)
            {
                return make_unexpected(
                    ErrorCode::verification_failed,
                    "Inconsistent stack depth at implicit program end.");
            }

            return {};
        }

        if (pc > program.code.size())
        {
            return make_unexpected(
                ErrorCode::invalid_jump_target,
                "Jump target points past end of bytecode.");
        }

        std::optional<std::size_t>& known_depth = stack_depth_at_pc[pc];
        if (!known_depth.has_value())
        {
            known_depth = depth;
            worklist.push_back(pc);
            return {};
        }

        if (known_depth.value() != depth)
        {
            return make_unexpected(
                ErrorCode::verification_failed,
                "Inconsistent stack depth across control-flow merge.");
        }

        return {};
    };

    {
        const auto entry = enqueue_successor(0, 0);
        if (!entry.has_value())
        {
            return std::unexpected(entry.error());
        }
    }

    while (!worklist.empty())
    {
        const std::size_t pc = worklist.back();
        worklist.pop_back();

        const Instruction& instruction = program.code[pc];
        const std::size_t stack_depth = stack_depth_at_pc[pc].value_or(0);

        std::size_t pops = 0;
        std::size_t pushes = 0;
        std::optional<std::size_t> explicit_target;
        bool has_fallthrough = true;

        switch (instruction.opcode)
        {
            case OpCode::push_constant:
            {
                if (instruction.operand >= program.constants.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_constant_index,
                        "push_constant operand out of range during verification.");
                }
                pushes = 1;
                break;
            }
            case OpCode::push_input:
            {
                if (instruction.operand >= available_inputs)
                {
                    return make_unexpected(
                        ErrorCode::invalid_input_index,
                        "push_input operand out of range during verification.");
                }
                pushes = 1;
                break;
            }
            case OpCode::add_i64:
            case OpCode::sub_i64:
            case OpCode::mul_i64:
            case OpCode::mod_i64:
            case OpCode::cmp_eq_i64:
            case OpCode::cmp_lt_i64:
            case OpCode::and_i64:
            case OpCode::or_i64:
            case OpCode::xor_i64:
            case OpCode::shl_i64:
            case OpCode::shr_i64:
            {
                pops = 2;
                pushes = 1;
                break;
            }
            case OpCode::call_native:
            {
                if (instruction.operand >= native_bindings_.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_native_index,
                        "call_native operand out of range during verification.");
                }
                if (!native_bindings_[instruction.operand].function)
                {
                    return make_unexpected(
                        ErrorCode::empty_native_binding,
                        "call_native resolved to empty native binding during verification.");
                }
                pops = native_bindings_[instruction.operand].arity;
                pushes = 1;
                break;
            }
            case OpCode::jump:
            {
                explicit_target = static_cast<std::size_t>(instruction.operand);
                has_fallthrough = false;
                break;
            }
            case OpCode::jump_if_true:
            {
                pops = 1;
                explicit_target = static_cast<std::size_t>(instruction.operand);
                has_fallthrough = true;
                break;
            }
            case OpCode::dup:
            {
                if (stack_depth == 0)
                {
                    return make_unexpected(
                        ErrorCode::stack_underflow,
                        "dup requires at least one value on stack.");
                }
                pushes = 1;
                break;
            }
            case OpCode::pop:
            {
                pops = 1;
                break;
            }
            case OpCode::call:
            {
                if (instruction.operand >= program.functions.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_function_index,
                        "call operand out of range during verification.");
                }

                const auto& function = program.functions[instruction.operand];
                pops = function.arity;
                pushes = 1;
                has_fallthrough = true;
                break;
            }
            case OpCode::ret:
            {
                pops = 1;
                has_fallthrough = false;
                break;
            }
            case OpCode::load_local:
            {
                pushes = 1;
                break;
            }
            case OpCode::store_local:
            {
                pops = 1;
                break;
            }
            case OpCode::halt:
            {
                has_fallthrough = false;
                break;
            }
            default:
            {
                return make_unexpected(ErrorCode::unknown_opcode, "Unknown opcode during verification.");
            }
        }

        if (stack_depth < pops)
        {
            return make_unexpected(
                ErrorCode::stack_underflow,
                "Instruction would underflow stack during verification.");
        }

        const std::size_t next_depth = (stack_depth - pops) + pushes;

        if (explicit_target.has_value())
        {
            if (explicit_target.value() >= program.code.size())
            {
                return make_unexpected(
                    ErrorCode::invalid_jump_target,
                    "Jump target out of range during verification.");
            }

            const auto target_result = enqueue_successor(explicit_target.value(), next_depth);
            if (!target_result.has_value())
            {
                return std::unexpected(target_result.error());
            }
        }

        if (has_fallthrough)
        {
            const auto fallthrough_result = enqueue_successor(pc + 1, next_depth);
            if (!fallthrough_result.has_value())
            {
                return std::unexpected(fallthrough_result.error());
            }
        }
    }

    return {};
}

auto VM::run(const Program& program) -> Result<Value>
{
    const VoidResult verify_result = verify(program, inputs_.size());
    if (!verify_result.has_value())
    {
        return std::unexpected(verify_result.error());
    }

    return run_unchecked(program);
}

auto VM::run_unchecked(const Program& program) -> Result<Value>
{
    using Clock = std::chrono::steady_clock;

    struct RunProfileGuard final
    {
        VM* vm = nullptr;
        bool enabled = false;
        Clock::time_point started {};

        RunProfileGuard(VM* vm_ptr, const bool active)
            : vm(vm_ptr)
            , enabled(active)
            , started(active ? Clock::now() : Clock::time_point {})
        {
        }

        ~RunProfileGuard()
        {
            if (!enabled || vm == nullptr)
            {
                return;
            }

            ++vm->profile_stats_.runs;
            const auto elapsed = Clock::now() - started;
            vm->profile_stats_.total_run_nanoseconds +=
                static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count());
        }
    };

    RunProfileGuard run_profile(this, profiling_enabled_);

    clear_stack();
    call_frames_.clear();
    std::size_t executed_steps = 0;

    for (std::size_t pc = 0; pc < program.code.size();)
    {
        if (step_budget_ != 0 && executed_steps >= step_budget_)
        {
            return make_unexpected(
                ErrorCode::step_budget_exceeded,
                "VM step budget exhausted before termination.");
        }
        ++executed_steps;

        const Instruction& instruction = program.code[pc];
        bool advance_pc = true;
        const std::size_t opcode_index = static_cast<std::size_t>(instruction.opcode);

        struct InstructionProfileGuard final
        {
            VM* vm = nullptr;
            std::size_t opcode = 0;
            bool enabled = false;
            Clock::time_point started {};

            InstructionProfileGuard(VM* vm_ptr, const std::size_t opcode_index, const bool active)
                : vm(vm_ptr)
                , opcode(opcode_index)
                , enabled(active)
                , started(active ? Clock::now() : Clock::time_point {})
            {
                if (enabled && vm != nullptr)
                {
                    ++vm->profile_stats_.executed_steps;
                    ++vm->profile_stats_.opcode_counts[opcode];
                }
            }

            ~InstructionProfileGuard()
            {
                if (!enabled || vm == nullptr)
                {
                    return;
                }

                const auto elapsed = Clock::now() - started;
                vm->profile_stats_.opcode_nanoseconds[opcode] +=
                    static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count());
            }
        };

        InstructionProfileGuard instruction_profile(this, opcode_index, profiling_enabled_);

        if (trace_sink_)
        {
            trace_sink_(TraceEvent {
                .pc = pc,
                .opcode = instruction.opcode,
                .stack_size = stack_.size(),
                .call_depth = call_frames_.size(),
            });
        }

        switch (instruction.opcode)
        {
            case OpCode::push_constant:
            {
                if (instruction.operand >= program.constants.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_constant_index,
                        "push_constant operand out of range.");
                }

                const Value& constant = program.constants[instruction.operand];
                if (constant.is_i64() && (pc + 1) < program.code.size() && !stack_.empty())
                {
                    const Instruction& next_instruction = program.code[pc + 1];
                    const std::int64_t rhs = constant.as_i64();
                    auto lhs_result = stack_.back().expect_i64("fused_i64 lhs");
                    if (!lhs_result.has_value())
                    {
                        return std::unexpected(lhs_result.error());
                    }

                    const std::int64_t lhs = lhs_result.value();
                    std::optional<std::int64_t> fused_result;

                    switch (next_instruction.opcode)
                    {
                        case OpCode::add_i64:
                            fused_result = lhs + rhs;
                            break;
                        case OpCode::sub_i64:
                            fused_result = lhs - rhs;
                            break;
                        case OpCode::mul_i64:
                            fused_result = lhs * rhs;
                            break;
                        case OpCode::mod_i64:
                            if (rhs == 0)
                            {
                                return make_unexpected(ErrorCode::division_by_zero, "mod_i64 divisor cannot be zero.");
                            }
                            fused_result = lhs % rhs;
                            break;
                        case OpCode::cmp_eq_i64:
                            fused_result = lhs == rhs ? 1 : 0;
                            break;
                        case OpCode::cmp_lt_i64:
                            fused_result = lhs < rhs ? 1 : 0;
                            break;
                        case OpCode::and_i64:
                            fused_result = lhs & rhs;
                            break;
                        case OpCode::or_i64:
                            fused_result = lhs | rhs;
                            break;
                        case OpCode::xor_i64:
                            fused_result = lhs ^ rhs;
                            break;
                        case OpCode::shl_i64:
                            if (rhs < 0 || rhs > 63)
                            {
                                return make_unexpected(
                                    ErrorCode::invalid_shift_amount,
                                    "shl_i64 shift amount must be in [0, 63].");
                            }
                            fused_result = lhs << rhs;
                            break;
                        case OpCode::shr_i64:
                            if (rhs < 0 || rhs > 63)
                            {
                                return make_unexpected(
                                    ErrorCode::invalid_shift_amount,
                                    "shr_i64 shift amount must be in [0, 63].");
                            }
                            fused_result = lhs >> rhs;
                            break;
                        default:
                            break;
                    }

                    if (fused_result.has_value())
                    {
                        stack_.back() = Value::i64(fused_result.value());
                        pc += 2;
                        advance_pc = false;
                        break;
                    }
                }

                stack_.push_back(constant);
                break;
            }
            case OpCode::push_input:
            {
                if (instruction.operand >= inputs_.size())
                {
                    return make_unexpected(ErrorCode::invalid_input_index, "push_input operand out of range.");
                }
                stack_.push_back(std::move(inputs_[instruction.operand]));
                inputs_[instruction.operand] = Value {};
                break;
            }
            case OpCode::add_i64:
            {
                Result<Value> add_result = execute_add_i64();
                if (!add_result.has_value())
                {
                    return std::unexpected<Error> {add_result.error()};
                }
                stack_.push_back(std::move(add_result).value());
                break;
            }
            case OpCode::sub_i64:
            {
                Result<Value> sub_result = execute_sub_i64();
                if (!sub_result.has_value())
                {
                    return std::unexpected<Error> {sub_result.error()};
                }
                stack_.push_back(std::move(sub_result).value());
                break;
            }
            case OpCode::mul_i64:
            {
                Result<Value> mul_result = execute_mul_i64();
                if (!mul_result.has_value())
                {
                    return std::unexpected<Error> {mul_result.error()};
                }
                stack_.push_back(std::move(mul_result).value());
                break;
            }
            case OpCode::mod_i64:
            {
                Result<Value> mod_result = execute_mod_i64();
                if (!mod_result.has_value())
                {
                    return std::unexpected<Error> {mod_result.error()};
                }
                stack_.push_back(std::move(mod_result).value());
                break;
            }
            case OpCode::cmp_eq_i64:
            {
                Result<Value> cmp_result = execute_cmp_eq_i64();
                if (!cmp_result.has_value())
                {
                    return std::unexpected<Error> {cmp_result.error()};
                }
                stack_.push_back(std::move(cmp_result).value());
                break;
            }
            case OpCode::cmp_lt_i64:
            {
                Result<Value> cmp_result = execute_cmp_lt_i64();
                if (!cmp_result.has_value())
                {
                    return std::unexpected<Error> {cmp_result.error()};
                }
                stack_.push_back(std::move(cmp_result).value());
                break;
            }
            case OpCode::and_i64:
            {
                Result<Value> bit_result = execute_and_i64();
                if (!bit_result.has_value())
                {
                    return std::unexpected<Error> {bit_result.error()};
                }
                stack_.push_back(std::move(bit_result).value());
                break;
            }
            case OpCode::or_i64:
            {
                Result<Value> bit_result = execute_or_i64();
                if (!bit_result.has_value())
                {
                    return std::unexpected<Error> {bit_result.error()};
                }
                stack_.push_back(std::move(bit_result).value());
                break;
            }
            case OpCode::xor_i64:
            {
                Result<Value> bit_result = execute_xor_i64();
                if (!bit_result.has_value())
                {
                    return std::unexpected<Error> {bit_result.error()};
                }
                stack_.push_back(std::move(bit_result).value());
                break;
            }
            case OpCode::shl_i64:
            {
                Result<Value> shift_result = execute_shl_i64();
                if (!shift_result.has_value())
                {
                    return std::unexpected<Error> {shift_result.error()};
                }
                stack_.push_back(std::move(shift_result).value());
                break;
            }
            case OpCode::shr_i64:
            {
                Result<Value> shift_result = execute_shr_i64();
                if (!shift_result.has_value())
                {
                    return std::unexpected<Error> {shift_result.error()};
                }
                stack_.push_back(std::move(shift_result).value());
                break;
            }
            case OpCode::jump:
            {
                if (instruction.operand >= program.code.size())
                {
                    return make_unexpected(ErrorCode::invalid_jump_target, "jump target out of range.");
                }
                pc = static_cast<std::size_t>(instruction.operand);
                advance_pc = false;
                break;
            }
            case OpCode::jump_if_true:
            {
                if (instruction.operand >= program.code.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_jump_target,
                        "jump_if_true target out of range.");
                }

                Result<Value> condition_result = pop_value();
                if (!condition_result.has_value())
                {
                    return std::unexpected(condition_result.error());
                }

                Value condition = std::move(condition_result).value();
                Result<std::int64_t> condition_i64 = condition.expect_i64("jump_if_true");
                if (!condition_i64.has_value())
                {
                    return std::unexpected(condition_i64.error());
                }

                if (condition_i64.value() != 0)
                {
                    pc = static_cast<std::size_t>(instruction.operand);
                    advance_pc = false;
                }
                break;
            }
            case OpCode::dup:
            {
                if (stack_.empty())
                {
                    return make_unexpected(ErrorCode::stack_underflow, "dup requires non-empty stack.");
                }
                stack_.push_back(stack_.back());
                break;
            }
            case OpCode::pop:
            {
                if (stack_.empty())
                {
                    return make_unexpected(ErrorCode::stack_underflow, "pop requires non-empty stack.");
                }
                stack_.pop_back();
                break;
            }
            case OpCode::call:
            {
                if (instruction.operand >= program.functions.size())
                {
                    return make_unexpected(ErrorCode::invalid_function_index, "call operand out of range.");
                }

                const auto& function = program.functions[instruction.operand];
                if (function.local_count < function.arity)
                {
                    return make_unexpected(
                        ErrorCode::invalid_function_signature,
                        "Function local_count must be >= arity.");
                }

                if (function.entry >= program.code.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_function_index,
                        "Function entry points outside bytecode.");
                }

                if (stack_.size() < function.arity)
                {
                    return make_unexpected(
                        ErrorCode::stack_underflow,
                        "call does not have enough stack arguments.");
                }

                const std::size_t base = stack_.size() - function.arity;
                stack_.resize(base + function.local_count);
                call_frames_.push_back({pc + 1, base, function.local_count});
                pc = function.entry;
                advance_pc = false;
                break;
            }
            case OpCode::ret:
            {
                Result<Value> return_value_result = pop_value();
                if (!return_value_result.has_value())
                {
                    return std::unexpected(return_value_result.error());
                }

                Value return_value = std::move(return_value_result).value();
                if (call_frames_.empty())
                {
                    return return_value;
                }

                const CallFrame frame = call_frames_.back();
                call_frames_.pop_back();

                if (frame.base > stack_.size())
                {
                    return make_unexpected(
                        ErrorCode::missing_call_frame,
                        "Corrupted call frame base exceeds stack size.");
                }

                stack_.resize(frame.base);
                stack_.push_back(std::move(return_value));
                pc = frame.return_pc;
                advance_pc = false;
                break;
            }
            case OpCode::load_local:
            {
                if (call_frames_.empty())
                {
                    return make_unexpected(
                        ErrorCode::missing_call_frame,
                        "load_local requires an active call frame.");
                }

                const CallFrame& frame = call_frames_.back();
                const std::size_t local_index = static_cast<std::size_t>(instruction.operand);
                if (local_index >= frame.local_count)
                {
                    return make_unexpected(
                        ErrorCode::invalid_local_index,
                        "load_local operand out of range.");
                }

                const std::size_t stack_index = frame.base + local_index;
                if (stack_index >= stack_.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_local_index,
                        "load_local resolved stack index out of range.");
                }

                stack_.push_back(stack_[stack_index]);
                break;
            }
            case OpCode::store_local:
            {
                if (call_frames_.empty())
                {
                    return make_unexpected(
                        ErrorCode::missing_call_frame,
                        "store_local requires an active call frame.");
                }

                const CallFrame& frame = call_frames_.back();
                const std::size_t local_index = static_cast<std::size_t>(instruction.operand);
                if (local_index >= frame.local_count)
                {
                    return make_unexpected(
                        ErrorCode::invalid_local_index,
                        "store_local operand out of range.");
                }

                Result<Value> value_result = pop_value();
                if (!value_result.has_value())
                {
                    return std::unexpected(value_result.error());
                }

                const std::size_t stack_index = frame.base + local_index;
                if (stack_index >= stack_.size())
                {
                    return make_unexpected(
                        ErrorCode::invalid_local_index,
                        "store_local resolved stack index out of range.");
                }

                stack_[stack_index] = std::move(value_result).value();
                break;
            }
            case OpCode::call_native:
            {
                Result<Value> native_result = execute_call_native(instruction.operand);
                if (!native_result.has_value())
                {
                    return std::unexpected<Error> {native_result.error()};
                }
                stack_.push_back(std::move(native_result).value());
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
                return make_unexpected(ErrorCode::unknown_opcode, "Unknown opcode.");
            }
        }

        if (advance_pc)
        {
            ++pc;
        }
    }

    if (stack_.empty())
    {
        return Value {};
    }

    return pop_value();
}

auto VM::pop_value() -> Result<Value>
{
    if (stack_.empty())
    {
        return make_unexpected(ErrorCode::stack_underflow, "VM stack underflow.");
    }

    Value value = std::move(stack_.back());
    stack_.pop_back();
    return value;
}

auto VM::execute_add_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("add_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("add_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() + rhs_i64.value());
}

auto VM::execute_sub_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("sub_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("sub_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() - rhs_i64.value());
}

auto VM::execute_mul_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("mul_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("mul_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() * rhs_i64.value());
}

auto VM::execute_mod_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("mod_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("mod_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }
    if (rhs_i64.value() == 0)
    {
        return make_unexpected(ErrorCode::division_by_zero, "mod_i64 divisor cannot be zero.");
    }

    return Value::i64(lhs_i64.value() % rhs_i64.value());
}

auto VM::execute_cmp_eq_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("cmp_eq_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("cmp_eq_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() == rhs_i64.value() ? 1 : 0);
}

auto VM::execute_cmp_lt_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("cmp_lt_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("cmp_lt_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() < rhs_i64.value() ? 1 : 0);
}

auto VM::execute_and_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("and_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("and_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() & rhs_i64.value());
}

auto VM::execute_or_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("or_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("or_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() | rhs_i64.value());
}

auto VM::execute_xor_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("xor_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("xor_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }

    return Value::i64(lhs_i64.value() ^ rhs_i64.value());
}

auto VM::execute_shl_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("shl_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("shl_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }
    if (rhs_i64.value() < 0 || rhs_i64.value() > 63)
    {
        return make_unexpected(ErrorCode::invalid_shift_amount, "shl_i64 shift amount must be in [0, 63].");
    }

    return Value::i64(lhs_i64.value() << rhs_i64.value());
}

auto VM::execute_shr_i64() -> Result<Value>
{
    Result<Value> rhs_result = pop_value();
    if (!rhs_result.has_value())
    {
        return std::unexpected<Error> {rhs_result.error()};
    }

    Result<Value> lhs_result = pop_value();
    if (!lhs_result.has_value())
    {
        return std::unexpected<Error> {lhs_result.error()};
    }

    Value rhs = std::move(rhs_result).value();
    Value lhs = std::move(lhs_result).value();

    Result<std::int64_t> lhs_i64 = lhs.expect_i64("shr_i64 lhs");
    if (!lhs_i64.has_value())
    {
        return std::unexpected(lhs_i64.error());
    }
    Result<std::int64_t> rhs_i64 = rhs.expect_i64("shr_i64 rhs");
    if (!rhs_i64.has_value())
    {
        return std::unexpected(rhs_i64.error());
    }
    if (rhs_i64.value() < 0 || rhs_i64.value() > 63)
    {
        return make_unexpected(ErrorCode::invalid_shift_amount, "shr_i64 shift amount must be in [0, 63].");
    }

    return Value::i64(lhs_i64.value() >> rhs_i64.value());
}

auto VM::execute_call_native(std::size_t binding_index) -> Result<Value>
{
    if (binding_index >= native_bindings_.size())
    {
        return make_unexpected(ErrorCode::invalid_native_index, "call_native operand out of range.");
    }

    NativeBinding& binding = native_bindings_[binding_index];
    if (!binding.function)
    {
        return make_unexpected(ErrorCode::empty_native_binding, "Native function binding is empty.");
    }

    if (stack_.size() < binding.arity)
    {
        return make_unexpected(
            ErrorCode::insufficient_native_arguments,
            "call_native does not have enough stack arguments.");
    }

    const std::size_t args_offset = stack_.size() - binding.arity;
    std::span<Value> args(stack_.data() + args_offset, binding.arity);

    Result<Value> result = binding.function(*this, args);
    if (!result.has_value())
    {
        return std::unexpected<Error> {result.error()};
    }

    stack_.resize(args_offset);
    return std::move(result).value();
}
} // namespace stella::vm
