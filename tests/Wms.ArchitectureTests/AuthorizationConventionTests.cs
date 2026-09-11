using System.Text.RegularExpressions;
using Xunit;

namespace Wms.ArchitectureTests;

/// <summary>
/// Invariant 8: authorisation is checked against permissions plus scope,
/// never against role names. <c>if (user.Role == "Supervisor")</c> becomes
/// unmaintainable within a year, and every new role then requires touching
/// every check.
///
/// This is a source scan rather than a NetArchTest rule, deliberately.
/// NetArchTest reasons about types and dependencies; a comparison between a
/// property and a string literal is not expressible in it. A Roslyn analyser
/// would be the more precise instrument — this is the cheap version that
/// fails the build today, and the guard-fact-path.sh hook flags the same
/// pattern at edit time.
/// </summary>
public sealed partial class AuthorizationConventionTests
{
    [GeneratedRegex(@"\.Role\s*(==|!=)|\.Role\s*\.\s*Equals\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex RoleComparison();

    [Fact]
    public void ProductionCode_NeverComparesAgainstARoleName()
    {
        string sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        List<string> violations = [];

        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Build output, not source.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (RoleComparison().IsMatch(lines[i]))
                {
                    violations.Add($"{Path.GetRelativePath(sourceRoot, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Authorisation compares a role name. Resolve permissions server-side and check "
            + "those, with warehouse/zone scope. Violations:" + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Walks up from the test assembly until it finds src/, so the scan does
    /// not depend on the build output layout.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "db", "migrations")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
