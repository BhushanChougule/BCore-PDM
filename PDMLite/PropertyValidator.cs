using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;

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
        // Your 9 required properties (PartWeight is auto-filled so excluded)
        public static readonly string[] RequiredProperties = new[]
        {
            "PartNo",
            "DrawingNo",
            "Description",
            "DrawnBy",
            "DrawnDate",
            "Material",
            "FinishType",
            "Revision"
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

        // Auto-calculate PartWeight from SOLIDWORKS mass properties
        public static void AutoFillWeight(ModelDoc2 doc)
        {
            try
            {
                int docType = doc.GetType();
                if (docType != (int)swDocumentTypes_e.swDocPART &&
                    docType != (int)swDocumentTypes_e.swDocASSEMBLY)
                    return;

                int status;
                MassProperty mp = doc.Extension.GetMassProperties2(1, out status, false);

                if (mp == null) return;

                double massKg = mp.Mass;
                double massLbs = massKg * 2.20462;

                string weightStr = $"{massKg:F3} kg / {massLbs:F3} lbs";
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

                    // Check if in old yyyy-MM-dd format
                    if (DateTime.TryParseExact(val, "yyyy-MM-dd",
                        null, System.Globalization.DateTimeStyles.None,
                        out DateTime dt))
                    {
                        // Convert to new format
                        SetProperty(doc, prop, dt.ToString("MM/dd/yyyy"));
                    }
                }
            }
            catch { }
        }
    }

}