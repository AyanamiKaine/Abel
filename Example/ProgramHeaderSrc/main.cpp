// app/main.cpp

// 1. Standard Library Includes
#include <print>     // C++23 formatted output
#include <exception> // For catching standard exceptions
#include <limits>    // For demonstrating the overflow

// 2. Third-Party / Project Library Includes
// Notice the angle brackets and the folder prefix! 
// We DO NOT use relative paths like #include "../include/my_math/math_operations.h".
// The build system (like CMake) is responsible for telling the compiler where to find this.
#include <math/math_operations.h>

// 3. What we CANNOT include:
// #include "math_internal.h" // ERROR: This file is completely invisible to main.cpp!

int main() {
    std::println("Starting the Math Application...");
    std::println("================================");

    // --- Standard Usage ---
    int x = 15;
    int y = 5;

    // main.cpp calls the public API. It has no idea that internally, 
    // my_math::add is calling details::causes_overflow() before doing the math.
    int sum = my_math::add(x, y);
    int diff = my_math::subtract(x, y);

    std::println("Standard Operations:");
    std::println("  {} + {} = {}", x, y, sum);
    std::println("  {} - {} = {}", x, y, diff);
    std::println("--------------------------------");

    // --- Handling Hidden Internal Logic ---
    // Let's force an integer overflow to see our hidden internal logic in action.
    int max_int = std::numeric_limits<int>::max();
    int dangerous_add = 10;

    std::println("Attempting a dangerous operation...");
    std::println("  Adding {} to the maximum possible integer ({}).", dangerous_add, max_int);

    // Because my_math::add can throw an exception (as defined in our .cpp file),
    // robust application code should wrap it in a try-catch block.
    try {
        int bad_sum = my_math::add(max_int, dangerous_add);
        
        // This line will never execute because the exception interrupts the flow.
        std::println("  Result: {}", bad_sum); 
    } 
    catch (const std::overflow_error& e) {
        // We gracefully catch the error thrown by the library's internal checks.
        std::println("  [ERROR CAUGHT]: {}", e.what());
    } 
    catch (const std::exception& e) {
        // A generic catch-all for any other standard exceptions.
        std::println("  [GENERIC ERROR]: {}", e.what());
    }

    std::println("================================");
    std::println("Application finished successfully.");
    
    return 0;
}