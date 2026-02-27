module;

#include <cstddef>
#include <cstdint>
#include <expected>
#include <functional>
#include <memory>
#include <memory_resource>
#include <array>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <tuple>
#include <type_traits>
#include <utility>
#include <variant>
#include <vector>

export module vm;


export namespace stella::vm
{
enum class OpCode : std::uint8_t
{
    push_constant = 0,
    push_input = 1,
    add_i64 = 2,
    call_native = 3,
    halt = 4,
    sub_i64 = 5,
    mul_i64 = 6,
    mod_i64 = 7,
    cmp_eq_i64 = 8,
    cmp_lt_i64 = 9,
    jump = 10,
    jump_if_true = 11,
    dup = 12,
    pop = 13,
    call = 14,
    ret = 15,
    load_local = 16,
    store_local = 17,
    and_i64 = 18,
    or_i64 = 19,
    xor_i64 = 20,
    shl_i64 = 21,
    shr_i64 = 22
};

struct Instruction final
{
    OpCode opcode {};
    std::uint32_t operand = 0;
};

enum class ErrorCode : std::uint8_t
{
    type_mismatch = 0,
    invalid_buffer_access = 1,
    invalid_constant_index = 2,
    invalid_input_index = 3,
    stack_underflow = 4,
    invalid_native_index = 5,
    empty_native_binding = 6,
    insufficient_native_arguments = 7,
    unknown_opcode = 8,
    division_by_zero = 9,
    invalid_jump_target = 10,
    verification_failed = 11,
    invalid_function_index = 12,
    invalid_local_index = 13,
    missing_call_frame = 14,
    step_budget_exceeded = 15,
    invalid_function_signature = 16,
    invalid_shift_amount = 17,
    invalid_bytecode_magic = 18,
    unsupported_bytecode_version = 19,
    malformed_bytecode = 20
};

struct Error final
{
    ErrorCode code = ErrorCode::unknown_opcode;
    std::string message;
};

template <typename T>
using Result = std::expected<T, Error>;

using VoidResult = std::expected<void, Error>;

struct MoveBuffer final
{
    std::unique_ptr<std::byte[]> data {};
    std::size_t size = 0;

    MoveBuffer() = default;
    explicit MoveBuffer(std::size_t byte_count);
    MoveBuffer(std::unique_ptr<std::byte[]> bytes, std::size_t byte_count) noexcept;

    MoveBuffer(MoveBuffer&&) noexcept = default;
    auto operator=(MoveBuffer&&) noexcept -> MoveBuffer& = default;

    MoveBuffer(const MoveBuffer&) = delete;
    auto operator=(const MoveBuffer&) -> MoveBuffer& = delete;

    [[nodiscard]] auto bytes() noexcept -> std::span<std::byte>;
    [[nodiscard]] auto bytes() const noexcept -> std::span<const std::byte>;
    [[nodiscard]] auto data_ptr() noexcept -> std::byte*;
    [[nodiscard]] auto data_ptr() const noexcept -> const std::byte*;
};

class Value final
{
public:
    enum class Kind : std::uint8_t
    {
        empty = 0,
        i64 = 1,
        f64 = 2,
        borrowed_string = 3,
        owned_string = 4,
        buffer = 5
    };

    using Storage = std::variant<std::monostate, std::int64_t, double, std::string_view, std::string, MoveBuffer>;

    Value() = default;
    explicit Value(std::int64_t integer) noexcept;
    explicit Value(double floating_point) noexcept;
    explicit Value(std::string_view borrowed_text) noexcept;
    explicit Value(std::string owned_text) noexcept;
    explicit Value(MoveBuffer buffer) noexcept;

    Value(Value&&) noexcept = default;
    auto operator=(Value&&) noexcept -> Value& = default;
    Value(const Value& other);
    auto operator=(const Value& other) -> Value&;

    [[nodiscard]] static auto i64(std::int64_t integer) noexcept -> Value;
    [[nodiscard]] static auto f64(double floating_point) noexcept -> Value;
    [[nodiscard]] static auto borrowed_string(std::string_view borrowed_text) noexcept -> Value;
    [[nodiscard]] static auto owned_string(std::string owned_text) noexcept -> Value;
    [[nodiscard]] static auto owned_buffer(MoveBuffer buffer) noexcept -> Value;

    [[nodiscard]] auto kind() const noexcept -> Kind;
    [[nodiscard]] static auto kind_name(Kind kind) noexcept -> std::string_view;

    [[nodiscard]] auto is_empty() const noexcept -> bool;
    [[nodiscard]] auto is_i64() const noexcept -> bool;
    [[nodiscard]] auto is_f64() const noexcept -> bool;
    [[nodiscard]] auto is_string_view() const noexcept -> bool;
    [[nodiscard]] auto is_owned_string() const noexcept -> bool;
    [[nodiscard]] auto is_string() const noexcept -> bool;
    [[nodiscard]] auto is_buffer() const noexcept -> bool;

    [[nodiscard]] auto as_i64() -> std::int64_t&;
    [[nodiscard]] auto as_i64() const -> const std::int64_t&;
    [[nodiscard]] auto as_f64() -> double&;
    [[nodiscard]] auto as_f64() const -> const double&;
    [[nodiscard]] auto as_string_view() -> std::string_view&;
    [[nodiscard]] auto as_string_view() const -> const std::string_view&;
    [[nodiscard]] auto as_owned_string() -> std::string&;
    [[nodiscard]] auto as_owned_string() const -> const std::string&;
    [[nodiscard]] auto as_buffer() -> MoveBuffer&;
    [[nodiscard]] auto as_buffer() const -> const MoveBuffer&;

    [[nodiscard]] auto expect_i64(std::string_view context) const -> Result<std::int64_t>;
    [[nodiscard]] auto expect_string(std::string_view context) const -> Result<std::string_view>;
    [[nodiscard]] auto take_buffer() -> Result<MoveBuffer>;

private:
    Storage storage_ {};
};

class Arena final
{
public:
    class Marker final
    {
    public:
        Marker() = default;
        ~Marker();

        Marker(Marker&& other) noexcept;
        auto operator=(Marker&& other) noexcept -> Marker&;

        Marker(const Marker&) = delete;
        auto operator=(const Marker&) -> Marker& = delete;

        void release() noexcept;

    private:
        friend class Arena;
        Marker(Arena* arena, std::size_t rewind_to) noexcept;

        Arena* arena_ = nullptr;
        std::size_t rewind_to_ = 0;
    };

    explicit Arena(std::size_t initial_bytes = 4096);
    ~Arena();

    Arena(const Arena&) = delete;
    auto operator=(const Arena&) -> Arena& = delete;
    Arena(Arena&&) = delete;
    auto operator=(Arena&&) -> Arena& = delete;

    [[nodiscard]] auto mark() noexcept -> Marker;
    void reset() noexcept;
    [[nodiscard]] auto live_allocations() const noexcept -> std::size_t;

    template <typename T, typename... Args>
    auto emplace(Args&&... args) -> T*
    {
        void* memory = allocate_bytes(sizeof(T), alignof(T));
        T* object = new (memory) T(std::forward<Args>(args)...);
        register_destructor(object, [](void* ptr) noexcept {
            auto* typed = static_cast<T*>(ptr);
            typed->~T();
        });
        return object;
    }

private:
    void* allocate_bytes(std::size_t size, std::size_t alignment);
    void register_destructor(void* object, void (*destroy)(void*) noexcept);
    void rewind(std::size_t index) noexcept;

    struct TrackedAllocation final
    {
        void* object = nullptr;
        void (*destroy)(void*) noexcept = nullptr;
        bool alive = false;
    };

    std::vector<std::byte> initial_buffer_;
    std::pmr::monotonic_buffer_resource resource_;
    std::vector<TrackedAllocation> tracked_allocations_;
};

class Program final
{
public:
    [[nodiscard]] auto add_constant(Value value) -> std::size_t;
    [[nodiscard]] auto add_function(std::uint32_t entry, std::uint32_t arity, std::uint32_t local_count)
        -> std::size_t;

    std::vector<Instruction> code;
    std::vector<Value> constants;

    struct Function final
    {
        std::uint32_t entry = 0;
        std::uint32_t arity = 0;
        std::uint32_t local_count = 0;
    };

    std::vector<Function> functions;
};

inline constexpr std::uint32_t bytecode_magic = 0x5354564DU;
inline constexpr std::uint16_t bytecode_version = 1;

[[nodiscard]] auto serialize_program(const Program& program) -> Result<MoveBuffer>;
[[nodiscard]] auto deserialize_program(std::span<const std::byte> bytes) -> Result<Program>;

struct TraceEvent final
{
    std::size_t pc = 0;
    OpCode opcode = OpCode::halt;
    std::size_t stack_size = 0;
    std::size_t call_depth = 0;
};

struct ProfileStats final
{
    std::uint64_t runs = 0;
    std::uint64_t executed_steps = 0;
    std::uint64_t total_run_nanoseconds = 0;
    std::array<std::uint64_t, 256> opcode_counts {};
    std::array<std::uint64_t, 256> opcode_nanoseconds {};
};

class VM;
using NativeFunction = std::move_only_function<Result<Value>(VM&, std::span<Value>)>;

namespace native_detail
{
template <typename T>
inline constexpr bool dependent_false_v = false;

template <typename T>
struct CallableTraits : CallableTraits<decltype(&std::remove_cvref_t<T>::operator())>
{
};

template <typename C, typename R, typename... Args>
struct CallableTraits<R (C::*)(Args...) const>
{
    using Return = R;
    using ArgsTuple = std::tuple<Args...>;
    static constexpr bool has_vm_first = sizeof...(Args) > 0 &&
                                         std::is_same_v<std::remove_cvref_t<std::tuple_element_t<0, ArgsTuple>>, VM>;
    static constexpr std::size_t arity = sizeof...(Args) - (has_vm_first ? 1 : 0);
};

template <typename C, typename R, typename... Args>
struct CallableTraits<R (C::*)(Args...)>
{
    using Return = R;
    using ArgsTuple = std::tuple<Args...>;
    static constexpr bool has_vm_first = sizeof...(Args) > 0 &&
                                         std::is_same_v<std::remove_cvref_t<std::tuple_element_t<0, ArgsTuple>>, VM>;
    static constexpr std::size_t arity = sizeof...(Args) - (has_vm_first ? 1 : 0);
};

template <typename R, typename... Args>
struct CallableTraits<R (*)(Args...)>
{
    using Return = R;
    using ArgsTuple = std::tuple<Args...>;
    static constexpr bool has_vm_first = sizeof...(Args) > 0 &&
                                         std::is_same_v<std::remove_cvref_t<std::tuple_element_t<0, ArgsTuple>>, VM>;
    static constexpr std::size_t arity = sizeof...(Args) - (has_vm_first ? 1 : 0);
};

template <typename Tuple>
struct TupleTail
{
    using type = std::tuple<>;
};

template <typename Head, typename... Tail>
struct TupleTail<std::tuple<Head, Tail...>>
{
    using type = std::tuple<Tail...>;
};

template <typename T>
using TupleTailT = typename TupleTail<T>::type;

template <typename Tuple>
struct TupleDecay
{
    using type = std::tuple<>;
};

template <typename... Ts>
struct TupleDecay<std::tuple<Ts...>>
{
    using type = std::tuple<std::remove_cvref_t<Ts>...>;
};

template <typename Tuple>
using TupleDecayT = typename TupleDecay<Tuple>::type;

template <typename Arg>
auto decode_arg(Value& value, std::string_view context) -> Result<std::remove_cvref_t<Arg>>
{
    using T = std::remove_cvref_t<Arg>;
    if constexpr (std::is_same_v<T, std::int64_t>)
    {
        return value.expect_i64(context);
    }
    else if constexpr (std::is_same_v<T, double>)
    {
        if (!value.is_f64())
        {
            return std::unexpected(Error {
                ErrorCode::type_mismatch,
                std::string(context) + " expected f64 but got " + std::string(Value::kind_name(value.kind())) + ".",
            });
        }
        return value.as_f64();
    }
    else if constexpr (std::is_same_v<T, std::string_view>)
    {
        return value.expect_string(context);
    }
    else if constexpr (std::is_same_v<T, std::string>)
    {
        auto text = value.expect_string(context);
        if (!text.has_value())
        {
            return std::unexpected(text.error());
        }
        return std::string(text.value());
    }
    else if constexpr (std::is_same_v<T, MoveBuffer>)
    {
        return value.take_buffer();
    }
    else if constexpr (std::is_same_v<T, Value>)
    {
        return value;
    }
    else
    {
        static_assert(dependent_false_v<T>, "Unsupported native binder argument type.");
    }
}

template <typename Ret>
auto encode_return(Ret&& value) -> Result<Value>
{
    using T = std::remove_cvref_t<Ret>;
    if constexpr (std::is_same_v<T, Result<Value>>)
    {
        return std::forward<Ret>(value);
    }
    else if constexpr (std::is_same_v<T, Value>)
    {
        return std::forward<Ret>(value);
    }
    else if constexpr (std::is_same_v<T, std::int64_t>)
    {
        return Value::i64(value);
    }
    else if constexpr (std::is_same_v<T, double>)
    {
        return Value::f64(value);
    }
    else if constexpr (std::is_same_v<T, std::string_view>)
    {
        return Value::borrowed_string(value);
    }
    else if constexpr (std::is_same_v<T, std::string>)
    {
        return Value::owned_string(std::move(value));
    }
    else if constexpr (std::is_same_v<T, MoveBuffer>)
    {
        return Value::owned_buffer(std::move(value));
    }
    else if constexpr (std::is_void_v<T>)
    {
        return Value {};
    }
    else
    {
        static_assert(dependent_false_v<T>, "Unsupported native binder return type.");
    }
}
} // namespace native_detail

struct NativeBinding final
{
    std::string name;
    std::size_t arity = 0;
    NativeFunction function;
};

class NativeBindingBuilder;

class VM final
{
public:
    explicit VM(std::size_t stack_reserve = 64, std::size_t arena_bytes = 4096);

    [[nodiscard]] auto bind_native(std::string name, std::size_t arity, NativeFunction function)
        -> std::size_t;
    [[nodiscard]] auto native(std::string name) -> NativeBindingBuilder;
    [[nodiscard]] auto push_input(Value value) -> std::size_t;

    void clear_inputs();
    void clear_stack();

    [[nodiscard]] auto stack() noexcept -> std::span<Value>;
    [[nodiscard]] auto stack() const noexcept -> std::span<const Value>;
    [[nodiscard]] auto arena() noexcept -> Arena&;
    [[nodiscard]] auto arena() const noexcept -> const Arena&;
    void set_step_budget(std::size_t max_steps) noexcept;
    void clear_step_budget() noexcept;
    void set_trace_sink(std::move_only_function<void(const TraceEvent&)> trace_sink);
    void clear_trace_sink();
    void set_profiling_enabled(bool enabled) noexcept;
    [[nodiscard]] auto profiling_enabled() const noexcept -> bool;
    void reset_profile() noexcept;
    [[nodiscard]] auto profile() const noexcept -> const ProfileStats&;

    [[nodiscard]] auto verify(const Program& program, std::size_t available_inputs) const -> VoidResult;
    [[nodiscard]] auto run(const Program& program) -> Result<Value>;
    [[nodiscard]] auto run_unchecked(const Program& program) -> Result<Value>;

private:
    [[nodiscard]] auto pop_value() -> Result<Value>;
    [[nodiscard]] auto execute_add_i64() -> Result<Value>;
    [[nodiscard]] auto execute_sub_i64() -> Result<Value>;
    [[nodiscard]] auto execute_mul_i64() -> Result<Value>;
    [[nodiscard]] auto execute_mod_i64() -> Result<Value>;
    [[nodiscard]] auto execute_cmp_eq_i64() -> Result<Value>;
    [[nodiscard]] auto execute_cmp_lt_i64() -> Result<Value>;
    [[nodiscard]] auto execute_and_i64() -> Result<Value>;
    [[nodiscard]] auto execute_or_i64() -> Result<Value>;
    [[nodiscard]] auto execute_xor_i64() -> Result<Value>;
    [[nodiscard]] auto execute_shl_i64() -> Result<Value>;
    [[nodiscard]] auto execute_shr_i64() -> Result<Value>;
    [[nodiscard]] auto execute_call_native(std::size_t binding_index) -> Result<Value>;

    Arena arena_;
    std::vector<Value> stack_;
    std::vector<Value> inputs_;
    std::vector<NativeBinding> native_bindings_;

    struct CallFrame final
    {
        std::size_t return_pc = 0;
        std::size_t base = 0;
        std::size_t local_count = 0;
    };

    std::vector<CallFrame> call_frames_;
    std::size_t step_budget_ = 0;
    std::move_only_function<void(const TraceEvent&)> trace_sink_;
    bool profiling_enabled_ = false;
    ProfileStats profile_stats_ {};
};

class NativeBindingBuilder final
{
public:
    NativeBindingBuilder(VM& vm, std::string name);

    auto arity(std::size_t expected_arity) -> NativeBindingBuilder&;

    template <typename Fn>
    auto bind(Fn function) -> std::size_t
    {
        using Traits = native_detail::CallableTraits<Fn>;
        using FullArgsTuple = typename Traits::ArgsTuple;
        using RawScriptArgsTuple =
            std::conditional_t<Traits::has_vm_first, native_detail::TupleTailT<FullArgsTuple>, FullArgsTuple>;
        using ScriptArgsTuple = native_detail::TupleDecayT<RawScriptArgsTuple>;

        static_assert(
            std::tuple_size_v<RawScriptArgsTuple> == Traits::arity,
            "Script arg tuple size mismatch.");

        constexpr std::size_t script_arity = Traits::arity;
        if (explicit_arity_.has_value() && explicit_arity_.value() != script_arity)
        {
            const std::size_t declared_arity = explicit_arity_.value();
            NativeFunction mismatch = [binding_name = name_, declared_arity, inferred_arity = script_arity](
                                          VM&,
                                          std::span<Value>) -> Result<Value> {
                return std::unexpected(Error {
                    ErrorCode::invalid_function_signature,
                    "native binding '" + binding_name + "' declared arity " + std::to_string(declared_arity) +
                        " but inferred " + std::to_string(inferred_arity) + ".",
                });
            };
            return vm_->bind_native(std::move(name_), declared_arity, std::move(mismatch));
        }

        const std::size_t final_arity = explicit_arity_.value_or(script_arity);

        NativeFunction wrapped =
            [fn = std::move(function), binding_name = name_, final_arity](VM& vm, std::span<Value> args) mutable
            -> Result<Value> {
            if (args.size() != final_arity)
            {
                return std::unexpected(Error {
                    ErrorCode::insufficient_native_arguments,
                    "native binding '" + binding_name + "' received wrong arity.",
                });
            }

            ScriptArgsTuple decoded {};
            bool decode_ok = true;
            Error decode_error {};

            [&]<std::size_t... I>(std::index_sequence<I...>) {
                ((
                     [&] {
                         if (!decode_ok)
                         {
                             return;
                         }

                         using Arg = std::tuple_element_t<I, ScriptArgsTuple>;
                         auto decoded_arg = native_detail::decode_arg<Arg>(
                             args[I],
                             "native " + binding_name + " arg[" + std::to_string(I) + "]");
                         if (!decoded_arg.has_value())
                         {
                             decode_ok = false;
                             decode_error = decoded_arg.error();
                             return;
                         }

                         std::get<I>(decoded) = std::move(decoded_arg).value();
                     }()),
                 ...);
            }(std::make_index_sequence<script_arity> {});

            if (!decode_ok)
            {
                return std::unexpected(decode_error);
            }

            if constexpr (Traits::has_vm_first)
            {
                auto returned = std::apply(
                    [&](auto&&... unpacked) { return fn(vm, std::forward<decltype(unpacked)>(unpacked)...); },
                    std::move(decoded));
                return native_detail::encode_return(std::move(returned));
            }
            else
            {
                auto returned = std::apply(
                    [&](auto&&... unpacked) { return fn(std::forward<decltype(unpacked)>(unpacked)...); },
                    std::move(decoded));
                return native_detail::encode_return(std::move(returned));
            }
        };

        return vm_->bind_native(std::move(name_), final_arity, std::move(wrapped));
    }

private:
    VM* vm_ = nullptr;
    std::string name_ {};
    std::optional<std::size_t> explicit_arity_ {};
};
} // namespace stella::vm
