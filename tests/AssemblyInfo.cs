using Xunit;

// The test suite mutates process-wide static state via reflection:
//   - SettingsTests / SmtpPipelineTests replace Settings.Current and manage the
//     EmailQueue singleton
//   - NotificationTests reset Notification.Enabled / Slots
//   - StatsTests reset the Stats payout/share counters
//   - FileTailerTests drive NotifyManager.Handle, which reads Notification state
// xUnit runs different collections in parallel by default, which caused data
// races and flaky assertions between those classes. Parallelization is therefore
// disabled for the whole assembly; the suite runs in ~200 ms, so the cost is nil.
// The [Collection("GlobalState")] markers on the state-touching classes are kept:
// they document the dependency and keep those classes serialized with each other
// even if assembly-level parallelization is ever re-enabled. Classes without
// shared state (AhoCorasickTreeTests, CommonHelperJsonTests,
// ImapClientServiceTests) need no marker.
[assembly: CollectionBehavior(DisableTestParallelization = true)]