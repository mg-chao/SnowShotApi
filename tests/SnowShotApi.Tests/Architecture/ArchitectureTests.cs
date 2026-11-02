using System.Reflection;
using System.Xml.Linq;
using SnowShot.Application;
using SnowShot.Contracts;
using SnowShot.Domain;

namespace SnowShotApi.Tests.Architecture;

public sealed class ArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ProjectMatrix =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["SnowShot.Contracts"] = [],
            ["SnowShot.Domain"] = [],
            ["SnowShot.Application"] = ["SnowShot.Domain"],
            ["SnowShot.ApiAdapter"] = ["SnowShot.Application", "SnowShot.Contracts"],
            ["SnowShot.Infrastructure"] = ["SnowShot.Application", "SnowShot.Domain"],
            ["SnowShotApi"] = ["SnowShot.ApiAdapter", "SnowShot.Infrastructure"],
            ["SnowShot.DatabaseMigrator"] = ["SnowShot.Infrastructure"],
        };

    [Fact]
    public void ProductionProjectGraphExactlyMatchesTheArchitectureMatrix()
    {
        var repository = RepositoryRoot();
        var projects = Directory.GetFiles(Path.Combine(repository, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(value => Path.GetFileNameWithoutExtension(value)!, StringComparer.Ordinal);
        Assert.Equal(ProjectMatrix.Keys.Order(StringComparer.Ordinal), projects.Keys.Order(StringComparer.Ordinal));

        foreach (var (project, expectedReferences) in ProjectMatrix)
        {
            var document = XDocument.Load(projects[project]);
            var actual = document.Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => value is not null)
                .Select(value => Path.GetFileNameWithoutExtension(value!))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actual);
        }
    }

    [Theory]
    [MemberData(nameof(ProductionAssemblies))]
    public void LoadedAssemblyReferencesContainNoUnexpectedSnowShotEdges(Assembly assembly, string[] expected)
    {
        var actual = assembly.GetReferencedAssemblies().Select(value => value.Name!)
            .Where(value => value.StartsWith("SnowShot", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    [Theory]
    [InlineData(typeof(NanoYuan))]
    [InlineData(typeof(AppEnvelope))]
    [InlineData(typeof(ChatUseCase))]
    public void FrameworkIndependentProjectsDoNotReferenceForbiddenAssemblies(Type marker)
    {
        var references = marker.Assembly.GetReferencedAssemblies().Select(value => value.Name!).ToArray();
        Assert.DoesNotContain(references, IsForbiddenFrameworkAssembly);
    }

    [Fact]
    public void PublicApplicationSurfaceIsTransportNeutral()
    {
        var application = typeof(IOperationLedger).Assembly;
        var surface = application.GetExportedTypes()
            .Where(type => type.IsInterface || type.Name.EndsWith("Command", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods().Select(method => method.ReturnType))
                .Concat(type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType))))
            .SelectMany(Flatten)
            .Distinct()
            .ToArray();

        Assert.DoesNotContain(surface, type => type == typeof(Stream) || typeof(HttpContent).IsAssignableFrom(type));
        Assert.DoesNotContain(surface, type => IsForbiddenNamespace(type.Namespace));
    }

    public static TheoryData<Assembly, string[]> ProductionAssemblies() => new()
    {
        { typeof(AppEnvelope).Assembly, [] },
        { typeof(NanoYuan).Assembly, [] },
        { typeof(ChatUseCase).Assembly, ["SnowShot.Domain"] },
        { typeof(SnowShot.Api.AdapterComposition).Assembly, ["SnowShot.Application", "SnowShot.Contracts"] },
        { typeof(SnowShot.Infrastructure.DependencyInjection).Assembly, ["SnowShot.Application", "SnowShot.Domain"] },
        { typeof(Program).Assembly, ["SnowShot.ApiAdapter", "SnowShot.Infrastructure"] },
    };

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
            foreach (var nested in Flatten(element)) yield return nested;
        foreach (var argument in type.GetGenericArguments())
            foreach (var nested in Flatten(argument)) yield return nested;
    }

    private static bool IsForbiddenFrameworkAssembly(string name) =>
        name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
        name.StartsWith("StackExchange.Redis", StringComparison.Ordinal) ||
        name.StartsWith("Npgsql", StringComparison.Ordinal);

    private static bool IsForbiddenNamespace(string? value) => value is not null &&
        (value.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
         value.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
         value.StartsWith("StackExchange.Redis", StringComparison.Ordinal) ||
         value.StartsWith("Npgsql", StringComparison.Ordinal) ||
         value.StartsWith("SnowShot.Infrastructure", StringComparison.Ordinal));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SnowShotApi.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
