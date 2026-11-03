using System;
using System.Reflection;

namespace DerethWatch
{
    // Read-only window onto Virindi Tank, late-bound so DerethWatch still loads without VTank.
    // VTank's file is utank2-i.dll but the assembly is "uTank2". Its public GetExternalInterface()
    // returns null, and the cExternalInterfaceTrustedRelay ctor only grants a permission level
    // when VTank itself is the caller, so build the relay and set its permission field directly.
    // DerethWatch only reads: MacroEnabled, CurrentMetaState, nav profile + waypoint, EnableNav.
    internal static class VTank
    {
        static object relay;
        internal static string Error = "not looked up yet";

        static object Relay()
        {
            if (relay != null) return relay;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type pc;
                try { pc = asm.GetType("uTank2.PluginCore"); } catch { continue; }
                if (pc == null) continue;
                Type relayT = pc.GetNestedType("cExternalInterfaceTrustedRelay", BindingFlags.Public | BindingFlags.NonPublic);
                Type permT = asm.GetType("uTank2.eExternalsPermissionLevel");
                if (relayT != null && permT != null)
                {
                    ConstructorInfo ctor = relayT.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { permT }, null);
                    object perm = Enum.ToObject(permT, 15);
                    if (ctor != null) relay = ctor.Invoke(new object[] { perm });
                    if (relay != null)
                        foreach (FieldInfo pf in relayT.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (pf.FieldType == permT) pf.SetValue(relay, perm);
                }
                Error = relay == null ? "could not create VTank interface" : "";
                return relay;
            }
            Error = "VTank (uTank2) assembly not loaded";
            return null;
        }

        static object Prop(string name)
        {
            try
            {
                object r = Relay();
                if (r == null) return null;
                PropertyInfo p = r.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) { Error = "property " + name + " not found"; return null; }
                return p.GetValue(r, null);
            }
            catch (Exception ex)
            {
                relay = null;
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Error = name + ": " + inner.GetType().Name + " " + inner.Message;
                return null;
            }
        }

        internal static object Get(string name) { return Prop(name); }
        internal static bool? MacroEnabled { get { object v = Prop("MacroEnabled"); return v is bool ? (bool?)(bool)v : null; } }
        internal static string MetaState { get { return Prop("CurrentMetaState") as string; } }

        // A VTank option by name, e.g. "EnableNav" (the Navigation switch). null = unknown.
        internal static object Setting(string name)
        {
            try
            {
                object r = Relay();
                if (r == null) return null;
                MethodInfo m = r.GetType().GetMethod("GetSetting", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                return m == null ? null : m.Invoke(r, new object[] { name });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return null; }
        }
        internal static bool? NavEnabled { get { object v = Setting("EnableNav"); return v is bool ? (bool?)(bool)v : null; } }

        // "<route file> wp <index>/<count>" - which waypoint VTank is heading to
        internal static string Nav
        {
            get
            {
                try
                {
                    object r = Relay();
                    if (r == null) return null;
                    MethodInfo m = r.GetType().GetMethod("GetNavProfile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    string route = m == null ? "?" : m.Invoke(r, null) as string;
                    return (string.IsNullOrEmpty(route) ? "(none)" : route) + " wp " + Prop("NavCurrent") + "/" + Prop("NavNumPoints");
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return null; }
            }
        }
    }
}
