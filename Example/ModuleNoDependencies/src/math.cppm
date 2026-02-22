export module math; // Declares this file as the primary interface for the 'math' module

// Exporting a class
export class Calculator {
public:
    Calculator() = default;
    int multiply(int a, int b);
    int add(int a, int b);
};
