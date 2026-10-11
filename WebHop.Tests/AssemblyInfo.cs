// Test classes run in parallel; the tests of one class run one after another. Tests that touch
// process-wide environment variables (WEBHOP_AUTHTOKEN) are [NonParallelizable].
[assembly: Parallelizable(ParallelScope.Fixtures)]
