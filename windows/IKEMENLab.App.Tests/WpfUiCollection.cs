using Xunit;

namespace IKEMENLab.App.Tests;

/// <summary>
/// Every WPF UI test class belongs to this collection. WPF allows one <see cref="System.Windows.Application"/> per process and pins
/// windows to the thread that made them, so these tests must not run in parallel with each other (xUnit runs different classes in
/// parallel by default, which made two classes race to create the Application).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection
{
    public const string Name = "WPF UI";
}
