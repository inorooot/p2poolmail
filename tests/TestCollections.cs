using Xunit;

namespace Tests;

/// <summary>
/// Settings.Current and EmailQueue._instance are process-wide statics that both
/// SettingsTests and SmtpPipelineTests mutate. xUnit runs test classes in
/// parallel by default, which would let one class swap Settings.Current while
/// the other is mid-assert. Grouping them into one collection serializes them.
/// </summary>
[CollectionDefinition("ConfigState")]
public sealed class ConfigStateCollection;
