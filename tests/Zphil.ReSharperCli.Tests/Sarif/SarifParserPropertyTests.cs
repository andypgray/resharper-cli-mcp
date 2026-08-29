using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Tests.Sarif;

/// <summary>
///     The parser's one structural promise, over generated reports rather than the eight fixtures on disk:
///     every result carrying a first location becomes exactly one issue, in document order across every run,
///     and every result without one is dropped. The fixtures each pin a single shape; what they cannot show
///     is the shapes <em>interleaved</em> — a located result after two dropped ones, a run that contributes
///     nothing sitting between two that do — which is where an off-by-one in the flattening would live.
/// </summary>
public sealed class SarifParserPropertyTests
{
    /// <summary>
    ///     Characters that make a URI awkward or outright illegal: host delimiters, reserved marks, whitespace,
    ///     non-ASCII.
    /// </summary>
    private static readonly char[] HostileCharacters =
        ['a', 'Z', '0', '/', '\\', ':', '[', ']', '|', '^', '%', '?', '#', '@', '.', ' ', 'é'];

    [Property]
    public Property Parse_AnyStructurallyValidReport_FlattensEveryLocatedResultInOrder()
    {
        return Prop.ForAll(
            ReportShape().ToArbitrary(),
            runs =>
            {
                // Arrange
                (string json, IReadOnlyList<string> locatedRuleIds) = BuildReport(runs);

                // Act
                List<InspectIssue> issues = SarifParser.Parse(json);

                // Assert — rule ids rather than file paths: the ids carry document order, and a path would
                // drag a platform-dependent LocalPath into an assertion that is not about paths at all.
                issues.Select(issue => issue.RuleId).ShouldBe(
                    locatedRuleIds,
                    $"The report has {locatedRuleIds.Count} located results across {runs.Count} run(s); each "
                    + "must become one issue, in this order, and every result without a first location must "
                    + "be dropped.");
            });
    }

    [Property]
    public Property Parse_UriTheRuntimeCannotParse_ReportsTheResultRatherThanThrowing()
    {
        return Prop.ForAll(
            HostileUri().ToArbitrary(),
            uri =>
            {
                // Arrange
                string json = SingleResultReport(uri);

                // Act
                List<InspectIssue> issues = Should.NotThrow(() => SarifParser.Parse(json));

                // Assert — what the file path becomes is deliberately unstated: a parseable file:// URI turns
                // into a platform-dependent local path, and an unparseable one cannot. What must hold either
                // way is that the result survives, because the alternative is one malformed URI discarding a
                // whole report the caller waited minutes for.
                issues.ShouldHaveSingleItem().RuleId.ShouldBe(
                    "OnlyResult",
                    $"A result located at \"{uri}\" must still be reported. jb's URI is someone else's output, "
                    + "so a shape the runtime rejects is bad input to be passed through — not a fault in this "
                    + "server, which is what an escaping exception would report it as.");
            });
    }

    /// <summary>
    ///     URIs at and past the edge of what <see cref="Uri" /> accepts. The curated shapes are drawn on every
    ///     seed rather than waited for: an authority-less <c>file://</c>, invalid host characters, a malformed
    ///     IPv6 literal, and an embedded null are the forms that are cheap to hit deliberately and unlikely to
    ///     be assembled at random.
    /// </summary>
    private static Gen<string> HostileUri()
    {
        Gen<string> curated = Gen.Elements(
            "file://",
            "file://h^ost/p",
            "file://[zz]/",
            "file://a|b",
            "file://\u0000",
            "file:",
            "");

        Gen<string> tail = Gen.Choose(0, 12)
            .SelectMany(length => Gen.Elements(HostileCharacters).ListOf(length))
            .Select(characters => new string(characters.ToArray()));

        Gen<string> assembled = Gen.Elements("file://", "file:///", "file:", "", "http://")
            .SelectMany(_ => tail, (scheme, rest) => scheme + rest);

        return Gen.OneOf(curated, assembled);
    }

    /// <summary>A single located result at <paramref name="uri" /> — the smallest report that reaches the URI conversion.</summary>
    private static string SingleResultReport(string uri)
    {
        Dictionary<string, object?> result = new()
        {
            ["ruleId"] = "OnlyResult",
            ["level"] = "warning",
            ["message"] = new Dictionary<string, object?> { ["text"] = "" },
            ["locations"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["physicalLocation"] = new Dictionary<string, object?>
                    {
                        ["artifactLocation"] = new Dictionary<string, object?> { ["uri"] = uri }
                    }
                }
            }
        };

        var report = new Dictionary<string, object?>
        {
            ["runs"] = new List<Dictionary<string, object?>>
            {
                new() { ["results"] = new List<Dictionary<string, object?>> { result } }
            }
        };

        return JsonSerializer.Serialize(report);
    }

    /// <summary>
    ///     A report as a list of runs, each a list of result shapes. Both are bounded rather than left to the
    ///     generator's size: nesting two unbounded collections would build reports of thousands of results per
    ///     case, which buys nothing an interleaving of a handful does not already show.
    /// </summary>
    private static Gen<IReadOnlyList<IReadOnlyList<ResultShape>>> ReportShape()
    {
        return Gen.Choose(0, 4)
            .SelectMany(runCount => ResultShapes().ListOf(runCount))
            .Select(runs => (IReadOnlyList<IReadOnlyList<ResultShape>>)runs.ToList());
    }

    private static Gen<IReadOnlyList<ResultShape>> ResultShapes()
    {
        return Gen.Choose(0, 6)
            .SelectMany(count => Gen.Elements(Enum.GetValues<ResultShape>()).ListOf(count))
            .Select(shapes => (IReadOnlyList<ResultShape>)shapes.ToList());
    }

    /// <summary>
    ///     The SARIF for <paramref name="runs" />, and the rule ids of the results that must survive it. Ids
    ///     are stamped in document order, so asserting on them asserts on order as well as on membership.
    /// </summary>
    private static (string Json, IReadOnlyList<string> LocatedRuleIds) BuildReport(
        IReadOnlyList<IReadOnlyList<ResultShape>> runs)
    {
        List<string> locatedRuleIds = [];
        List<Dictionary<string, object?>> runObjects = [];
        var index = 0;

        foreach (IReadOnlyList<ResultShape> run in runs)
        {
            List<Dictionary<string, object?>> results = [];
            foreach (ResultShape shape in run)
            {
                var ruleId = $"R{index++}";
                if (shape == ResultShape.Located) locatedRuleIds.Add(ruleId);

                results.Add(BuildResult(ruleId, shape));
            }

            runObjects.Add(new Dictionary<string, object?> { ["results"] = results });
        }

        var report = new Dictionary<string, object?> { ["runs"] = runObjects };

        return (JsonSerializer.Serialize(report), locatedRuleIds);
    }

    private static Dictionary<string, object?> BuildResult(string ruleId, ResultShape shape)
    {
        Dictionary<string, object?> result = new()
        {
            ["ruleId"] = ruleId,
            ["level"] = "warning",
            ["message"] = new Dictionary<string, object?> { ["text"] = ruleId }
        };

        // The absent case is the one that must leave the key out entirely rather than write a null.
        if (shape != ResultShape.NoLocationsProperty) result["locations"] = BuildLocations(shape);

        return result;
    }

    private static List<Dictionary<string, object?>> BuildLocations(ResultShape shape)
    {
        return shape switch
        {
            ResultShape.EmptyLocations => [],
            ResultShape.NoPhysicalLocation => [new Dictionary<string, object?>()],
            ResultShape.NoArtifactLocation =>
            [
                new Dictionary<string, object?> { ["physicalLocation"] = new Dictionary<string, object?>() }
            ],
            ResultShape.NoUri =>
            [
                new Dictionary<string, object?>
                {
                    ["physicalLocation"] = new Dictionary<string, object?>
                    {
                        ["artifactLocation"] = new Dictionary<string, object?>()
                    }
                }
            ],
            _ =>
            [
                new Dictionary<string, object?>
                {
                    ["physicalLocation"] = new Dictionary<string, object?>
                    {
                        ["artifactLocation"] = new Dictionary<string, object?> { ["uri"] = "src/Sample.cs" },
                        ["region"] = new Dictionary<string, object?> { ["startLine"] = 1, ["endLine"] = 2 }
                    }
                }
            ]
        };
    }

    /// <summary>The ways a result can fail to carry a usable first location, each of which must drop it.</summary>
    private enum ResultShape
    {
        Located,
        NoLocationsProperty,
        EmptyLocations,
        NoPhysicalLocation,
        NoArtifactLocation,
        NoUri
    }
}