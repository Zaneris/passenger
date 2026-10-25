
int test = 4;
test = 5;

Func<int, int> myLambda = (int x) => x + test;
await Task.Delay(10);
