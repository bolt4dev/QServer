using Xunit;

// Run test classes SERIALLY, never in parallel.
//
// Several lifecycle classes (KillOnCloseJobTests, ScraperLifecycleTests, ScraperHostJobTests) spawn real
// FakeServer processes, and OrphanSweeper.Sweep kills EVERY process whose exe path matches — machine-wide.
// If OrphanSweeperTests ran concurrently with those classes it would reap their live FakeServers (and they
// could reap the sweep test's orphan), producing spurious cross-class failures and leaked processes. xunit
// parallelizes test collections by default, so this opt-out is load-bearing, not a mere performance knob.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
