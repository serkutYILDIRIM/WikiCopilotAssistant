using System.Xml.Linq;

namespace WikiCopilotAssistant.UnitTests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Domain", new string[0])]
    [InlineData("Application", new[] { "Domain" })]
    [InlineData("Infrastructure", new[] { "Application", "Domain" })]
    [InlineData("Web", new[] { "Application", "Infrastructure" })]
    public void ProjectReferences_ForEachLayer_FollowDependencyDirection(
        string layer,
        string[] expectedDependencies)
    {
        var project = LoadProject(layer);
        var references = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(
                Assert.IsType<string>(reference.Attribute("Include")?.Value)
                    .Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = expectedDependencies
            .Select(name => $"WikiCopilotAssistant.{name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, references);
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void InnerLayer_HasNoInfrastructurePackages(string layer)
    {
        var project = LoadProject(layer);

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    private static XDocument LoadProject(string layer) =>
        XDocument.Load(Path.Combine(
            AppContext.BaseDirectory, "Architecture", $"WikiCopilotAssistant.{layer}.csproj"));
}
