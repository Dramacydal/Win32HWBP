﻿using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WhiteMagic.Patterns
{
    public static class RegexExtensions
    {
        public static Match Match(this IEnumerable<byte> Data, MemoryPattern Pattern)
            => Pattern.Match(PatternHelper.BytesToString(Data.ToArray()));

        public static MatchCollection Matches(this IEnumerable<byte> Data, MemoryPattern Pattern)
            => Pattern.Matches(PatternHelper.BytesToString(Data.ToArray()));
    }
}
