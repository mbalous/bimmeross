using System.Globalization;
using System.Text.RegularExpressions;

namespace BmwCoder.Core.Workspace;

/// <summary>Vehicle facts a CAF selection rule is evaluated against.</summary>
public sealed record CriteriaContext(IReadOnlySet<int> Criteria, int? BuildCriterion);

/// <summary>One selectable value of a CAF function plus the rule that selects it.</summary>
public sealed record CafRuleOption(string Value, string? Expression, bool IsDefault);

public sealed record CafFunctionRules(string Id, int Block, int Ordinal, int Start, int End, string Mask,
    string? DefaultValue, IReadOnlyDictionary<string, IReadOnlyList<CafRuleOption>> BySeries)
{
    /// <summary>Returns the value the CAF would choose for the vehicle, or null if no rule applies.</summary>
    public CafRuleOption? Resolve(string series, CriteriaContext context)
    {
        if (!BySeries.TryGetValue(series, out var options))
            return null;
        foreach (var option in options)
        {
            if (!option.IsDefault && option.Expression != null && CafRules.Evaluate(option.Expression, context))
                return option;
        }
        return options.FirstOrDefault(o => o.IsDefault) ??
            (DefaultValue == null ? null : new CafRuleOption(DefaultValue, null, true));
    }
}

public static partial class CafRules
{
    public static IReadOnlyList<CafFunctionRules> Extract(CafDefinition definition)
    {
        var groups = new Dictionary<int, int>();
        var functions = new SortedDictionary<(int Group, int Func), FunctionBuilder>();
        foreach (var field in definition.Fields)
        {
            string path = NamespaceRegex().Replace(field.Path, string.Empty);
            var dg = DatagroupAddressRegex().Match(path);
            if (dg.Success)
            {
                groups[int.Parse(dg.Groups[1].Value, CultureInfo.InvariantCulture)] =
                    int.Parse(field.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                continue;
            }

            var m = FunctionPathRegex().Match(path);
            if (!m.Success)
                continue;
            var key = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            if (!functions.TryGetValue(key, out var builder))
                functions[key] = builder = new FunctionBuilder();
            builder.Add(m.Groups[3].Value, field.Value);
        }

        var result = new List<CafFunctionRules>();
        foreach (var ((group, func), builder) in functions)
        {
            if (!groups.TryGetValue(group, out int block) || builder.Start == null || builder.End == null)
                continue;
            var bySeries = builder.BuildOptions();
            if (bySeries.Count == 0 && builder.DefaultValue == null)
                continue;
            result.Add(new CafFunctionRules($"{block:X}/{func - 1}", block, func - 1, builder.Start.Value, builder.End.Value,
                builder.Mask ?? "11111111b", builder.DefaultValue, bySeries));
        }
        return result;
    }

    public static bool Evaluate(string expression, CriteriaContext context)
    {
        var atoms = new List<(string Cat, string CatOp, int Crit, string CritOp)>();
        string text = AtomRegex().Replace(expression, match =>
        {
            atoms.Add((match.Groups["cat"].Value, match.Groups["catop"].Value,
                int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture), match.Groups["op"].Value));
            return $" A{atoms.Count - 1} ";
        });
        var tokens = TokenRegex().Matches(text).Select(t => t.Value).ToList();
        int pos = 0;
        bool EvalAtom(int index)
        {
            var (cat, catOp, crit, critOp) = atoms[index];
            if (cat == "128" && catOp == "<")
                return critOp == "=" && context.Criteria.Contains(crit);
            if (cat == "255" && catOp == "=" && context.BuildCriterion is int build)
                return critOp switch { "<" => build < crit, ">" => build > crit, ">=" => build >= crit, "<=" => build <= crit, _ => build == crit };
            return false;
        }

        bool Or()
        {
            bool value = And();
            while (pos < tokens.Count && tokens[pos] == "or") { pos++; value |= And(); }
            return value;
        }
        bool And()
        {
            bool value = Unary();
            while (pos < tokens.Count && tokens[pos] == "and") { pos++; value &= Unary(); }
            return value;
        }
        bool Unary()
        {
            string token = tokens[pos++];
            switch (token)
            {
                case "(":
                {
                    bool inner = Or();
                    pos++;
                    return inner;
                }
                case "not":
                case "boolean":
                {
                    pos++;
                    bool inner = Or();
                    pos++;
                    return token == "not" ? !inner : inner;
                }
                case "true":
                case "false":
                    pos += 2;
                    return token == "true";
                default:
                    if (token.Length > 1 && token[0] == 'A' && int.TryParse(token.AsSpan(1), out int index))
                        return EvalAtom(index);
                    throw new FormatException($"Unsupported token '{token}' in CAF rule '{expression}'.");
            }
        }

        if (tokens.Count == 0)
            return false;
        bool result = Or();
        if (pos != tokens.Count)
            throw new FormatException($"Unparsed trailing tokens in CAF rule '{expression}'.");
        return result;
    }

    private sealed class FunctionBuilder
    {
        private readonly SortedDictionary<int, ParamBuilder> _params = new();
        public int? Start { get; private set; }
        public int? End { get; private set; }
        public string? Mask { get; private set; }
        public string? DefaultValue { get; private set; }

        public void Add(string rest, string value)
        {
            switch (rest)
            {
                case "storageInfo[1]/start[1]": Start = int.Parse(value, CultureInfo.InvariantCulture); return;
                case "storageInfo[1]/end[1]": End = int.Parse(value, CultureInfo.InvariantCulture); return;
                case "storageInfo[1]/mask[1]": Mask = value; return;
            }
            var def = DefValueRegex().Match(rest);
            if (def.Success)
            {
                DefaultValue = (DefaultValue == null ? string.Empty : DefaultValue + " ") + $"{int.Parse(def.Groups[1].Value, CultureInfo.InvariantCulture) - 1}:{value}";
                return;
            }
            var m = CondRegex().Match(rest);
            if (!m.Success)
                return;
            int index = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
            if (!_params.TryGetValue(index, out var p))
                _params[index] = p = new ParamBuilder();
            string tail = m.Groups["tail"].Value;
            var bool2 = BoolRegex().Match(tail);
            if (bool2.Success)
            {
                int bi = int.Parse(bool2.Groups[1].Value, CultureInfo.InvariantCulture);
                var b = p.Bools.TryGetValue(bi, out var existing) ? existing : p.Bools[bi] = new BoolBuilder();
                if (bool2.Groups[2].Value == "@isDefault") b.IsDefault = value == "true";
                else if (bool2.Groups[2].Value == "@mainSeries") b.Series = value;
                else b.Expression = value;
                return;
            }
            var unit = UnitRegex().Match(tail);
            if (unit.Success)
                p.Units[int.Parse(unit.Groups[1].Value, CultureInfo.InvariantCulture) - 1] = value;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<CafRuleOption>> BuildOptions()
        {
            var map = new Dictionary<string, List<CafRuleOption>>();
            foreach (var p in _params.Values)
            {
                if (p.Units.Count == 0)
                    continue;
                string value = string.Join(' ', p.Units.OrderBy(u => u.Key).Select(u => $"{u.Key}:{u.Value}"));
                foreach (var b in p.Bools.Values.Where(b => b.Series != null))
                {
                    if (!map.TryGetValue(b.Series!, out var list))
                        map[b.Series!] = list = new List<CafRuleOption>();
                    list.Add(new CafRuleOption(value, string.IsNullOrWhiteSpace(b.Expression) ? null : b.Expression, b.IsDefault));
                }
            }
            return map.ToDictionary(k => k.Key, k => (IReadOnlyList<CafRuleOption>)k.Value);
        }
    }

    private sealed class ParamBuilder
    {
        public SortedDictionary<int, BoolBuilder> Bools { get; } = new();
        public Dictionary<int, string> Units { get; } = new();
    }

    private sealed class BoolBuilder
    {
        public bool IsDefault { get; set; }
        public string? Series { get; set; }
        public string? Expression { get; set; }
    }

    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex NamespaceRegex();
    [GeneratedRegex(@"^/caf\[1\]/data\[1\]/datagroup\[(\d+)\]/@address$")] private static partial Regex DatagroupAddressRegex();
    [GeneratedRegex(@"^/caf\[1\]/data\[1\]/datagroup\[(\d+)\]/function\[(\d+)\]/(.+)$")] private static partial Regex FunctionPathRegex();
    [GeneratedRegex(@"^defVal\[1\]/unitVal\[(\d+)\]$")] private static partial Regex DefValueRegex();
    [GeneratedRegex(@"^condParam\[(?<i>\d+)\]/(?<tail>.+)$")] private static partial Regex CondRegex();
    [GeneratedRegex(@"^bool\[(\d+)\](?:/(@isDefault|@mainSeries))?$")] private static partial Regex BoolRegex();
    [GeneratedRegex(@"^values\[1\]/unitVal\[(\d+)\]$")] private static partial Regex UnitRegex();
    [GeneratedRegex(@"//cat\[@val(?<catop>[<>=]+)""(?<cat>\d+)""\]\[crit\[@val(?<op>[<>=]+)(?<n>\d+)\]\]")] private static partial Regex AtomRegex();
    [GeneratedRegex(@"A\d+|\(|\)|and|or|not|boolean|true|false")] private static partial Regex TokenRegex();
}
