using System;
using System.Globalization;
using System.Reflection;

namespace RdpTabs
{
    /// <summary>
    /// Late-bound (IDispatch) access to the RDP ActiveX control.
    ///
    /// Why not strongly typed interop: generating MSTSCLib interop needs tlbimp/aximp (not installed here)
    /// or an SDK-style project's COMReference. Reflection-based late binding lets net48 and net8 share one
    /// source tree with zero dependencies.
    /// The cost: only dispinterface members are reachable -- IMsRdpClientNonScriptable* (pure vtable) and
    /// the event callbacks are not, which is why connection state is polled from the Connected property
    /// (see RdpSessionControl).
    /// </summary>
    internal static class Com
    {
        private const BindingFlags GetFlags = BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags SetFlags = BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags CallFlags = BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance;

        public static object Get(object target, string name)
        {
            if (target == null) throw new ArgumentNullException("target");
            return target.GetType().InvokeMember(name, GetFlags, null, target, null);
        }

        public static bool TryGet(object target, string name, out object value)
        {
            value = null;
            if (target == null) return false;
            try
            {
                value = Get(target, name);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Set(object target, string name, object value)
        {
            if (target == null) throw new ArgumentNullException("target");
            target.GetType().InvokeMember(name, SetFlags, null, target, new object[] { value });
        }

        /// <summary>Sets a property; returns false instead of throwing when the control does not support it.</summary>
        public static bool TrySet(object target, string name, object value)
        {
            if (target == null) return false;
            try
            {
                Set(target, name, value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static object Call(object target, string name, params object[] args)
        {
            if (target == null) throw new ArgumentNullException("target");
            return target.GetType().InvokeMember(name, CallFlags, null, target, args ?? new object[0]);
        }

        public static bool TryCall(object target, string name, out object result, params object[] args)
        {
            result = null;
            if (target == null) return false;
            try
            {
                result = Call(target, name, args);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static int GetInt(object target, string name, int fallback)
        {
            object value;
            if (!TryGet(target, name, out value) || value == null) return fallback;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static string GetString(object target, string name, string fallback)
        {
            object value;
            if (!TryGet(target, name, out value) || value == null) return fallback;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>Returns the first property that exists (used for the AdvancedSettings9 -> ... -> AdvancedSettings fallback).</summary>
        public static object FirstAvailable(object target, out string usedName, params string[] names)
        {
            usedName = null;
            foreach (string name in names)
            {
                object value;
                if (TryGet(target, name, out value) && value != null)
                {
                    usedName = name;
                    return value;
                }
            }
            return null;
        }

        /// <summary>Unwraps the reflection/COM exception layers so we can show a readable message.</summary>
        public static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return ex;
        }
    }
}
