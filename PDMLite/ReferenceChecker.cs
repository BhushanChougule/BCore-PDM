using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Collections.Generic;
using System.IO;

namespace PDMLite
{
    public static class ReferenceChecker
    {
        public static List<string> GetBrokenReferences(ModelDoc2 doc)
        {
            var errors = new List<string>();

            int docType = doc.GetType();

            // For assemblies — check every component file exists on disk
            if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                AssemblyDoc asm = (AssemblyDoc)doc;
                object[] components = (object[])asm.GetComponents(false);

                if (components != null)
                {
                    foreach (object obj in components)
                    {
                        Component2 comp = obj as Component2;
                        if (comp == null) continue;

                        string compPath = comp.GetPathName();

                        if (!string.IsNullOrEmpty(compPath) && !File.Exists(compPath))
                        {
                            errors.Add("Missing file: " + Path.GetFileName(compPath));
                        }
                    }
                }
            }

            return errors;
        }
    }
}