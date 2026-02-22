module;

#include <print>
#include <string_view>

module log;

namespace logger
{
    void info(std::string_view message)
    {
        std::println("[log] {}", message);
    }
}
