// src/math_internal.h
// THIS IS A PRIVATE HEADER. Do not install or ship this to users.

#pragma once

#include <limits>
#include <stdexcept>

// Best Practice: Wrap internal logic in a nested "details" or "internal" namespace.
// This acts as a secondary warning to other developers not to use these functions directly.
namespace my_math::details {

    // A private helper function to check for integer overflow before adding.
    // We use C++ constexpr so it can be evaluated at compile-time if needed.
    constexpr bool causes_overflow(int a, int b) {
        if (b > 0 && a > std::numeric_limits<int>::max() - b) {
            return true;
        }
        if (b < 0 && a < std::numeric_limits<int>::min() - b) {
            return true;
        }
        return false;
    }

    // A private constant used internally by the library algorithms.
    constexpr int INTERNAL_MATH_THRESHOLD = 1024;

}