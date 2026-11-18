using Xunit;

// CustomWebApplicationFactory sets process-wide environment variables
// (ConnectionStrings__Default, Jwt__SigningKey) to get config overrides seen
// by Program.cs's eager startup reads — see CustomWebApplicationFactory for
// why. That's process-global state, so test classes using different factory
// instances/containers must not run concurrently against it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
