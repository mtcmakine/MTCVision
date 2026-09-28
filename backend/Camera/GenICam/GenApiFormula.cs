using System.Globalization;

namespace MTCVision.Backend.Camera.GenICam;

/// <summary>
/// Evaluator for the GenApi formula language used by SwissKnife and Converter nodes
/// (GenICam standard §Formula). Variables are the node's pVariable bindings, plus the
/// implicit TO/FROM of a converter.
/// </summary>
public static class GenApiFormula
{
    public static double Evaluate(string formula, IReadOnlyDictionary<string, double> variables)
    {
        var parser = new Parser(formula, variables);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        return value;
    }

    private sealed class Parser(string text, IReadOnlyDictionary<string, double> variables)
    {
        private int _position;

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_position < text.Length)
            {
                throw new FormatException($"GenApi formülü ayrıştırılamadı, '{text[_position..]}' beklenmiyordu: {text}");
            }
        }

        // Precedence climbs from here down; each level consumes the one below it.
        public double ParseExpression() => ParseConditional();

        private double ParseConditional()
        {
            var condition = ParseLogicalOr();
            if (!Match("?"))
            {
                return condition;
            }

            // Both branches must be parsed even though one is discarded — they share the cursor.
            var whenTrue = ParseConditional();
            Expect(":");
            var whenFalse = ParseConditional();
            return condition != 0 ? whenTrue : whenFalse;
        }

        private double ParseLogicalOr()
        {
            var left = ParseLogicalAnd();
            while (Match("||"))
            {
                var right = ParseLogicalAnd();
                left = (left != 0) || (right != 0) ? 1 : 0;
            }

            return left;
        }

        private double ParseLogicalAnd()
        {
            var left = ParseBitwiseOr();
            while (Match("&&"))
            {
                var right = ParseBitwiseOr();
                left = (left != 0) && (right != 0) ? 1 : 0;
            }

            return left;
        }

        private double ParseBitwiseOr()
        {
            var left = ParseBitwiseXor();
            while (PeekOperator("|", "||"))
            {
                Expect("|");
                left = ToInteger(left) | ToInteger(ParseBitwiseXor());
            }

            return left;
        }

        private double ParseBitwiseXor()
        {
            var left = ParseBitwiseAnd();
            while (Match("^"))
            {
                left = ToInteger(left) ^ ToInteger(ParseBitwiseAnd());
            }

            return left;
        }

        private double ParseBitwiseAnd()
        {
            var left = ParseEquality();
            while (PeekOperator("&", "&&"))
            {
                Expect("&");
                left = ToInteger(left) & ToInteger(ParseEquality());
            }

            return left;
        }

        private double ParseEquality()
        {
            var left = ParseRelational();
            while (true)
            {
                if (Match("<>"))
                {
                    left = Math.Abs(left - ParseRelational()) > double.Epsilon ? 1 : 0;
                }
                else if (PeekOperator("=", "=="))
                {
                    Expect("=");
                    left = Math.Abs(left - ParseRelational()) <= double.Epsilon ? 1 : 0;
                }
                else
                {
                    return left;
                }
            }
        }

        private double ParseRelational()
        {
            var left = ParseShift();
            while (true)
            {
                if (Match("<=")) left = left <= ParseShift() ? 1 : 0;
                else if (Match(">=")) left = left >= ParseShift() ? 1 : 0;
                else if (PeekOperator("<", "<<", "<>")) { Expect("<"); left = left < ParseShift() ? 1 : 0; }
                else if (PeekOperator(">", ">>")) { Expect(">"); left = left > ParseShift() ? 1 : 0; }
                else return left;
            }
        }

        private double ParseShift()
        {
            var left = ParseAdditive();
            while (true)
            {
                if (Match("<<")) left = ToInteger(left) << (int)ToInteger(ParseAdditive());
                else if (Match(">>")) left = ToInteger(left) >> (int)ToInteger(ParseAdditive());
                else return left;
            }
        }

        private double ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (Match("+")) left += ParseMultiplicative();
                else if (Match("-")) left -= ParseMultiplicative();
                else return left;
            }
        }

        private double ParseMultiplicative()
        {
            var left = ParsePower();
            while (true)
            {
                if (PeekOperator("*", "**")) { Expect("*"); left *= ParsePower(); }
                else if (Match("/")) left /= ParsePower();
                else if (Match("%")) left = ToInteger(left) % ToInteger(ParsePower());
                else return left;
            }
        }

        private double ParsePower()
        {
            var left = ParseUnary();
            // Right-associative: 2 ** 3 ** 2 is 2 ** 9.
            return Match("**") ? Math.Pow(left, ParsePower()) : left;
        }

        private double ParseUnary()
        {
            if (Match("-")) return -ParseUnary();
            if (Match("+")) return ParseUnary();
            if (Match("~")) return ~ToInteger(ParseUnary());
            if (Match("!")) return ParseUnary() == 0 ? 1 : 0;
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (_position >= text.Length)
            {
                throw new FormatException($"GenApi formülü beklenmedik şekilde bitti: {text}");
            }

            if (Match("("))
            {
                var value = ParseExpression();
                Expect(")");
                return value;
            }

            var c = text[_position];
            if (char.IsDigit(c) || (c == '.' && _position + 1 < text.Length && char.IsDigit(text[_position + 1])))
            {
                return ParseNumber();
            }

            if (char.IsLetter(c) || c == '_')
            {
                return ParseIdentifier();
            }

            throw new FormatException($"GenApi formülünde beklenmeyen karakter '{c}': {text}");
        }

        private double ParseNumber()
        {
            var start = _position;
            if (text[_position] == '0' && _position + 1 < text.Length && (text[_position + 1] is 'x' or 'X'))
            {
                _position += 2;
                var hexStart = _position;
                while (_position < text.Length && Uri.IsHexDigit(text[_position]))
                {
                    _position++;
                }

                return (double)ulong.Parse(text[hexStart.._position], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] == '.'))
            {
                _position++;
            }

            // Exponent form, e.g. 1.5e-3.
            if (_position < text.Length && (text[_position] is 'e' or 'E'))
            {
                _position++;
                if (_position < text.Length && (text[_position] is '+' or '-'))
                {
                    _position++;
                }

                while (_position < text.Length && char.IsDigit(text[_position]))
                {
                    _position++;
                }
            }

            return double.Parse(text[start.._position], CultureInfo.InvariantCulture);
        }

        private double ParseIdentifier()
        {
            var start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_'))
            {
                _position++;
            }

            var name = text[start.._position];

            SkipWhitespace();
            if (_position < text.Length && text[_position] == '(')
            {
                return ParseFunctionCall(name);
            }

            if (variables.TryGetValue(name, out var value))
            {
                return value;
            }

            return name.ToUpperInvariant() switch
            {
                "PI" => Math.PI,
                "E" => Math.E,
                "TRUE" => 1,
                "FALSE" => 0,
                _ => throw new KeyNotFoundException($"GenApi formülündeki '{name}' değişkeni bağlanmamış: {text}"),
            };
        }

        private double ParseFunctionCall(string name)
        {
            Expect("(");
            var arguments = new List<double> { ParseExpression() };
            while (Match(","))
            {
                arguments.Add(ParseExpression());
            }

            Expect(")");

            var a = arguments[0];
            return name.ToUpperInvariant() switch
            {
                "NEG" => -a,
                "SIN" => Math.Sin(a),
                "COS" => Math.Cos(a),
                "TAN" => Math.Tan(a),
                "ASIN" => Math.Asin(a),
                "ACOS" => Math.Acos(a),
                "ATAN" => Math.Atan(a),
                "ABS" => Math.Abs(a),
                "EXP" => Math.Exp(a),
                "LN" => Math.Log(a),
                "LG" => Math.Log10(a),
                "SQRT" => Math.Sqrt(a),
                "TRUNC" => Math.Truncate(a),
                "FLOOR" => Math.Floor(a),
                "CEIL" => Math.Ceiling(a),
                "SGN" => Math.Sign(a),
                "ROUND" => Math.Round(a, arguments.Count > 1 ? (int)arguments[1] : 0, MidpointRounding.AwayFromZero),
                _ => throw new NotSupportedException($"GenApi formülündeki '{name}' fonksiyonu desteklenmiyor: {text}"),
            };
        }

        private static long ToInteger(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

        private void SkipWhitespace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private bool Match(string token)
        {
            SkipWhitespace();
            if (string.CompareOrdinal(text, _position, token, 0, token.Length) != 0)
            {
                return false;
            }

            _position += token.Length;
            return true;
        }

        /// <summary>
        /// True when <paramref name="token"/> is next but is not the start of any longer operator
        /// it prefixes — '&amp;' that is really '&amp;&amp;', or '&lt;' that is really '&lt;&gt;'.
        /// </summary>
        private bool PeekOperator(string token, params string[] longerOperators)
        {
            SkipWhitespace();
            return string.CompareOrdinal(text, _position, token, 0, token.Length) == 0
                && longerOperators.All(longer => string.CompareOrdinal(text, _position, longer, 0, longer.Length) != 0);
        }

        private void Expect(string token)
        {
            if (!Match(token))
            {
                throw new FormatException($"GenApi formülünde '{token}' bekleniyordu: {text}");
            }
        }
    }
}
