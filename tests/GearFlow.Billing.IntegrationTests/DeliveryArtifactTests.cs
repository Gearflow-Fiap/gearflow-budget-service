using YamlDotNet.RepresentationModel;

namespace GearFlow.Billing.IntegrationTests;

public class DeliveryArtifactTests
{
    private static string Root()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "GearFlow.Billing.sln"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    [Fact]
    public void Kubernetes_and_workflow_YAML_parse_and_base_references_existing_resources()
    {
        var root = Root();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "k8s"), "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"))
            .Concat(new[] { Path.Combine(root, "docker-compose.yml"), Path.Combine(root, "docker/otel/config.yaml") }))
        {
            var stream = new YamlStream(); stream.Load(new StringReader(File.ReadAllText(file)));
            Assert.NotEmpty(stream.Documents); Assert.All(stream.Documents, doc => Assert.IsType<YamlMappingNode>(doc.RootNode));
        }
        var kustomization = new YamlStream(); kustomization.Load(new StringReader(File.ReadAllText(Path.Combine(root, "k8s/base/kustomization.yaml"))));
        var map = (YamlMappingNode)kustomization.Documents[0].RootNode;
        foreach (var resource in ((YamlSequenceNode)map.Children[new YamlScalarNode("resources")]).Children.Cast<YamlScalarNode>())
            Assert.True(File.Exists(Path.Combine(root, "k8s/base", resource.Value!)), resource.Value);
    }
    [Fact]
    public void Deployment_has_two_replicas_probes_limits_and_no_embedded_secrets()
    {
        var stream = new YamlStream(); stream.Load(new StringReader(File.ReadAllText(Path.Combine(Root(), "k8s/base/deployment.yaml"))));
        var document = (YamlMappingNode)stream.Documents[0].RootNode; var spec = (YamlMappingNode)document.Children[new YamlScalarNode("spec")];
        Assert.Equal("2", ((YamlScalarNode)spec.Children[new YamlScalarNode("replicas")]).Value);
        var template = (YamlMappingNode)spec.Children[new YamlScalarNode("template")];
        var pod = (YamlMappingNode)template.Children[new YamlScalarNode("spec")];
        var container = (YamlMappingNode)((YamlSequenceNode)pod.Children[new YamlScalarNode("containers")]).Children[0];
        foreach (var key in new[] { "startupProbe", "livenessProbe", "readinessProbe", "resources", "envFrom", "securityContext" })
            Assert.True(container.Children.ContainsKey(new YamlScalarNode(key)), key);
        Assert.DoesNotContain("password:", File.ReadAllText(Path.Combine(Root(), "k8s/base/configmap.yaml")), StringComparison.OrdinalIgnoreCase);
    }
}
