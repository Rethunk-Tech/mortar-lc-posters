using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Posters;

internal static class Pick
{
    internal static (int Width, int Height) FitSize(int w, int h, int max)
    {
        int longest = Math.Max(w, h);
        if (longest <= max)
        {
            return (w, h);
        }
        return (Math.Max(1, w * max / longest), Math.Max(1, h * max / longest));
    }

    internal static IEnumerable<string> Order(IEnumerable<string> files, string root) =>
        files.OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ThenBy(f => Path.GetRelativePath(root, f), StringComparer.Ordinal);
}
