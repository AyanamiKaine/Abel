module;

#include <string_view>

export module log;

export namespace logger
{
    void info(std::string_view message);
}
