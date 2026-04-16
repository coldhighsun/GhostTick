using GhostTick.Examples;

// Run all examples in sequence.
// Each example is self-contained and can be copied independently.

await Ex01BasicTicker.RunAsync();
await Ex02DriftMeasurement.RunAsync();
await Ex03SlowConsumer.RunAsync();
await Ex04CancellationAndStop.RunAsync();
await Ex05MultipleConsumers.RunAsync();
await Ex06CustomOptions.RunAsync();

Console.WriteLine("All examples completed.");
