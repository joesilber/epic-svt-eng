// This file contains the entire script. It prompts for the files, dynamically finds the column indexes based on
// your exact header names, creates the new assembly, resolves configurations, places and names the instances,
// applies the rotation and translation, and locks them in place.

using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

class Program
{
    [STAThread] // Required for OpenFileDialog
    static void Main()
    {
        // 1. Prompt for input files
        string csvPath = GetFile("Select Pattern CSV File", "CSV Files (*.csv)|*.csv");
        if (string.IsNullOrEmpty(csvPath)) return;

        string subAssyPath = GetFile("Select Subassembly to Pattern", "SOLIDWORKS Assembly (*.sldasm)|*.sldasm");
        if (string.IsNullOrEmpty(subAssyPath)) return;

        string baseName = Path.GetFileNameWithoutExtension(subAssyPath);

        // 2. Read headers and dynamically map columns
        var lines = File.ReadAllLines(csvPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        var headers = lines[0].Split(',').Select(h => h.Trim()).ToList();

        int C(string name) => headers.IndexOf(name); // Helper to get column index
        
        if (C("type_id") < 0 || C("Rx_deg") < 0 || C("x_mm") < 0 || C("disk_id") < 0)
        {
            Console.WriteLine("CSV is missing required headers.");
            return;
        }

        // 3. Connect to SOLIDWORKS
        SldWorks swApp;
        try { swApp = (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
        catch { Console.WriteLine("SOLIDWORKS must be running first."); return; }

        // 4. Pre-load subassembly silently to read configurations
        int err = 0, warn = 0;
        var subModel = swApp.OpenDoc6(subAssyPath, (int)swDocumentTypes_e.swDocASSEMBLY, 
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        
        string[] availableConfigs = (string[])subModel.GetConfigurationNames();

        // 5. Create the new parent assembly 
        // (To use an active existing assembly instead, change this to: var asmDoc = (ModelDoc2)swApp.ActiveDoc;)
        string template = swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
        var asmDoc = (ModelDoc2)swApp.NewDocument(template, 0, 0, 0);
        var asm = (AssemblyDoc)asmDoc;
        var math = (MathUtility)swApp.GetMathUtility();

        // 6. Process the CSV rows
        asmDoc.FeatureManager.EnableFeatureTree = false; // Pause tree updates for speed
        try
        {
            for (int i = 1; i < lines.Length; i++)
            {
                var row = lines[i].Split(',');

                // Find matching configuration by checking if type_id is a substring
                string typeId = row[C("type_id")];
                string targetConfig = availableConfigs.FirstOrDefault(c => 
                    c.IndexOf(typeId, StringComparison.OrdinalIgnoreCase) >= 0) ?? availableConfigs[0];

                // Insert component
                var comp = asm.AddComponent5(subAssyPath, 
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, 
                    "", true, targetConfig, 0, 0, 0);

                if (comp == null) continue;

                // Rename instance in feature tree (SOLIDWORKS will still append <1>, <2> etc. to guarantee uniqueness)
                string diskId = row[C("disk_id")];
                string rowId = row[C("row_id")];
                string xId = row[C("x_id")];
                comp.Name2 = $"{baseName} Disk-{diskId} Row-{rowId} Position-{xId}";

                // Parse and convert units (deg to rad, mm to m)
                double rx = D(row[C("Rx_deg")]) * Math.PI / 180.0;
                double ry = D(row[C("Ry_deg")]) * Math.PI / 180.0;
                double rz = D(row[C("Rz_deg")]) * Math.PI / 180.0;
                
                double tx = D(row[C("x_mm")]) / 1000.0;
                double ty = D(row[C("y_mm")]) / 1000.0;
                double tz = D(row[C("z_mm")]) / 1000.0;

                // Build 16-element transform array (Rotation first, then translation)
                double cx = Math.Cos(rx), sx = Math.Sin(rx);
                double cy = Math.Cos(ry), sy = Math.Sin(ry);
                double cz = Math.Cos(rz), sz = Math.Sin(rz);

                double[] matrix = new double[16] {
                    cz*cy, cz*sy*sx - sz*cx, cz*sy*cx + sz*sx,
                    sz*cy, sz*sy*sx + cz*cx, sz*sy*cx - cz*sx,
                    -sy,   cy*sx,            cy*cx,
                    tx, ty, tz,
                    1, 0, 0, 0
                };

                // Apply transform
                comp.Transform2 = (MathTransform)math.CreateTransform(matrix);

                // Select the component and fix it in place
                comp.Select4(false, null, false);
                asmDoc.EditFix();
            }
        }
        finally
        {
            // Always turn the feature tree back on, even if it crashes
            asmDoc.FeatureManager.EnableFeatureTree = true; 
        }

        asmDoc.EditRebuild3();
        Console.WriteLine("Pattern generation complete!");
    }

    // Helper to open file dialogs
    static string GetFile(string title, string filter)
    {
        using (var ofd = new OpenFileDialog { Title = title, Filter = filter })
        {
            return ofd.ShowDialog() == DialogResult.OK ? ofd.FileName : null;
        }
    }

    // Helper to safely parse strings to doubles regardless of region settings
    static double D(string val) => double.Parse(val.Trim(), CultureInfo.InvariantCulture);
}
