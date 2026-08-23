using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Globalization;

namespace JarvisAI.Infrastructure.Tools;

public sealed class CalculatorTool : ITool
{
    public string Name => "calculator";
    public string Description =>
        "Evaluates a math expression reliably and returns the numeric result. " +
        "Supports + - * / % ^, parentheses, decimal numbers, and functions: sqrt, cbrt, abs, round, floor, ceil, " +
        "min, max, sin, cos, tan, asin, acos, atan, log (base 10), ln (natural), exp, pi, e. " +
        "Use it instead of computing arithmetic yourself. Example: (2 + 3) * 4 ^ 2 + sqrt(144).";
    public string Category => "math";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("expression", "The math expression to evaluate", typeof(string), required: true)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!parameters.TryGetValue("expression", out var expression) || string.IsNullOrWhiteSpace(expression))
            {
                return Task.FromResult(ToolResult.Failed("Missing required parameter 'expression'."));
            }

            var value = new Evaluator(expression).Evaluate();
            var result = Math.Abs(value) < 1e-9 && value != 0 ? 0 : value;
            var text = result == Math.Floor(result) && Math.Abs(result) < 1e15
                ? ((long)result).ToString(CultureInfo.InvariantCulture)
                : result.ToString("0.############", CultureInfo.InvariantCulture);
            return Task.FromResult(ToolResult.Succeeded($"{expression} = {text}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed($"Expression invalide : {ex.Message}"));
        }
    }

    private sealed class Evaluator
    {
        private readonly string _input;
        private int _pos;

        public Evaluator(string input) => _input = input;

        public double Evaluate()
        {
            var result = ParseExpression();
            SkipWhiteSpace();
            if (_pos < _input.Length)
            {
                throw new FormatException($"Caractère inattendu à la position {_pos}");
            }
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                throw new OverflowException("Résultat invalide (NaN ou infini)");
            }
            return result;
        }

        private double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhiteSpace();
                if (Match('+')) value += ParseTerm();
                else if (Match('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhiteSpace();
                if (Match('*')) value *= ParseFactor();
                else if (Match('/'))
                {
                    var divisor = ParseFactor();
                    if (divisor == 0) throw new DivideByZeroException("Division par zéro");
                    value /= divisor;
                }
                else if (Match('%')) value %= ParseFactor();
                else return value;
            }
        }

        private double ParseFactor()
        {
            SkipWhiteSpace();
            var value = ParseUnary();
            SkipWhiteSpace();
            if (Match('^')) value = Math.Pow(value, ParseUnary());
            return value;
        }

        private double ParseUnary()
        {
            SkipWhiteSpace();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhiteSpace();

            if (Match('('))
            {
                var value = ParseExpression();
                SkipWhiteSpace();
                if (!Match(')')) throw new FormatException("Parenthèse fermante manquante");
                return value;
            }

            if (char.IsDigit(_pos < _input.Length ? _input[_pos] : '\0') || Peek() == '.')
            {
                return ParseNumber();
            }

            var name = ParseIdentifier();
            if (name is null)
            {
                throw new FormatException($"Caractère inattendu '{(_pos < _input.Length ? _input[_pos] : '?')}' à la position {_pos}");
            }

            SkipWhiteSpace();
            var hasParenthesis = Peek() == '(';

            switch (name.ToLowerInvariant())
            {
                case "pi": return Math.PI;
                case "e": return Math.E;
                case "sqrt": return Math.Sqrt(ParseArgument(hasParenthesis));
                case "cbrt": return Math.Cbrt(ParseArgument(hasParenthesis));
                case "abs": return Math.Abs(ParseArgument(hasParenthesis));
                case "round": return Math.Round(ParseArgument(hasParenthesis));
                case "floor": return Math.Floor(ParseArgument(hasParenthesis));
                case "ceil": return Math.Ceiling(ParseArgument(hasParenthesis));
                case "sin": return Math.Sin(ParseArgument(hasParenthesis));
                case "cos": return Math.Cos(ParseArgument(hasParenthesis));
                case "tan": return Math.Tan(ParseArgument(hasParenthesis));
                case "asin": return Math.Asin(ParseArgument(hasParenthesis));
                case "acos": return Math.Acos(ParseArgument(hasParenthesis));
                case "atan": return Math.Atan(ParseArgument(hasParenthesis));
                case "log": return Math.Log10(ParseArgument(hasParenthesis));
                case "ln": return Math.Log(ParseArgument(hasParenthesis));
                case "exp": return Math.Exp(ParseArgument(hasParenthesis));
                case "min":
                case "max":
                    var args = ParseArguments(hasParenthesis);
                    if (args.Length == 0) throw new FormatException($"{name} nécessite au moins 1 argument");
                    return name == "min" ? args.Min() : args.Max();
                default:
                    throw new FormatException($"Fonction inconnue '{name}'");
            }
        }

        private double ParseArgument(bool hasParenthesis)
        {
            if (!hasParenthesis) return ParseUnary();
            SkipWhiteSpace();
            if (!Match('(')) throw new FormatException("Parenthèse attendue");
            var value = ParseExpression();
            SkipWhiteSpace();
            if (!Match(')')) throw new FormatException("Parenthèse fermante manquante");
            return value;
        }

        private double[] ParseArguments(bool hasParenthesis)
        {
            if (!hasParenthesis) return new[] { ParseUnary() };
            SkipWhiteSpace();
            if (!Match('(')) throw new FormatException("Parenthèse attendue");
            var values = new List<double>();
            SkipWhiteSpace();
            if (Peek() != ')')
            {
                while (true)
                {
                    values.Add(ParseExpression());
                    SkipWhiteSpace();
                    if (Match(',')) continue;
                    break;
                }
            }
            if (!Match(')')) throw new FormatException("Parenthèse fermante manquante");
            return values.ToArray();
        }

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < _input.Length && (char.IsDigit(_input[_pos]) || _input[_pos] == '.'))
                _pos++;
            var text = _input[start.._pos];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Nombre invalide '{text}'");
            return value;
        }

        private string? ParseIdentifier()
        {
            var start = _pos;
            while (_pos < _input.Length && (char.IsLetter(_input[_pos]) || _input[_pos] == '_'))
                _pos++;
            return _pos > start ? _input[start.._pos] : null;
        }

        private char Peek() => _pos < _input.Length ? _input[_pos] : '\0';

        private bool Match(char c)
        {
            if (_pos < _input.Length && _input[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void SkipWhiteSpace()
        {
            while (_pos < _input.Length && char.IsWhiteSpace(_input[_pos]))
                _pos++;
        }
    }
}
