// math_operations.h

// #pragma once is a non-standard but universally supported include guard.
// It prevents the compiler from including this file more than once per translation unit,
// which avoids redefinition errors.
#pragma once

// It is best practice to wrap your library in a namespace to prevent name collisions
namespace my_math {

    // Function declarations (prototypes)
    // We are telling the compiler: "These functions exist and take these arguments, 
    // but the actual logic is somewhere else."
    
    int add(int a, int b);
    
    int subtract(int a, int b);
    
    int multiply(int a, int b);

    // Note: If we were writing a template function or a C++20/23 'constexpr' function, 
    // the implementation would typically need to stay in the header file. 
    // Standard functions, however, go in the .cpp file.
}