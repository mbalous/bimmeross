using BmwCoder.Core.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BmwCoder.Cli.Tests;

[TestClass]
public class CafRulesTests
{
    private static CriteriaContext Ctx(int? build, params int[] criteria) => new(criteria.ToHashSet(), build);

    private static string Crit(int n) => $"boolean(//cat[@val<\"128\"][crit[@val={n}]])";

    [TestMethod]
    public void Evaluate_HandlesAndNotOrAndBuildThreshold()
    {
        string rule = $"{Crit(1496)} and {Crit(2919)} and boolean(//cat[@val=\"255\"][crit[@val<1707]])";
        Assert.IsTrue(CafRules.Evaluate(rule, Ctx(1612, 1496, 2919)));
        Assert.IsFalse(CafRules.Evaluate(rule, Ctx(1801, 1496, 2919)));
        Assert.IsFalse(CafRules.Evaluate(rule, Ctx(1612, 1496)));
        Assert.IsTrue(CafRules.Evaluate($"{Crit(1495)} and not({Crit(2121)} or {Crit(2919)})", Ctx(null, 1495)));
        Assert.IsFalse(CafRules.Evaluate($"{Crit(1495)} and not({Crit(2121)} or {Crit(2919)})", Ctx(null, 1495, 2919)));
        Assert.IsFalse(CafRules.Evaluate("false()", Ctx(null)));
        Assert.IsTrue(CafRules.Evaluate("true()", Ctx(null)));
    }

    [TestMethod]
    public void Resolve_PicksMatchingRuleThenDefaultPerSeries()
    {
        string xml = $@"<caf xmlns=""http://bmw.com/2005/psdz.data.caf""><data><datagroup address=""3000""><function>
<storageInfo><start>0</start><end>0</end><mask>11111111b</mask></storageInfo>
<condParam><bool isDefault=""false"" mainSeries=""F030"">{System.Security.SecurityElement.Escape($"{Crit(1496)} and {Crit(2919)}")}</bool><values><unitVal position=""0"">04</unitVal></values></condParam>
<condParam><bool isDefault=""true"" mainSeries=""F030""></bool><values><unitVal position=""0"">01</unitVal></values></condParam>
</function></datagroup></data></caf>";
        var rules = CafRules.Extract(CafDefinition.Parse("cafd_test.caf.000_000_001", "test", xml));
        var fn = rules.Single();
        Assert.AreEqual("3000/0", fn.Id);
        Assert.AreEqual("0:04", fn.Resolve("F030", Ctx(1612, 1496, 2919))!.Value);
        Assert.AreEqual("0:01", fn.Resolve("F030", Ctx(1612, 1496))!.Value);
        Assert.IsNull(fn.Resolve("F020", Ctx(1612, 1496)));
    }
}
