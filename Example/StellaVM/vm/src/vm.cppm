module;

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

export module vm;


export namespace stella::vm
{
enum class OpCode : std::uint8_t
{
    push_constant = 0,
    push_input = 1,
    add_i64 = 2,
    call_native = 3,
    halt = 4
};

struct Instruction final
{
    OpCode opcode {};
    std::uint32_t operand = 0;
};

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
    using Storage = std::variant<std::monostate, std::int64_t, double, std::string_view, MoveBuffer>;

    Value() = default;
    explicit Value(std::int64_t integer) noexcept;
    explicit Value(double floating_point) noexcept;
    explicit Value(std::string_view borrowed_text) noexcept;
    explicit Value(MoveBuffer buffer) noexcept;

    Value(Value&&) noexcept = default;
    auto operator=(Value&&) noexcept -> Value& = default;
    Value(const Value& other);
    auto operator=(const Value& other) -> Value&;

    [[nodiscard]] static auto i64(std::int64_t integer) noexcept -> Value;
    [[nodiscard]] static auto f64(double floating_point) noexcept -> Value;
    [[nodiscard]] static auto borrowed_string(std::string_view borrowed_text) noexcept -> Value;
    [[nodiscard]] static auto owned_buffer(MoveBuffer buffer) noexcept -> Value;

    [[nodiscard]] auto is_empty() const noexcept -> bool;
    [[nodiscard]] auto is_i64() const noexcept -> bool;
    [[nodiscard]] auto is_f64() const noexcept -> bool;
    [[nodiscard]] auto is_string_view() const noexcept -> bool;
    [[nodiscard]] auto is_buffer() const noexcept -> bool;

    [[nodiscard]] auto as_i64() -> std::int64_t&;
    [[nodiscard]] auto as_i64() const -> const std::int64_t&;
    [[nodiscard]] auto as_f64() -> double&;
    [[nodiscard]] auto as_f64() const -> const double&;
    [[nodiscard]] auto as_string_view() -> std::string_view&;
    [[nodiscard]] auto as_string_view() const -> const std::string_view&;
    [[nodiscard]] auto as_buffer() -> MoveBuffer&;
    [[nodiscard]] auto as_buffer() const -> const MoveBuffer&;

    [[nodiscard]] auto take_buffer() -> MoveBuffer;

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

    std::vector<Instruction> code;
    std::vector<Value> constants;
};

class VM;
using NativeFunction = std::move_only_function<Value(VM&, std::span<Value>)>;

struct NativeBinding final
{
    std::string name;
    std::size_t arity = 0;
    NativeFunction function;
};

class VM final
{
public:
    explicit VM(std::size_t stack_reserve = 64, std::size_t arena_bytes = 4096);

    [[nodiscard]] auto bind_native(std::string name, std::size_t arity, NativeFunction function)
        -> std::size_t;
    [[nodiscard]] auto push_input(Value value) -> std::size_t;

    void clear_inputs();
    void clear_stack();

    [[nodiscard]] auto stack() noexcept -> std::span<Value>;
    [[nodiscard]] auto stack() const noexcept -> std::span<const Value>;
    [[nodiscard]] auto arena() noexcept -> Arena&;
    [[nodiscard]] auto arena() const noexcept -> const Arena&;

    [[nodiscard]] auto run(const Program& program) -> Value;

private:
    [[nodiscard]] auto pop_value() -> Value;
    [[nodiscard]] auto execute_add_i64() -> Value;
    [[nodiscard]] auto execute_call_native(std::size_t binding_index) -> Value;

    Arena arena_;
    std::vector<Value> stack_;
    std::vector<Value> inputs_;
    std::vector<NativeBinding> native_bindings_;
};
} // namespace stella::vm
