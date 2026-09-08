using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RdpTabs
{
    /// <summary>
    /// Hosts the Remote Desktop ActiveX control that ships with Windows (mstscax.dll -- the same engine
    /// mstsc uses). The CLSID is probed at run time from the newest ProgID downwards; no hard-coded GUIDs.
    /// We use the NotSafeForScripting class (MsTscAx.MsTscAx*) because only it allows ClearTextPassword.
    /// </summary>
    internal sealed class RdpAxHost : AxHost
    {
        private static readonly string[] CandidateProgIds =
        {
            "MsTscAx.MsTscAx.13",
            "MsTscAx.MsTscAx.12",
            "MsTscAx.MsTscAx.11",
            "MsTscAx.MsTscAx.10",
            "MsTscAx.MsTscAx.9",
            "MsTscAx.MsTscAx.8",
            "MsTscAx.MsTscAx.7",
            "MsTscAx.MsTscAx"
        };

        private static string _clsid;
        private static string _progId;

        public RdpAxHost() : base(ResolveClsid())
        {
        }

        public static string ResolvedProgId
        {
            get
            {
                ResolveClsid();
                return _progId;
            }
        }

        public static string ResolvedClsid
        {
            get { return ResolveClsid(); }
        }

        /// <summary>The underlying COM object (IMsRdpClient*), or null before the handle exists.</summary>
        public object Ocx
        {
            get { return IsHandleCreated ? GetOcx() : null; }
        }

        /// <summary>
        /// Forces handle creation (which instantiates the OCX). CreateControl() does nothing while the
        /// window is not yet visible, so CreateHandle() is used as a fallback.
        /// </summary>
        public void EnsureCreated()
        {
            if (IsHandleCreated) return;
            CreateControl();
            if (!IsHandleCreated) CreateHandle();
        }

        private static string ResolveClsid()
        {
            if (_clsid != null) return _clsid;

            // The registry alone is not enough: on Windows 11 both the ProgID and CLSID of
            // MsTscAx.MsTscAx.13 are registered, yet mstscax.dll's class factory does not implement it and
            // CoCreateInstance returns CLASS_E_CLASSNOTAVAILABLE. So actually try to create each candidate
            // and take the first one that works.
            List<string> attempts = new List<string>();
            foreach (string progId in CandidateProgIds)
            {
                string clsid = ClsidFromRegistry(progId) ?? ClsidFromProgId(progId);
                if (clsid == null)
                {
                    attempts.Add(progId + ": not registered");
                    continue;
                }

                string error;
                if (!CanCreate(clsid, out error))
                {
                    attempts.Add(progId + " " + clsid + ": " + error);
                    continue;
                }

                _progId = progId;
                _clsid = clsid;
                return clsid;
            }

            throw new InvalidOperationException(
                "No usable Remote Desktop ActiveX control on this machine. This usually means mstscax.dll " +
                "is not registered; try running regsvr32 mstscax.dll as administrator.\n\nTried:\n" +
                string.Join("\n", attempts.ToArray()));
        }

        /// <summary>Actually creates the COM object once and releases it immediately.</summary>
        private static bool CanCreate(string clsid, out string error)
        {
            error = null;
            object instance = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid(clsid), false);
                if (type == null)
                {
                    error = "CLSID not registered";
                    return false;
                }
                instance = Activator.CreateInstance(type);
                return instance != null;
            }
            catch (Exception ex)
            {
                error = Com.Unwrap(ex).Message;
                return false;
            }
            finally
            {
                if (instance != null) Marshal.ReleaseComObject(instance);
            }
        }

        private static string ClsidFromRegistry(string progId)
        {
            try
            {
                object value = Registry.GetValue(@"HKEY_CLASSES_ROOT\" + progId + @"\CLSID", "", null);
                string text = value as string;
                Guid clsid;
                if (!string.IsNullOrEmpty(text) && Guid.TryParse(text, out clsid))
                    return clsid.ToString("B");
            }
            catch (Exception)
            {
                // If the registry read fails, fall back to ProgID activation
            }
            return null;
        }

        private static string ClsidFromProgId(string progId)
        {
            try
            {
                Type type = Type.GetTypeFromProgID(progId, false);
                if (type != null && type.GUID != Guid.Empty) return type.GUID.ToString("B");
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
