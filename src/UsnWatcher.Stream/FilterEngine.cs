using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UsnWatcher.Core;

namespace UsnWatcher.Stream
{
    // Simple recursive-descent parser for a small filter language
    // Supported predicates:
    //   ext:.cs
    //   path:src/
    //   reason:CLOSE
    //   name:temp
    //   dir:true|false
    // Operators: AND, OR, NOT (AND has higher precedence than OR)
    public class FilterEngine
    {
        private readonly Func<UsnRecord, bool> _predicate;

        public FilterEngine(string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                _predicate = _ => true;
                return;
            }

            var tokens = Tokenize(filter!);
            var parser = new Parser(tokens);
            _predicate = parser.ParseExpression();
        }

        public bool Matches(UsnRecord record) => _predicate(record);

        private static string[] Tokenize(string s)
        {
            return s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private class Parser
        {
            private readonly string[] _tokens;
            private int _pos;

            public Parser(string[] tokens)
            {
                _tokens = tokens;
                _pos = 0;
            }

            public Func<UsnRecord, bool> ParseExpression()
            {
                return ParseOr();
            }

            // Or := And (OR And)*
            private Func<UsnRecord, bool> ParseOr()
            {
                var left = ParseAnd();
                while (MatchIgnoreCase("OR"))
                {
                    var right = ParseAnd();
                    var l = left; var r = right;
                    left = rec => l(rec) || r(rec);
                }
                return left;
            }

            // And := Unary (AND Unary)*
            private Func<UsnRecord, bool> ParseAnd()
            {
                var left = ParseUnary();
                while (MatchIgnoreCase("AND"))
                {
                    var right = ParseUnary();
                    var l = left; var r = right;
                    left = rec => l(rec) && r(rec);
                }
                return left;
            }

            // Unary := (NOT)? Primary
            private Func<UsnRecord, bool> ParseUnary()
            {
                if (MatchIgnoreCase("NOT"))
                {
                    var operand = ParseUnary();
                    return rec => !operand(rec);
                }
                return ParsePrimary();
            }

            // Primary := predicate
            private Func<UsnRecord, bool> ParsePrimary()
            {
                if (_pos >= _tokens.Length) return _ => true;
                var tok = _tokens[_pos++];

                // predicate form: key:value
                var idx = tok.IndexOf(':');
                if (idx <= 0) throw new FormatException($"Invalid predicate: '{tok}'");

                var key = tok.Substring(0, idx).ToLowerInvariant();
                var value = tok.Substring(idx + 1);

                switch (key)
                {
                    case "ext":
                        return BuildExtPredicate(value);
                    case "path":
                        return BuildPathPredicate(value);
                    case "reason":
                        return BuildReasonPredicate(value);
                    case "name":
                        return BuildNamePredicate(value);
                    case "dir":
                        return BuildDirPredicate(value);
                    default:
                        throw new FormatException($"Unknown predicate key: '{key}'");
                }
            }

            private static Func<UsnRecord, bool> BuildExtPredicate(string value)
            {
                var want = value.Trim();
                if (!want.StartsWith('.')) want = "." + want;
                return rec =>
                {
                    var fn = rec?.FileName ?? string.Empty;
                    var ext = Path.GetExtension(fn) ?? string.Empty;
                    return ext.Equals(want, StringComparison.OrdinalIgnoreCase);
                };
            }

            private static Func<UsnRecord, bool> BuildPathPredicate(string value)
            {
                var want = value.Trim();
                return rec =>
                {
                    var full = rec?.FullPath ?? string.Empty;
                    var name = rec?.FileName ?? string.Empty;
                    return (!string.IsNullOrEmpty(full) && full.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
                };
            }

            private static Func<UsnRecord, bool> BuildReasonPredicate(string value)
            {
                var want = value.Trim();
                return rec =>
                {
                    if (rec?.Reasons == null) return false;
                    return rec.Reasons.Any(r => string.Equals(r, want, StringComparison.OrdinalIgnoreCase));
                };
            }

            private static Func<UsnRecord, bool> BuildNamePredicate(string value)
            {
                var want = value.Trim();
                return rec =>
                {
                    var name = rec?.FileName ?? string.Empty;
                    return name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
                };
            }

            private static Func<UsnRecord, bool> BuildDirPredicate(string value)
            {
                var v = value.Trim();
                bool want = false;
                if (bool.TryParse(v, out var b)) want = b;
                else if (v == "1" || v.Equals("t", StringComparison.OrdinalIgnoreCase)) want = true;
                else if (v == "0" || v.Equals("f", StringComparison.OrdinalIgnoreCase)) want = false;

                return rec => rec?.IsDirectory == want;
            }

            private bool MatchIgnoreCase(string kw)
            {
                if (_pos >= _tokens.Length) return false;
                if (string.Equals(_tokens[_pos], kw, StringComparison.OrdinalIgnoreCase))
                {
                    _pos++; return true;
                }
                return false;
            }
        }
    }
}
