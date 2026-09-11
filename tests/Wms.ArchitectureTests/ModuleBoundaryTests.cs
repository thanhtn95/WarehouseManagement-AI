using System.Reflection;
using NetArchTest.Rules;
using Wms.Modules.Catalog;
using Wms.Modules.Identity;
using Wms.Modules.Inbound;
using Wms.Modules.Inventory;
using Wms.Modules.Platform;
using Wms.Modules.Tasks;
using Xunit;

namespace Wms.ArchitectureTests;

/// <summary>
/// The boundary rule, enforced by a failing build rather than a review
/// comment: a module may reference Wms.SharedKernel and other modules'
/// Contracts, and nothing else.
///
/// This is what keeps a modular monolith from quietly becoming a regular
/// one, and what makes a future extraction cheap. ADR 0001 records why the
/// boundary is test-enforced here rather than compiler-enforced by a
/// separate Contracts project per module.
/// </summary>
public sealed class ModuleBoundaryTests
{
    /// <summary>
    /// The folders inside a module that are private to it. Only Contracts is
    /// visible across the boundary.
    /// </summary>
    private static readonly string[] InternalSegments = ["Domain", "Application", "Infrastructure"];

    private static readonly (string Name, Assembly Assembly)[] Modules =
    [
        ("Identity", typeof(IdentityModule).Assembly),
        ("Catalog", typeof(CatalogModule).Assembly),
        ("Inventory", typeof(InventoryModule).Assembly),
        ("Tasks", typeof(TasksModule).Assembly),
        ("Inbound", typeof(InboundModule).Assembly),
        ("Platform", typeof(PlatformModule).Assembly),
    ];

    [Fact]
    public void Module_DoesNotReachIntoAnotherModulesInternals()
    {
        List<string> violations = [];

        foreach ((string name, Assembly assembly) in Modules)
        {
            string[] forbidden =
            [
                .. Modules
                    .Where(other => other.Name != name)
                    .SelectMany(other => InternalSegments
                        .Select(segment => $"Wms.Modules.{other.Name}.{segment}"))
            ];

            TestResult result = Types.InAssembly(assembly)
                .That().ResideInNamespaceStartingWith($"Wms.Modules.{name}")
                .ShouldNot().HaveDependencyOnAny(forbidden)
                .GetResult();

            if (!result.IsSuccessful)
            {
                violations.AddRange(
                    (result.FailingTypeNames ?? [])
                    .Select(type => $"{name}: {type} reaches into another module's internals"));
            }
        }

        Assert.True(
            violations.Count == 0,
            "Cross-module access must go through Contracts, or through the outbox for "
            + "effects. Violations:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Module_DoesNotDependOnAHostProcess()
    {
        List<string> violations = [];

        foreach ((string name, Assembly assembly) in Modules)
        {
            TestResult result = Types.InAssembly(assembly)
                .ShouldNot().HaveDependencyOnAny("Wms.Api", "Wms.Worker")
                .GetResult();

            if (!result.IsSuccessful)
            {
                violations.AddRange(
                    (result.FailingTypeNames ?? [])
                    .Select(type => $"{name}: {type} depends on a host process"));
            }
        }

        Assert.True(
            violations.Count == 0,
            "Hosts compose modules; a module never depends on the process hosting it. "
            + "Violations:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}
