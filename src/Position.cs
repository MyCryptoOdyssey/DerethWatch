using System;
using System.Globalization;
using Decal.Adapter;

namespace DerethWatch
{
    // Where the character stands, as "0xLLLLCCCC x y z" (landcell + local coords from Decal).
    // Used by Crash resume: after a relaunch the server puts the character back where its
    // session ended; somewhere else (lifestone after a death, portaled) = don't resume.
    internal static class Position
    {
        // The server's last saved spot can lag the crash by a few seconds of running.
        internal const double SAME_PLACE_M = 50;

        internal static string Now()
        {
            try
            {
                var a = CoreManager.Current.Actions;
                return string.Format(CultureInfo.InvariantCulture, "0x{0:X8} {1:0.0} {2:0.0} {3:0.0}",
                    a.Landcell, a.LocationX, a.LocationY, a.LocationZ);
            }
            catch { return ""; }
        }

        // Metres between two saved positions; double.MaxValue if not comparable
        // (different dungeon, outdoors vs indoors, unreadable).
        internal static double Distance(string a, string b)
        {
            uint ca, cb; double xa, ya, za, xb, yb, zb;
            if (!Parse(a, out ca, out xa, out ya, out za) || !Parse(b, out cb, out xb, out yb, out zb)) return double.MaxValue;
            bool outA = (ca & 0xFFFF) < 0x100, outB = (cb & 0xFFFF) < 0x100;
            if (outA != outB) return double.MaxValue;
            if (outA)
            {
                // outdoors: landblocks are 192 m squares, x/y are local to the landblock
                xa += ((ca >> 24) & 0xFF) * 192.0; ya += ((ca >> 16) & 0xFF) * 192.0;
                xb += ((cb >> 24) & 0xFF) * 192.0; yb += ((cb >> 16) & 0xFF) * 192.0;
            }
            else if ((ca >> 16) != (cb >> 16)) return double.MaxValue;   // different dungeon
            double dx = xa - xb, dy = ya - yb, dz = za - zb;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static bool Parse(string s, out uint cell, out double x, out double y, out double z)
        {
            cell = 0; x = y = z = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string[] p = s.Split(' ');
            if (p.Length != 4 || !p[0].StartsWith("0x")) return false;
            return uint.TryParse(p[0].Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cell)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)
                && cell != 0;
        }
    }
}
