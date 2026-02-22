module math;

import log;

int Calculator::multiply(int a, int b)
{
    logger::info("multiply");
    return a * b;
}

int Calculator::add(int a, int b)
{
    logger::info("add");
    return a + b;
}
