using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PDMLite
{
    public class ValidationResult
    {
        public bool IsValid => EmptyFields.Count == 0;
        public List<string> EmptyFields { get; set; } = new List<string>();
        public List<string> MissingFields { get; set; } = new List<string>();
    }

    public static class PropertyValidator
    {
        // Required properties (PartWeight is auto-filled so excluded).
        // PartType (Manufactured/Purchased) drives the assembly drawing-release
        // gate — Purchased parts with no drawing are skipped, Manufactured ones
        // are warned. Defaults to Manufactured in PropertyForm.
        public static readonly string[] RequiredProperties = new[]
        {
            "PartNo",
            "DrawingNo",
            "Description",
            "DrawnBy",
            "DrawnDate",
            "Material1",
            "FinishType",
            "Revision",
            "PartType"
        };

        // Check all required properties on the open document
        public static ValidationResult Validate(ModelDoc2 doc)
        {
            var result = new ValidationResult();
            // Drawings inherit properties from part — skip validation
            if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
                return result;
            var cpm = doc.Extension.get_CustomPropertyManager(((SolidWorks.Interop.sldworks.Configuration)doc.GetActiveConfiguration()).Name);

            foreach (string propName in RequiredProperties)
            {
                string val = "", resolvedVal = "";
                bool retVal = cpm.Get4(propName, false, out val, out resolvedVal);

                if (!retVal)
                {
                    result.MissingFields.Add(propName);
                    result.EmptyFields.Add(propName);
                }
                else if (string.IsNullOrWhiteSpace(resolvedVal))
                {
                    result.EmptyFields.Add(propName);
                }
            }

            return result;
        }

        // Get a single property value
        public static string GetProperty(ModelDoc2 doc, string propName)
        {
            try
            {
                string configName = "";
                if (doc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                {
                    var config = doc.GetActiveConfiguration()
                        as SolidWorks.Interop.sldworks.Configuration;
                    if (config != null) configName = config.Name;
                }
                var cpm = doc.Extension.get_CustomPropertyManager(configName);
                string val = "", resolvedVal = "";
                cpm.Get4(propName, false, out val, out resolvedVal);
                return resolvedVal ?? "";
            }
            catch { return ""; }
        }

        // Write a property value back to the document
        public static void SetProperty(ModelDoc2 doc, string propName, string value)
        {
            try
            {
                string configName = "";
                if (doc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                {
                    var config = doc.GetActiveConfiguration()
                        as SolidWorks.Interop.sldworks.Configuration;
                    if (config != null) configName = config.Name;
                }
                var cpm = doc.Extension.get_CustomPropertyManager(configName);
                cpm.Add3(propName,
                         (int)swCustomInfoType_e.swCustomInfoText,
                         value,
                         (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            }
            catch { }
        }

        // Returns all configuration names in the document (parts/assemblies only).
        // For drawings returns an empty list — drawings have no configurations.
        public static List<string> GetConfigNames(ModelDoc2 doc)
        {
            var names = new List<string>();
            try
            {
                if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING) return names;
                object raw = doc.GetConfigurationNames();
                string[] arr = raw as string[];
                if (arr != null) names.AddRange(arr);
            }
            catch { }
            return names;
        }

        // Read a single property from a specific named configuration.
        // Useful when the caller knows which config to target (e.g. during
        // multi-config save or per-config revision bump).
        public static string GetProperty(ModelDoc2 doc, string propName,
            string configName)
        {
            try
            {
                var cpm = doc.Extension.get_CustomPropertyManager(configName ?? "");
                string val = "", resolvedVal = "";
                cpm.Get4(propName, false, out val, out resolvedVal);
                return resolvedVal ?? "";
            }
            catch { return ""; }
        }

        // Write a property to a specific named configuration.
        public static void SetProperty(ModelDoc2 doc, string propName,
            string value, string configName)
        {
            try
            {
                var cpm = doc.Extension.get_CustomPropertyManager(configName ?? "");
                cpm.Add3(propName,
                         (int)swCustomInfoType_e.swCustomInfoText,
                         value,
                         (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            }
            catch { }
        }

        // Validate all configurations and return a dictionary of
        //   configName → list of missing/empty required field names
        // for every config that has at least one problem. An empty dict means
        // all configurations are complete. Only call on parts/assemblies.
        public static Dictionary<string, List<string>> ValidateAllConfigs(ModelDoc2 doc)
        {
            var result = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
                    return result;

                foreach (string cfgName in GetConfigNames(doc))
                {
                    var missing = new List<string>();
                    var cpm = doc.Extension.get_CustomPropertyManager(cfgName);
                    foreach (string propName in RequiredProperties)
                    {
                        string val = "", resolvedVal = "";
                        bool ok = cpm.Get4(propName, false, out val, out resolvedVal);
                        if (!ok || string.IsNullOrWhiteSpace(resolvedVal))
                            missing.Add(propName);
                    }
                    if (missing.Count > 0)
                        result[cfgName] = missing;
                }
            }
            catch { }
            return result;
        }

        // Auto-calculate PartWeight from SOLIDWORKS mass properties.
        //
        // Uses CreateMassProperty (the documented IMassProperty API), NOT the
        // old `MassProperty mp = doc.Extension.GetMassProperties2(...)`:
        // GetMassProperties2 returns a double[] — under embedded interop that
        // assignment compiled as a dynamic conversion that threw
        // RuntimeBinderException at runtime, was swallowed by the catch, and
        // PartWeight was silently never auto-filled (audit H5).
        // GetMassProperties2's array is kept as a FALLBACK, read correctly as
        // double[] (index 5 = mass in kg).
        public static void AutoFillWeight(ModelDoc2 doc)
        {
            try
            {
                int docType = doc.GetType();
                if (docType != (int)swDocumentTypes_e.swDocPART &&
                    docType != (int)swDocumentTypes_e.swDocASSEMBLY)
                    return;

                double massKg = double.NaN;

                try
                {
                    MassProperty mp =
                        doc.Extension.CreateMassProperty() as MassProperty;
                    if (mp != null)
                    {
                        // System (MKS) units so Mass is kilograms regardless
                        // of the document's display units.
                        try { mp.UseSystemUnits = true; } catch { }
                        massKg = mp.Mass;
                    }
                }
                catch { }

                if (double.IsNaN(massKg) || massKg <= 0)
                {
                    int status;
                    double[] vals = doc.Extension.GetMassProperties2(
                        1, out status, false) as double[];
                    // Layout: [0..2] centre of mass, [3] volume, [4] area,
                    // [5] mass (kg), [6..] moments of inertia.
                    if (vals != null && vals.Length > 5)
                        massKg = vals[5];
                }

                if (double.IsNaN(massKg) || massKg < 0) return;

                double massLbs = massKg * 2.20462;

                string weightStr = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F3} kg / {1:F3} lbs", massKg, massLbs);
                SetProperty(doc, "PartWeight", weightStr);
            }
            catch
            {
                // Non-fatal — skip if mass properties unavailable
            }
        }
        // Convert date properties from yyyy-MM-dd to MM/dd/yyyy format
        public static void FixDateFormats(ModelDoc2 doc)
        {
            try
            {
                string[] dateProps = { "DrawnDate", "CheckedDate" };
                foreach (string prop in dateProps)
                {
                    string val = GetProperty(doc, prop);
                    if (string.IsNullOrEmpty(val)) continue;

                    // Check if in old yyyy-MM-dd format. InvariantCulture on
                    // BOTH sides: a null culture parses — and "/" in a format
                    // string separates — with the machine's locale, so a
                    // non-US Windows locale wrote "06.13.2026"-style values.
                    if (DateTime.TryParseExact(val, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime dt))
                    {
                        // Convert to new format
                        SetProperty(doc, prop, dt.ToString("MM/dd/yyyy",
                            System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            catch { }
        }
    }

}