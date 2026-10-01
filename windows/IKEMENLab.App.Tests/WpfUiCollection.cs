using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// WPF allows exactly one Application per AppDomain, and Application.Current is only set once. Two test classes
/// running in parallel would each observe no Application and race to create one, so every UI test must share this
/// non-parallel collection and build it once.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection
{
    public const string Name = "WPF UI";
}
