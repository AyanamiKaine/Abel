// src/math_operations.cpp

// 1. Include the public interface it needs to implement
#include <math/math_operations.h>

// 2. Include the hidden, private tools using quotes (since it's in the same local src/ directory)
#include "math_internal.h"

// 3. Include any standard library headers needed for the implementation
#include <stdexcept>

namespace my_math {

    // The user just calls add(x, y). They don't see the complex checks happening here.
    int add(int a, int b) {
        // Utilizing the hidden helper function from math_internal.h
        if (details::causes_overflow(a, b)) {
            // In C++23, you might handle this with std::expected, but for this 
            // example, throwing a standard exception works perfectly.
            throw std::overflow_error("Addition resulted in integer overflow");
        }
        
        return a + b;
    }

    int subtract(int a, int b) {
        // Implementation for subtract...
        return a - b;
    }

    int multiply(int a, int b) {
        // Implementation for multiply...
        return a * b;
    }
}